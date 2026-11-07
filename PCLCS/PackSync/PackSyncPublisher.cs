namespace PCLCS.PackSync;

public sealed class PackSyncPublisherSettings {
    [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonProperty("packId")] public Guid PackId { get; set; }
    [JsonProperty("sharedFiles")] public List<string> SharedFiles { get; set; } = new();
    [JsonProperty("selectedMods", NullValueHandling = NullValueHandling.Ignore)] public List<string>? SelectedMods { get; set; }
}

/// <summary>Persists publisher identity and individually selected shared configuration files.</summary>
public static class PackSyncPublisher {
    /// <summary>Enumerates candidate mods and shared files without following links.</summary>
    public static IReadOnlyList<string> EnumerateCandidates(string instanceDirectory) {
        string root = Path.GetFullPath(instanceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        CheckPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("实例目录不存在。");
        var result = new List<string>();
        foreach (string name in new[] { "mods", "config", "defaultconfigs", "kubejs", "scripts" }) {
            string directory = Path.Combine(root, name);
            if (!Directory.Exists(directory)) continue;
            CheckPath(directory);
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0) {
                foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop())) {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"同步范围包含链接：{entry}");
                    if ((attributes & FileAttributes.Directory) != 0) { if (name != "mods") pending.Push(entry); continue; }
                    if (name == "mods" && !entry.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = entry.Substring(root.Length + 1).Replace('\\', '/');
                    result.Add(PackSyncPlanner.ValidatePath(relative));
                }
            }
        }
        if (result.Count != result.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new InvalidDataException("候选文件路径重复。");
        return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static PackSyncPublisherSettings? Load(string instanceDirectory) {
        string path = SettingsPath(instanceDirectory);
        CheckPath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("维护者设置文件过大。");
        try {
            using var input = File.OpenText(path);
            using var reader = new JsonTextReader(input) { MaxDepth = 16, DateParseHandling = DateParseHandling.None };
            var json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if ((int?)json["schemaVersion"] != 1 || json["sharedFiles"] is not JArray) throw new InvalidDataException("维护者设置格式不受支持。");
            var result = json.ToObject<PackSyncPublisherSettings>() ?? throw new InvalidDataException("维护者设置为空。");
            Validate(result);
            return result;
        } catch (JsonException ex) { throw new InvalidDataException("维护者设置损坏。", ex); }
    }

    /// <summary>Pass a new UUID to create a team, or an explicitly chosen bound UUID to continue that team.</summary>
    public static void Save(string instanceDirectory, Guid packId, IEnumerable<string> sharedFiles) => Save(instanceDirectory, packId, sharedFiles, null);

    public static void Save(string instanceDirectory, Guid packId, IEnumerable<string> sharedFiles, IEnumerable<string>? selectedMods) {
        if (sharedFiles == null) throw new ArgumentNullException(nameof(sharedFiles));
        var settings = new PackSyncPublisherSettings { PackId = packId, SharedFiles = sharedFiles.ToList(), SelectedMods = selectedMods?.ToList() };
        Validate(settings);
        string path = SettingsPath(instanceDirectory);
        string folder = Path.GetDirectoryName(path)!;
        CheckPath(folder);
        Directory.CreateDirectory(folder);
        CheckPath(path);
        string temp = Path.Combine(folder, "publisher-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) {
                writer.Write(JsonConvert.SerializeObject(settings, Formatting.Indented));
                writer.Flush();
                stream.Flush(true);
            }
            CheckPath(path);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void Validate(PackSyncPublisherSettings settings) {
        if (settings.SchemaVersion != 1 || settings.PackId == Guid.Empty || settings.SharedFiles == null)
            throw new InvalidDataException("维护者设置无效。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in settings.SharedFiles) {
            string path = PackSyncPlanner.ValidatePath(item);
            if (path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) || !seen.Add(path))
                throw new InvalidDataException($"共享文件选择无效或重复：{path}");
        }
        if (settings.SelectedMods != null) foreach (string item in settings.SelectedMods) {
            string path = PackSyncPlanner.ValidatePath(item);
            if (!path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) || !seen.Add(path))
                throw new InvalidDataException($"Mod 选择无效或重复：{path}");
        }
    }

    private static string SettingsPath(string instanceDirectory) => Path.Combine(Path.GetFullPath(instanceDirectory), "PCL", "PackSync", "publisher.json");
    private static void CheckPath(string path) {
        string? current = path;
        while (!string.IsNullOrEmpty(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"维护者设置路径包含链接：{current}");
            current = Path.GetDirectoryName(current);
        }
    }
}
