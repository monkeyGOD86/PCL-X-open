using System.Text;

namespace PCLCS.Bedrock;

public sealed class BedrockRemovedAddon(string transactionPath, string backupPath) {
    public string TransactionPath { get; } = transactionPath;
    public string BackupPath { get; } = backupPath;
}

public sealed class BedrockRemovalInfo(string path, string state, string? originalPath, string? backupPath, string? error) {
    public string Path { get; } = path;
    public string State { get; } = state;
    public string? OriginalPath { get; } = originalPath;
    public string? BackupPath { get; } = backupPath;
    public string? Error { get; } = error;
}

/// <summary>Experimental removal by same-volume quarantine with a separate verified backup.</summary>
public static class BedrockAddonRemoval {
    private const string Prefix = ".pcl-addon-removal-";
    private static readonly string[] Categories = ["resource_packs", "behavior_packs", "development_resource_packs", "development_behavior_packs"];

    /// <summary>Lists path-based removal states; restoration independently validates the stored data.</summary>
    public static IReadOnlyList<BedrockRemovalInfo> List(BedrockDataRoot root) {
        string rootPath = NormalizeRoot(root);
        using var rootLock = BedrockRootLock.Acquire(rootPath);
        var result = new List<BedrockRemovalInfo>();
        foreach (string candidate in Directory.EnumerateDirectories(rootPath, Prefix + "*")) {
            if (result.Count >= 1024) throw new InvalidDataException("移除事务数量超过读取限制。");
            try {
                var (stage, destination, _, record) = ReadJournal(rootPath, candidate);
                string content = Path.Combine(stage, "content");
                bool original = Directory.Exists(destination), quarantined = Directory.Exists(content);
                string state = File.Exists(destination) || File.Exists(content) || original && quarantined ? "路径冲突" :
                    quarantined ? "已隔离" : original ? "原位置存在" : "内容缺失";
                result.Add(new(stage, state, destination, record.Value<string>("backupPath"), null));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or
                                       InvalidDataException or FormatException or InvalidCastException or OverflowException or ArgumentException) {
                result.Add(new(candidate, "记录不可读取", null, null, ex.Message));
            }
        }
        return result;
    }

