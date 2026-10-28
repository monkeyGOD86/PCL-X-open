using System.Text;

namespace PCLCS.Bedrock;

public sealed class BedrockAddonTransactionInfo(string path, string state, int staged, int installed, int missing, string? error) {
    public string Path { get; } = path;
    public string State { get; } = state;
    public int Staged { get; } = staged;
    public int Installed { get; } = installed;
    public int Missing { get; } = missing;
    public string? Error { get; } = error;
}

/// <summary>Experimental journalled multi-pack installation with recoverable staging.</summary>
public static class BedrockAddonTransaction {
    private const string Prefix = ".pcl-addon-transaction-";
    private const string JournalName = "journal.json";
    private const long MaxStagedBytes = 1024L * 1024 * 1024;

    /// <summary>Reports path-based status only; commit and rollback verify contents independently.</summary>
    public static IReadOnlyList<BedrockAddonTransactionInfo> List(BedrockDataRoot root) {
        string rootPath = NormalizeRoot(root);
        using var rootLock = BedrockRootLock.Acquire(rootPath);
        var result = new List<BedrockAddonTransactionInfo>();
        foreach (string candidate in Directory.EnumerateDirectories(rootPath, Prefix + "*")) {
            if (result.Count >= 1024) throw new InvalidDataException("事务目录数量超过读取限制。");
            try {
                var (path, id, records, _) = ReadJournal(rootPath, candidate);
                int staged = 0, installed = 0, missing = 0;
                bool ambiguous = false;
                for (int i = 0; i < records.Count; i++) {
                    string stage = Stage(path, i), destination = Destination(rootPath, id, i, (JObject)records[i]);
                    if (File.Exists(stage) || File.Exists(destination)) { ambiguous = true; continue; }
                    bool hasStage = Directory.Exists(stage), hasDestination = Directory.Exists(destination);
                    if (hasStage && hasDestination) ambiguous = true;
                    else if (hasStage) staged++;
                    else if (hasDestination) installed++;
                    else missing++;
                }
                string state = ambiguous ? "路径冲突" : missing > 0 ? "准备未完成" :
                    staged == records.Count ? "已暂存" : installed == records.Count ? "已移动" : "部分移动";
                result.Add(new(path, state, staged, installed, missing, null));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or
                                       InvalidDataException or FormatException or InvalidCastException or OverflowException or ArgumentException) {
                result.Add(new(candidate, "记录不可读取", 0, 0, 0, ex.Message));
            }
        }
        return result;
    }

