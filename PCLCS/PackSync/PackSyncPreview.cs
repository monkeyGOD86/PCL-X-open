using System.IO;

namespace PCLCS.PackSync;

public sealed class PackSyncCorruptStateException : IOException {
    public PackSyncCorruptStateException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class PackSyncState {
    [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonProperty("packId")] public Guid PackId { get; set; }
    [JsonProperty("releaseId")] public string? ReleaseId { get; set; }
    [JsonProperty("archiveSha512")] public string? ArchiveSha512 { get; set; }
    [JsonProperty("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
    [JsonProperty("baselineFiles")] public Dictionary<string, string>? BaselineFiles { get; set; }
    [JsonProperty("optionalSelections")] public List<string>? OptionalSelections { get; set; }
    [JsonProperty("lastTransactionId")] public string? LastTransactionId { get; set; }
}

public sealed class PackSyncPreviewResult {
    public PackSyncPackage Package { get; internal set; } = null!;
    public PackSyncState? State { get; internal set; }
    public IReadOnlyList<PackSyncDifference> Differences { get; internal set; } = Array.Empty<PackSyncDifference>();
    public IReadOnlyList<string> OptionalSelections { get; internal set; } = Array.Empty<string>();
    public string? StateWarning { get; internal set; }
    internal IReadOnlyDictionary<string, string> LocalFiles { get; set; } = new Dictionary<string, string>();
    internal string InstanceDirectory { get; set; } = "";
    internal string StateSnapshot { get; set; } = "";
}

/// <summary>Creates a read-only preview; never follows reparse points or modifies the instance.</summary>
public static class PackSyncPreview {
    public static PackSyncPreviewResult Create(string archivePath, string instanceDirectory,
        string gameDirectory, IDictionary<string, string> environment, IEnumerable<string> selectedOptionalPaths) {
        string root = Path.GetFullPath(instanceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string game = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(root, game, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("小队同步仅支持独立实例，请先启用版本隔离。");
        EnsureNoReparsePoints(root);
        var package = PackSyncReader.Read(archivePath);
        if (environment == null || package.Dependencies.Count != environment.Count ||
            package.Dependencies.Any(p => !environment.TryGetValue(p.Key, out var version) || version != p.Value))
            throw new InvalidDataException("游戏或加载器版本与小队包不一致，请导入到新实例。");
        string fingerprint = StateFingerprint(root);
        PackSyncState? state;
        string? warning = null;
        try { state = ReadState(root); }
        catch (PackSyncCorruptStateException ex) { state = null; warning = ex.Message; }
        if (fingerprint != StateFingerprint(root)) throw new IOException("小队状态在预览期间发生变化，请重新检查。");
        if (state != null && state.PackId != package.PackId)
            throw new InvalidDataException("该实例已绑定其他小队，请导入到新实例。");
        var optional = new HashSet<string>(selectedOptionalPaths ?? throw new ArgumentNullException(nameof(selectedOptionalPaths)), StringComparer.OrdinalIgnoreCase);
        foreach (string path in optional) {
            PackSyncPlanner.ValidatePath(path);
            if (!package.Files.Any(f => f.Optional && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"所选可选文件不存在：{path}");
        }
        var target = package.Files.Where(f => !f.Optional || optional.Contains(f.Path)).ToDictionary(f => f.Path, f => f.Sha512, StringComparer.OrdinalIgnoreCase);
        var baseline = state?.BaselineFiles ?? new Dictionary<string, string>();
        var local = Scan(root);
        return new PackSyncPreviewResult { Package = package, State = state, Differences = PackSyncPlanner.Compare(baseline, local, target), OptionalSelections = optional.ToArray(),
            LocalFiles = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(local), InstanceDirectory = root,
            StateSnapshot = fingerprint, StateWarning = warning };
    }

    /// <summary>Read the binding for an instance; malformed state is reported, never treated as unbound.</summary>
    public static PackSyncState? ReadState(string root) {
        root = Path.GetFullPath(root);
        string path = Path.Combine(root, "PCL", "PackSync", "state.json");
        EnsureNoReparsePoints(path);
        if (!File.Exists(path)) return null;
        try {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("小队状态文件过大。");
            using var reader = File.OpenText(path);
            using var json = new JsonTextReader(reader) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
            var obj = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (obj["schemaVersion"]?.Type != JTokenType.Integer) throw new InvalidDataException("小队状态缺少版本号。");
            if ((int)obj["schemaVersion"]! != 1) throw new NotSupportedException("小队状态版本不受支持，不能原地修改。");
            var state = obj.ToObject<PackSyncState>() ?? throw new InvalidDataException("小队状态为空。");
            if (state.PackId == Guid.Empty || state.BaselineFiles == null || state.OptionalSelections == null || state.Dependencies == null)
                throw new InvalidDataException("小队状态损坏，请重新核对。");
            // Validate hashes and case-insensitive duplicates before using the baseline.
            PackSyncPlanner.Compare(state.BaselineFiles, Array.Empty<KeyValuePair<string, string>>(), Array.Empty<KeyValuePair<string, string>>());
            foreach (string optional in state.OptionalSelections) PackSyncPlanner.ValidatePath(optional);
            return state;
        } catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is FormatException || ex is OverflowException || ex is ArgumentException) {
            throw new PackSyncCorruptStateException("小队状态损坏：将以空基线重新核对，本地额外文件不会自动移除。", ex);
        }
    }

    private static string StateFingerprint(string root) {
        string path = Path.Combine(root, "PCL", "PackSync", "state.json");
        EnsureNoReparsePoints(path);
        if (!File.Exists(path)) return "missing";
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("小队状态文件过大，无法安全核对。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PackSyncReader.Hash(stream);
    }

    private static Dictionary<string, string> Scan(string root) {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in new[] { "mods", "config", "defaultconfigs", "kubejs", "scripts" }) {
            string path = Path.Combine(root, folder);
            if (!Directory.Exists(path)) continue;
            EnsureNoReparsePoints(path);
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0) {
                string current = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(current)) {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"同步范围中存在链接：{entry}");
                    if ((attributes & FileAttributes.Directory) != 0) {
                        if (folder != "mods") pending.Push(entry);
                        continue;
                    }
                    if (folder == "mods" && !entry.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = entry.Substring(root.Length + 1).Replace('\\', '/');
                    PackSyncPlanner.ValidatePath(relative);
                    using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (result.ContainsKey(relative)) throw new InvalidDataException($"本地路径重复：{relative}");
                    result.Add(relative, PackSyncReader.Hash(input));
                }
            }
        }
        return result;
    }

    private static void EnsureNoReparsePoints(string path) {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"路径包含链接或重解析点：{current}");
            current = Path.GetDirectoryName(current);
        }
    }
}