    public static BedrockRemovedAddon Remove(BedrockDataRoot root, BedrockInstalledAddon selected,
        BedrockDirectoryPreview confirmed, string backupParent, IReadOnlyList<BedrockDataRoot> worldRoots) {
        string rootPath = NormalizeRoot(root);
        string source = Path.GetFullPath(selected.Path).TrimEnd(Path.DirectorySeparatorChar);
        string relative = RelativePackPath(rootPath, source);
        string backupRoot = Path.GetFullPath(backupParent).TrimEnd(Path.DirectorySeparatorChar);
        if (Contains(rootPath, backupRoot)) throw new InvalidDataException("附加包备份必须位于游戏数据目录之外。");
        BedrockDataRootFinder.EnsureNoReparsePoints(backupRoot);
        if (worldRoots.Count is < 1 or > 128) throw new InvalidDataException("必须确认用于世界引用检查的数据目录。");
        string[] lockPaths = worldRoots.Select(item => NormalizeRoot(item)).Concat([rootPath]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (root.SupportsWorlds && !worldRoots.Any(item => string.Equals(NormalizeRoot(item), rootPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("世界检查范围必须包含所选账户目录。");
        if (lockPaths.Any(path => Contains(path, backupRoot))) throw new InvalidDataException("备份不能位于检查范围内的游戏数据目录中。");
        using var rootLock = BedrockRootLock.AcquireAll(lockPaths);
        var match = BedrockAddonCatalog.Read(root).SingleOrDefault(item =>
            string.Equals(Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar), source, StringComparison.OrdinalIgnoreCase));
        if (match?.Manifest is null) throw new InvalidDataException("所选附加包已不存在或无法识别。");
        Verify(source, confirmed.Pack.Uuid, confirmed.Pack.Version, confirmed.Fingerprint);
        RequireUnused(root, worldRoots, confirmed.Pack.Uuid);
        string id = Guid.NewGuid().ToString("N");
        string backup = Path.Combine(backupRoot, "pcl-addon-backup-" + id);
        string transaction = Path.Combine(rootPath, Prefix + id);
        if (Directory.Exists(backup) || File.Exists(backup) || Directory.Exists(transaction) || File.Exists(transaction))
            throw new IOException("备份或事务目录已存在，请重试。");
        bool started = false;
        try {
            Directory.CreateDirectory(backup);
            started = true;
            BedrockDirectoryInstaller.CopyTree(source, backup);
            Verify(backup, confirmed.Pack.Uuid, confirmed.Pack.Version, confirmed.Fingerprint);
            Directory.CreateDirectory(transaction);
            var record = new JObject { ["schemaVersion"] = 1, ["id"] = id, ["relativePath"] = relative,
                ["uuid"] = confirmed.Pack.Uuid.ToString("D"), ["version"] = confirmed.Pack.Version,
                ["fingerprint"] = confirmed.Fingerprint, ["backupPath"] = backup };
            using (var file = new FileStream(Path.Combine(transaction, "journal.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                byte[] bytes = new UTF8Encoding(false).GetBytes(record.ToString(Formatting.None));
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            Verify(source, confirmed.Pack.Uuid, confirmed.Pack.Version, confirmed.Fingerprint);
            RequireUnused(root, worldRoots, confirmed.Pack.Uuid);
            BedrockDataRootFinder.EnsureNoReparsePoints(transaction);
            string content = Path.Combine(transaction, "content");
            Directory.Move(source, content);
            Verify(content, confirmed.Pack.Uuid, confirmed.Pack.Version, confirmed.Fingerprint);
            return new(transaction, backup);
        } catch (Exception ex) when (started) {
            throw new IOException("附加包移除未完成或需要检查；外部备份：" + backup + "，事务目录：" + transaction, ex);
        }
    }

    private static void RequireUnused(BedrockDataRoot target, IReadOnlyList<BedrockDataRoot> roots, Guid uuid) {
        var usage = BedrockWorldPackUsage.Check(roots, uuid);
        if (!usage.Complete) throw new InvalidDataException("部分世界无法检查，不能安全移除附加包。");
        if (usage.References.Count > 0) throw new InvalidDataException("所选范围中的世界仍引用此附加包，请先检查世界配置。");
        var globalRoots = roots.Concat([target]).GroupBy(item => Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        var global = BedrockGlobalPackUsage.Check(globalRoots, uuid);
        if (global.IncompletePaths.Count > 0) throw new InvalidDataException("候选全局资源配置无法检查，不能安全移除附加包。");
        if (global.References.Count > 0) throw new InvalidDataException("候选全局资源配置仍引用此附加包，请先检查游戏设置。");
    }

    public static string Restore(BedrockDataRoot root, string transaction, IReadOnlyList<BedrockDataRoot> scope) {
        string rootPath = NormalizeRoot(root);
        if (scope.Count is < 1 or > 128) throw new InvalidDataException("必须确认附加包冲突检查范围。");
        string[] paths = scope.Select(NormalizeRoot).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length || !paths.Contains(rootPath, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("检查范围必须包含所选目录且不能重复。");
        using var rootLock = BedrockRootLock.AcquireAll(paths);
        var (stage, destination, uuid, record) = ReadJournal(rootPath, transaction);
        string relative = record.Value<string>("relativePath")!;
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("原位置已被占用，不能覆盖。");
        string parent = Path.GetDirectoryName(destination)!;
        BedrockDataRootFinder.EnsureNoReparsePoints(parent);
        string content = Path.Combine(stage, "content");
        Verify(content, uuid, record.Value<string>("version")!, record.Value<string>("fingerprint")!);
        // An emptied version-container is expected to be unrecognized after its only child is moved.
        bool emptyVersionContainer = relative.Split('/').Length == 3 && !File.Exists(Path.Combine(parent, "manifest.json")) &&
            !Directory.EnumerateDirectories(parent).Any();
        foreach (var scopeRoot in scope) {
            var catalog = BedrockAddonCatalog.Read(scopeRoot);
            if (catalog.Any(item => item.Manifest is not null && item.Manifest.Uuid == uuid))
                throw new InvalidDataException("检查范围中已存在相同 UUID 的包，不能自动恢复。");
            bool targetRoot = string.Equals(NormalizeRoot(scopeRoot), rootPath, StringComparison.OrdinalIgnoreCase);
            if (catalog.Any(item => item.Manifest is null && !(targetRoot && emptyVersionContainer &&
                    string.Equals(Path.GetFullPath(item.Path), parent, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("现有包无法全部识别，无法检查冲突。");
        }
        BedrockDataRootFinder.EnsureNoReparsePoints(parent);
        BedrockDataRootFinder.EnsureNoReparsePoints(content);
        Directory.Move(content, destination);
        try { Verify(destination, uuid, record.Value<string>("version")!, record.Value<string>("fingerprint")!); }
        catch (Exception ex) { throw new IOException("恢复后的内容发生变化，请检查原位置与备份：" + destination, ex); }
        return destination;
    }

    private static (string Stage, string Destination, Guid Uuid, JObject Record) ReadJournal(string rootPath, string transaction) {
        string stage = Path.GetFullPath(transaction).TrimEnd(Path.DirectorySeparatorChar);
        string name = Path.GetFileName(stage);
        if (!string.Equals(Path.GetDirectoryName(stage), rootPath, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(Prefix.Length), "N", out _))
            throw new InvalidDataException("移除事务目录无效。");
        BedrockDataRootFinder.EnsureNoReparsePoints(stage);
        string journal = Path.Combine(stage, "journal.json");
        BedrockDataRootFinder.EnsureNoReparsePoints(journal);
        JObject record;
        using (var file = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            if (file.Length > 65536) throw new InvalidDataException("事务记录过大。");
            using var reader = new StreamReader(file, new UTF8Encoding(false, true));
            using var json = new JsonTextReader(reader) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
            record = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (json.Read()) throw new InvalidDataException("事务记录包含多余内容。");
        }
        string relative = record.Value<string>("relativePath") ?? "";
        string destination = Path.GetFullPath(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (record.Value<int?>("schemaVersion") != 1 || record.Value<string>("id") != name.Substring(Prefix.Length) ||
            RelativePackPath(rootPath, destination) != relative || !Guid.TryParse(record.Value<string>("uuid"), out Guid uuid) || uuid == Guid.Empty ||
            string.IsNullOrEmpty(record.Value<string>("version")) ||
            !System.Text.RegularExpressions.Regex.IsMatch(record.Value<string>("fingerprint") ?? "", "^[0-9A-F]{64}$"))
            throw new InvalidDataException("移除事务记录无效。");
        return (stage, destination, uuid, record);
    }

    private static void Verify(string directory, Guid uuid, string version, string fingerprint) {
        var actual = BedrockDirectoryInstaller.Preview(directory);
        if (actual.Pack.Uuid != uuid || actual.Pack.Version != version || actual.Fingerprint != fingerprint)
            throw new InvalidDataException("附加包内容与确认的信息不一致。");
    }
    private static string RelativePackPath(string root, string path) {
        string prefix = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("附加包不属于所选数据目录。");
        string relative = path.Substring(prefix.Length).Replace('\\', '/');
        BedrockPackInspector.ValidatePath(relative);
        string[] parts = relative.Split('/');
        if (parts.Length is < 2 or > 3 || !Categories.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("附加包位置不受支持。");
        return relative;
    }
    private static string NormalizeRoot(BedrockDataRoot root) {
        string path = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
        BedrockDataRootFinder.EnsureNoReparsePoints(path);
        return path;
    }
    private static bool Contains(string parent, string path) => string.Equals(parent, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
