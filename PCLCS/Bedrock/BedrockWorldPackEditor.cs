using System.Security.Cryptography;
using System.Text;

namespace PCLCS.Bedrock;

public sealed class BedrockWorldPackSnapshot(string worldPath, string kind, bool exists, string hash, byte[] bytes) {
    public string WorldPath { get; } = worldPath;
    public string Kind { get; } = kind;
    public bool Exists { get; } = exists;
    public string Hash { get; } = hash;
    internal byte[] Bytes { get; } = (byte[])bytes.Clone();
}

public sealed class BedrockWorldPackChange(string? backupPath, string? displacedPath, string newHash) {
    public string? BackupPath { get; } = backupPath;
    public string? DisplacedPath { get; } = displacedPath;
    public string NewHash { get; } = newHash;
}

/// <summary>Experimental world pack editing. Callers must confirm the root and stop game and synchronization.</summary>
public static class BedrockWorldPackEditor {
    private const int MaxBytes = 1024 * 1024;

    public static BedrockWorldPackSnapshot Read(BedrockDataRoot root, BedrockWorldInfo world, string kind) {
        string path = ConfigurationPath(root, world, kind);
        bool exists = File.Exists(path);
        byte[] bytes = exists ? ReadBounded(path) : [];
        _ = Parse(bytes, exists);
        return new(Path.GetFullPath(world.Path), kind, exists, Hash(bytes), bytes);
    }

