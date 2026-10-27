using System.Text;

namespace PCLCS.Bedrock;

/// <summary>Experimental journalled multi-pack installation; not connected to the UI.</summary>
public static class BedrockAddonTransaction {
    private const string Prefix = ".pcl-addon-transaction-";
    private const string JournalName = "journal.json";

    public static string Prepare(BedrockDataRoot root, IReadOnlyList<BedrockPreparedPack> packs) {
        if (packs.Count is < 1 or > 16) throw new InvalidDataException("每次事务只能准备 1 至 16 个包。");
        string rootPath = NormalizeRoot(root);
        using var rootLock = BedrockRootLock.Acquire(rootPath);
        var catalog = BedrockAddonCatalog.Read(root);
        if (catalog.Any(item => item.Manifest is null)) throw new InvalidDataException("现有包无法全部识别，无法检查冲突。");
        if (packs.GroupBy(pack => pack.Preview.Pack.Uuid).Any(group => group.Count() > 1))
            throw new InvalidDataException("待安装包包含重复 UUID。");
        var records = new JArray();
        for (int i = 0; i < packs.Count; i++) {
            var pack = packs[i];
            string source = Path.GetFullPath(pack.Directory).TrimEnd(Path.DirectorySeparatorChar);
            if (Contains(rootPath, source) || Contains(source, rootPath)) throw new InvalidDataException("来源和目标目录不能相互包含。");
            var current = BedrockDirectoryInstaller.Preview(source);
            if (current.Fingerprint != pack.Preview.Fingerprint || current.Pack.Uuid != pack.Preview.Pack.Uuid ||
                current.Pack.Version != pack.Preview.Pack.Version) throw new InvalidDataException("包内容已变化，请重新预览。");
            if (catalog.Any(item => item.Manifest!.Uuid == current.Pack.Uuid)) throw new InvalidDataException("目标中存在相同 UUID 的包。");
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
            var journal = new JObject { ["schemaVersion"] = 1, ["id"] = id, ["packs"] = records };
            using (var file = new FileStream(Path.Combine(transaction, JournalName), FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                byte[] bytes = Encoding.UTF8.GetBytes(journal.ToString(Formatting.None));
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            for (int i = 0; i < packs.Count; i++) {
                string stage = Stage(transaction, i);
                Directory.CreateDirectory(stage);
                BedrockDirectoryInstaller.CopyTree(Path.GetFullPath(packs[i].Directory).TrimEnd(Path.DirectorySeparatorChar), stage);
                Verify(stage, (JObject)records[i]);
            }
            return transaction;
        } catch (Exception ex) when (created) {
            throw new IOException("准备安装失败，事务目录保留供检查：" + transaction, ex);
        }
    }

    public static void Commit(BedrockDataRoot root, string transaction) {
        string rootPath = NormalizeRoot(root);
        using var rootLock = BedrockRootLock.Acquire(rootPath);
        var (path, id, records) = ReadJournal(rootPath, transaction);
        var catalog = BedrockAddonCatalog.Read(root);
        if (catalog.Any(item => item.Manifest is null)) throw new InvalidDataException("现有包无法全部识别，无法检查冲突。");
        for (int i = 0; i < records.Count; i++) {
            var record = (JObject)records[i];
            if (catalog.Any(item => item.Manifest!.Uuid == Guid.Parse(record.Value<string>("uuid")!)))
                throw new InvalidDataException("目标中存在相同 UUID 的包。");
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
                Directory.Move(Stage(path, i), Destination(rootPath, id, i, record));
            }
        } catch (Exception ex) {
            throw new IOException("事务提交未完成；请保留事务目录并使用恢复流程：" + path, ex);
        }
    }

    /// <summary>Moves unchanged installed members back to the journal directory without deleting files.</summary>
    public static void Rollback(BedrockDataRoot root, string transaction) {
        string rootPath = NormalizeRoot(root);
        using var rootLock = BedrockRootLock.Acquire(rootPath);
        var (path, id, records) = ReadJournal(rootPath, transaction);
        var toMove = new List<(string From, string To, JObject Record)>();
        for (int i = 0; i < records.Count; i++) {
            var record = (JObject)records[i];
            string stage = Stage(path, i), destination = Destination(rootPath, id, i, record);
            if (File.Exists(stage) || File.Exists(destination)) throw new IOException("事务路径被其他文件占用。");
            if (!Directory.Exists(destination)) continue;
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
            }
        } catch (Exception ex) {
            throw new IOException("恢复未完成，请保留事务目录并再次检查：" + path, ex);
        }
    }

    private static (string Path, string Id, JArray Records) ReadJournal(string rootPath, string transaction) {
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
        if (json.Read() || journal.Value<int?>("schemaVersion") != 1 || journal.Value<string>("id") != id ||
            journal["packs"] is not JArray records || records.Count is < 1 or > 16)
            throw new InvalidDataException("事务记录无效。");
        var ids = new HashSet<Guid>();
        foreach (var token in records) {
            if (token is not JObject record || record.Value<string>("category") is not ("resource_packs" or "behavior_packs") ||
                !Guid.TryParse(record.Value<string>("uuid"), out Guid uuid) || uuid == Guid.Empty || !ids.Add(uuid) ||
                string.IsNullOrEmpty(record.Value<string>("version")) ||
                !System.Text.RegularExpressions.Regex.IsMatch(record.Value<string>("fingerprint") ?? "", "^[0-9A-F]{64}$"))
                throw new InvalidDataException("事务记录中的包无效。");
        }
        return (path, id, records);
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