    public static string Prepare(BedrockDataRoot root, IReadOnlyList<BedrockPreparedPack> packs, IReadOnlyList<BedrockDataRoot> scope) {
        if (packs.Count is < 1 or > 16) throw new InvalidDataException("每次事务只能准备 1 至 16 个包。");
        string rootPath = NormalizeRoot(root);
        string[] scopePaths = NormalizeScope(rootPath, scope);
        using var rootLock = BedrockRootLock.AcquireAll(scopePaths);
        if (packs.GroupBy(pack => pack.Preview.Pack.Uuid).Any(group => group.Count() > 1))
            throw new InvalidDataException("待安装包包含重复 UUID。");
        RequireNoConflicts(scope, packs.Select(pack => pack.Preview.Pack.Uuid));
        var records = new JArray();
        for (int i = 0; i < packs.Count; i++) {
            var pack = packs[i];
            string source = Path.GetFullPath(pack.Directory).TrimEnd(Path.DirectorySeparatorChar);
            if (scopePaths.Any(path => Contains(path, source) || Contains(source, path)))
                throw new InvalidDataException("来源和检查范围中的游戏数据目录不能相互包含。");
            var current = BedrockDirectoryInstaller.Preview(source);
            if (current.Fingerprint != pack.Preview.Fingerprint || current.Pack.Uuid != pack.Preview.Pack.Uuid ||
                current.Pack.Version != pack.Preview.Pack.Version) throw new InvalidDataException("包内容已变化，请重新预览。");
            bool resource = current.Pack.Modules.Contains("resources"), behavior = current.Pack.Modules.Contains("data");
            if (resource == behavior) throw new InvalidDataException("无法确定唯一的附加包类型。");
            string category = resource ? "resource_packs" : "behavior_packs";
            string parent = Path.Combine(rootPath, category);
            if (!Directory.Exists(parent)) throw new InvalidDataException("目标附加包目录不存在。");
            BedrockDataRootFinder.EnsureNoReparsePoints(parent);
            records.Add(new JObject { ["category"] = category, ["uuid"] = current.Pack.Uuid.ToString("D"),
                ["version"] = current.Pack.Version, ["fingerprint"] = current.Fingerprint });
        }
        string id = Guid.NewGuid().ToString("N");
        string transaction = Path.Combine(rootPath, Prefix + id);
        if (File.Exists(transaction) || Directory.Exists(transaction)) throw new IOException("事务目录已存在，请重试。");
        bool created = false;
        try {
            Directory.CreateDirectory(transaction);
            created = true;
            var journal = new JObject { ["schemaVersion"] = 2, ["id"] = id, ["packs"] = records, ["scope"] = new JArray(scopePaths) };
            using (var file = new FileStream(Path.Combine(transaction, JournalName), FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                byte[] bytes = Encoding.UTF8.GetBytes(journal.ToString(Formatting.None));
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            long copied = 0;
            for (int i = 0; i < packs.Count; i++) {
                string stage = Stage(transaction, i);
                Directory.CreateDirectory(stage);
                copied += BedrockDirectoryInstaller.CopyTree(Path.GetFullPath(packs[i].Directory).TrimEnd(Path.DirectorySeparatorChar), stage, MaxStagedBytes - copied);
                Verify(stage, (JObject)records[i]);
            }
            return transaction;
        } catch (Exception ex) when (created) {
            throw new IOException("准备安装失败，事务目录保留供检查：" + transaction, ex);
        }
    }

    public static void Commit(BedrockDataRoot root, string transaction, IReadOnlyList<BedrockDataRoot> scope) {
        string rootPath = NormalizeRoot(root);
        string[] scopePaths = NormalizeScope(rootPath, scope);
        using var rootLock = BedrockRootLock.AcquireAll(scopePaths);
        var (path, id, records, recordedScope) = ReadJournal(rootPath, transaction);
        if (recordedScope is null || !scopePaths.SequenceEqual(recordedScope, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("检查目录与准备事务时不同，请重新准备；旧事务仍可恢复。");
        RequireNoConflicts(scope, records.Cast<JObject>().Select(record => Guid.Parse(record.Value<string>("uuid")!)));
        for (int i = 0; i < records.Count; i++) {
            var record = (JObject)records[i];
            string parent = Path.Combine(rootPath, record.Value<string>("category")!);
            BedrockDataRootFinder.EnsureNoReparsePoints(parent);
            string destination = Destination(rootPath, id, i, record);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("安装目标已存在。");
            Verify(Stage(path, i), record);
        }
        try {
            for (int i = 0; i < records.Count; i++) {
                var record = (JObject)records[i];
                string parent = Path.Combine(rootPath, record.Value<string>("category")!);
                BedrockDataRootFinder.EnsureNoReparsePoints(parent);
                BedrockDataRootFinder.EnsureNoReparsePoints(Stage(path, i));
                Verify(Stage(path, i), record);
                string destination = Destination(rootPath, id, i, record);
                Directory.Move(Stage(path, i), destination);
                Verify(destination, record);
            }
        } catch (Exception ex) {
            throw new IOException("事务提交未完成；请保留事务目录并使用恢复流程：" + path, ex);
        }
    }

    /// <summary>Moves unchanged installed members back to the journal directory without deleting files.</summary>
    public static void Rollback(BedrockDataRoot root, string transaction) {
        string rootPath = NormalizeRoot(root);
        using var rootLock = BedrockRootLock.Acquire(rootPath);
        var (path, id, records, _) = ReadJournal(rootPath, transaction);
        var toMove = new List<(string From, string To, JObject Record)>();
        int missing = 0;
        for (int i = 0; i < records.Count; i++) {
            var record = (JObject)records[i];
            string stage = Stage(path, i), destination = Destination(rootPath, id, i, record);
            if (File.Exists(stage) || File.Exists(destination)) throw new IOException("事务路径被其他文件占用。");
            if (!Directory.Exists(destination)) {
                if (Directory.Exists(stage)) Verify(stage, record);
                else missing++;
                continue;
            }
            if (Directory.Exists(stage)) throw new IOException("暂存和安装目录同时存在，不能自动恢复。");
            Verify(destination, record);
            toMove.Add((destination, stage, record));
        }
        try {
            foreach (var (from, to, record) in toMove) {
                BedrockDataRootFinder.EnsureNoReparsePoints(from);
                BedrockDataRootFinder.EnsureNoReparsePoints(path);
                Verify(from, record);
                Directory.Move(from, to);
                Verify(to, record);
            }
        } catch (Exception ex) {
            throw new IOException("恢复未完成，请保留事务目录并再次检查：" + path, ex);
        }
        if (missing > 0) throw new IOException($"已恢复可移动的包，但有 {missing} 个包的安装和暂存目录均缺失；请检查事务：" + path);
    }

    private static (string Path, string Id, JArray Records, string[]? Scope) ReadJournal(string rootPath, string transaction) {
        string path = Path.GetFullPath(transaction).TrimEnd(Path.DirectorySeparatorChar);
        string name = Path.GetFileName(path);
        if (!string.Equals(Path.GetDirectoryName(path), rootPath, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name.Substring(Prefix.Length), "N", out _)) throw new InvalidDataException("事务目录无效。");
        BedrockDataRootFinder.EnsureNoReparsePoints(path);
        string journalPath = Path.Combine(path, JournalName);
        BedrockDataRootFinder.EnsureNoReparsePoints(journalPath);
        using var file = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 65536) throw new InvalidDataException("事务记录过大。");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        using var json = new JsonTextReader(reader) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
        var journal = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        string id = name.Substring(Prefix.Length);
        int? schema = journal.Value<int?>("schemaVersion");
        if (json.Read() || schema is not (1 or 2) || journal.Value<string>("id") != id ||
            journal["packs"] is not JArray records || records.Count is < 1 or > 16)
            throw new InvalidDataException("事务记录无效。");
        string[]? scope = null;
        if (schema == 2) {
            if (journal["scope"] is not JArray scopeArray || scopeArray.Count is < 1 or > 128 ||
                scopeArray.Any(item => item.Type != JTokenType.String)) throw new InvalidDataException("事务检查范围无效。");
            scope = scopeArray.Select(item => item.Value<string>()!).ToArray();
            if (scope.Any(item => !Path.IsPathRooted(item) || !string.Equals(item, Path.GetFullPath(item).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) ||
                scope.Distinct(StringComparer.OrdinalIgnoreCase).Count() != scope.Length ||
                !scope.Contains(rootPath, StringComparer.OrdinalIgnoreCase) ||
                !scope.SequenceEqual(scope.OrderBy(item => item, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("事务检查范围无效。");
        }
        var ids = new HashSet<Guid>();
        foreach (var token in records) {
            if (token is not JObject record || record.Value<string>("category") is not ("resource_packs" or "behavior_packs") ||
                !Guid.TryParse(record.Value<string>("uuid"), out Guid uuid) || uuid == Guid.Empty || !ids.Add(uuid) ||
                string.IsNullOrEmpty(record.Value<string>("version")) ||
                !System.Text.RegularExpressions.Regex.IsMatch(record.Value<string>("fingerprint") ?? "", "^[0-9A-F]{64}$"))
                throw new InvalidDataException("事务记录中的包无效。");
        }
        return (path, id, records, scope);
    }

    private static string[] NormalizeScope(string rootPath, IReadOnlyList<BedrockDataRoot> scope) {
        if (scope.Count is < 1 or > 128) throw new InvalidDataException("附加包检查目录数量无效。");
        string[] paths = scope.Select(NormalizeRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length || !paths.Contains(rootPath, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("检查范围必须包含所选目标且不能重复。");
        return paths;
    }

    private static void RequireNoConflicts(IReadOnlyList<BedrockDataRoot> scope, IEnumerable<Guid> ids) {
        var result = BedrockAddonScope.Check(scope, ids);
        if (!result.Complete) throw new InvalidDataException("部分数据目录中的包无法识别，不能安全检查冲突。");
        if (result.Conflicts.Count > 0) throw new InvalidDataException("检查范围中已存在相同 UUID 的附加包。");
    }

    private static void Verify(string directory, JObject record) {
        var preview = BedrockDirectoryInstaller.Preview(directory);
        bool resource = preview.Pack.Modules.Contains("resources"), behavior = preview.Pack.Modules.Contains("data");
        if (preview.Fingerprint != record.Value<string>("fingerprint") || preview.Pack.Uuid.ToString("D") != record.Value<string>("uuid") ||
            preview.Pack.Version != record.Value<string>("version") || resource == behavior ||
            record.Value<string>("category") != (resource ? "resource_packs" : "behavior_packs"))
            throw new InvalidDataException("包内容与事务记录不一致。");
    }

    private static string NormalizeRoot(BedrockDataRoot root) {
        string path = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
        BedrockDataRootFinder.EnsureNoReparsePoints(path);
        return path;
    }
    private static string Stage(string transaction, int index) => Path.Combine(transaction, "pack-" + index);
    private static string Destination(string root, string id, int index, JObject record) =>
        Path.Combine(root, record.Value<string>("category")!, "pcl-" + id + "-" + index);
    private static bool Contains(string parent, string path) => string.Equals(parent, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