    /// <summary>Writes one configuration file after preserving both old and new contents in an external backup.</summary>
    public static BedrockWorldPackChange SetEnabled(BedrockDataRoot root, BedrockWorldInfo world,
        BedrockWorldPackSnapshot snapshot, BedrockPackInfo pack, bool enabled, string backupDirectory) {
        string path = ConfigurationPath(root, world, snapshot.Kind);
        if (!string.Equals(Path.GetFullPath(world.Path), snapshot.WorldPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("配置预览不属于选定的世界。");
        bool resource = pack.Modules.Contains("resources"), behavior = pack.Modules.Contains("data");
        if (pack.Uuid == Guid.Empty || resource == behavior || (snapshot.Kind == "resource" ? !resource : !behavior))
            throw new InvalidDataException("附加包类型与配置不匹配。");
        int[] version = ParseVersion(pack.Version);
        string rootPath = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
        string backups = Path.GetFullPath(backupDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (Contains(rootPath, backups)) throw new InvalidDataException("配置备份必须位于所选游戏数据目录之外。");
        BedrockDataRootFinder.EnsureNoReparsePoints(backups);
        using var rootLock = BedrockRootLock.Acquire(rootPath);
        var current = Read(root, world, snapshot.Kind);
        if (current.Exists != snapshot.Exists || current.Hash != snapshot.Hash)
            throw new IOException("世界配置已变化，请重新读取后再操作。");
        JArray array = Parse(current.Bytes, current.Exists);
        var matches = array.Cast<JObject>().Where(item => Guid.Parse(item.Value<string>("pack_id")!) == pack.Uuid).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("世界配置中有重复的包 UUID，不能自动修改。");
        if (matches.Length == 1 && !VersionsEqual((JArray)matches[0]["version"]!, version))
            throw new InvalidDataException("世界配置中已有该 UUID 的其他版本，不能自动覆盖。");
        if (enabled && matches.Length == 1 || !enabled && matches.Length == 0)
            return new(null, null, current.Hash);
        if (enabled) array.Add(new JObject { ["pack_id"] = pack.Uuid.ToString("D"), ["version"] = new JArray(version) });
        else matches[0].Remove();
        byte[] output = new UTF8Encoding(false).GetBytes(array.ToString(Formatting.Indented));
        if (output.Length > MaxBytes) throw new InvalidDataException("修改后的世界配置过大。");
        string id = Guid.NewGuid().ToString("N");
        string backup = Path.Combine(backups, "pcl-world-pack-" + id + ".json");
        string temporary = Path.Combine(snapshot.WorldPath, ".pcl-pack-write-" + id);
        string displaced = Path.Combine(snapshot.WorldPath, ".pcl-pack-before-" + id);
        var record = new JObject { ["schemaVersion"] = 1, ["worldPath"] = snapshot.WorldPath, ["kind"] = snapshot.Kind,
            ["existed"] = current.Exists, ["original"] = Convert.ToBase64String(current.Bytes), ["originalHash"] = current.Hash,
            ["updated"] = Convert.ToBase64String(output), ["updatedHash"] = Hash(output) };
        try {
            if (File.Exists(displaced) || Directory.Exists(displaced)) throw new IOException("原配置副本路径已存在。");
            WriteNewAndFlush(backup, new UTF8Encoding(false).GetBytes(record.ToString(Formatting.None)));
            WriteNewAndFlush(temporary, output);
            var final = Read(root, world, snapshot.Kind);
            if (final.Exists != current.Exists || final.Hash != current.Hash) throw new IOException("世界配置在写入前发生变化。");
            BedrockDataRootFinder.EnsureNoReparsePoints(snapshot.WorldPath);
            if (current.Exists) {
                File.Replace(temporary, path, displaced);
                // Keep the exact displaced file, including if another writer raced the last check.
                if (Hash(ReadBounded(displaced)) != current.Hash)
                    throw new IOException("配置在替换时发生变化；被替换文件已保留：" + displaced);
            } else File.Move(temporary, path); // Fails if another writer created the file.
            if (Hash(ReadBounded(path)) != Hash(output)) throw new IOException("写入后的世界配置发生变化，请检查备份。");
            return new(backup, current.Exists ? displaced : null, Hash(output));
        } catch (Exception ex) {
            throw new IOException("配置修改未完成或需要检查；备份：" + backup + "，临时文件：" + temporary + "，原文件副本：" + displaced, ex);
        }
    }

    /// <summary>Restores a verified backup only while the current file still matches the recorded update.</summary>
    public static string Restore(BedrockDataRoot root, BedrockWorldInfo world, string backupPath) {
        string rootPath = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
        string backup = Path.GetFullPath(backupPath);
        if (Contains(rootPath, backup)) throw new InvalidDataException("备份必须位于所选游戏数据目录之外。");
        BedrockDataRootFinder.EnsureNoReparsePoints(backup);
        JObject record;
        using (var file = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            if (file.Length > 4 * MaxBytes) throw new InvalidDataException("配置备份过大。");
            using var reader = new StreamReader(file, new UTF8Encoding(false, true));
            using var json = new JsonTextReader(reader) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
            record = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (json.Read()) throw new InvalidDataException("配置备份包含多余内容。");
        }
        string kind = record.Value<string>("kind") ?? "";
        string path = ConfigurationPath(root, world, kind);
        if (record.Value<int?>("schemaVersion") != 1 ||
            !string.Equals(record.Value<string>("worldPath"), Path.GetFullPath(world.Path), StringComparison.OrdinalIgnoreCase) ||
            record["existed"]?.Type != JTokenType.Boolean) throw new InvalidDataException("配置备份与选定世界不匹配。");
        bool existed = record.Value<bool>("existed");
        byte[] original, updated;
        try {
            original = Convert.FromBase64String(record.Value<string>("original") ?? "");
            updated = Convert.FromBase64String(record.Value<string>("updated") ?? "");
        } catch (FormatException ex) { throw new InvalidDataException("配置备份内容无效。", ex); }
        if (original.Length > MaxBytes || updated.Length > MaxBytes || (!existed && original.Length != 0) ||
            Hash(original) != record.Value<string>("originalHash") || Hash(updated) != record.Value<string>("updatedHash"))
            throw new InvalidDataException("配置备份校验失败。");
        _ = Parse(original, existed);
        _ = Parse(updated, true);
        using var rootLock = BedrockRootLock.Acquire(rootPath);
        var current = Read(root, world, kind);
        if (!current.Exists || current.Hash != Hash(updated)) throw new IOException("世界配置已变化，拒绝覆盖；请手动检查备份。");
        string id = Guid.NewGuid().ToString("N");
        string temporary = Path.Combine(current.WorldPath, ".pcl-pack-restore-" + id);
        string displaced = Path.Combine(current.WorldPath, ".pcl-pack-replaced-" + id);
        try {
            if (File.Exists(displaced) || Directory.Exists(displaced)) throw new IOException("恢复副本路径已存在。");
            if (existed) WriteNewAndFlush(temporary, original);
            var final = Read(root, world, kind);
            if (!final.Exists || final.Hash != current.Hash) throw new IOException("世界配置在恢复前发生变化。");
            BedrockDataRootFinder.EnsureNoReparsePoints(current.WorldPath);
            if (existed) File.Replace(temporary, path, displaced);
            else File.Move(path, displaced); // Preserve the added configuration rather than deleting it.
            if (Hash(ReadBounded(displaced)) != current.Hash)
                throw new IOException("配置在恢复时发生变化；被替换文件已保留：" + displaced);
            if (existed && Hash(ReadBounded(path)) != Hash(original) || !existed && File.Exists(path))
                throw new IOException("恢复后的世界配置发生变化，请检查保留的副本。");
            return displaced;
        } catch (Exception ex) {
            throw new IOException("配置恢复未完成或需要检查；备份：" + backup + "，临时文件：" + temporary + "，恢复前副本：" + displaced, ex);
        }
    }

    private static string ConfigurationPath(BedrockDataRoot root, BedrockWorldInfo world, string kind) {
        if (!root.SupportsWorlds) throw new InvalidDataException("共享内容目录不是世界目录。");
        string worlds = Path.GetFullPath(Path.Combine(root.Path, "minecraftWorlds"));
        string worldPath = Path.GetFullPath(world.Path).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(worldPath), worlds, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("世界不属于所选数据目录。");
        BedrockDataRootFinder.EnsureNoReparsePoints(worldPath);
        string level = Path.Combine(worldPath, "level.dat");
        if (!File.Exists(level)) throw new InvalidDataException("世界缺少 level.dat。");
        BedrockDataRootFinder.EnsureNoReparsePoints(level);
        return Path.Combine(worldPath, kind switch {
            "resource" => "world_resource_packs.json", "behavior" => "world_behavior_packs.json",
            _ => throw new InvalidDataException("未知附加包类型。")
        });
    }

    private static JArray Parse(byte[] bytes, bool exists) {
        if (!exists) return new JArray();
        using var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'))) {
            MaxDepth = 16, DateParseHandling = DateParseHandling.None
        };
        var array = JArray.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read() || array.Count > 2048) throw new InvalidDataException("世界附加包配置无效。");
        foreach (var item in array) {
            if (item is not JObject entry || !Guid.TryParse(entry.Value<string>("pack_id"), out Guid id) || id == Guid.Empty ||
                entry["version"] is not JArray v || v.Count != 3 ||
                v.Any(number => number.Type != JTokenType.Integer || number.Value<long>() < 0 || number.Value<long>() > int.MaxValue))
                throw new InvalidDataException("世界附加包配置无效。");
        }
        return array;
    }

    private static int[] ParseVersion(string version) {
        string[] parts = version.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(ch => ch < '0' || ch > '9') || !int.TryParse(part, out int n) || n < 0))
            throw new InvalidDataException("世界配置只支持三个非负整数的包版本。");
        return parts.Select(int.Parse).ToArray();
    }
    private static bool VersionsEqual(JArray array, int[] version) =>
        array.Select(value => value.Value<int>()).SequenceEqual(version);
    private static byte[] ReadBounded(string path) {
        BedrockDataRootFinder.EnsureNoReparsePoints(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new InvalidDataException("世界配置超过大小限制。");
        using var content = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = file.Read(buffer, 0, buffer.Length)) > 0) {
            if (content.Length + count > MaxBytes) throw new InvalidDataException("世界配置超过大小限制。");
            content.Write(buffer, 0, count);
        }
        return content.ToArray();
    }
    private static void WriteNewAndFlush(string path, byte[] bytes) {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes, 0, bytes.Length);
        file.Flush(true);
    }
    private static string Hash(byte[] bytes) {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
    }
    private static bool Contains(string parent, string path) => string.Equals(parent, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
