using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PCLCS.Bedrock;

public sealed class BedrockBackupInfo(string worldName, int files, long bytes) {
    public string WorldName { get; } = worldName;
    public int Files { get; } = files;
    public long Bytes { get; } = bytes;
}

/// <summary>Creates and verifies a separate world backup without modifying the world.</summary>
public static class BedrockWorldBackup {
    private const long MaxBytes = 20L * 1024 * 1024 * 1024;
    private const int MaxFiles = 50000;
    private const string ManifestName = "_pcl_backup/manifest.json";

    public static BedrockBackupInfo Create(BedrockDataRoot root, BedrockWorldInfo world, string destination) =>
        Create(root, world, destination, null);

    internal static BedrockBackupInfo Create(BedrockDataRoot root, BedrockWorldInfo world, string destination, Action? beforeSourceVerification) {
        if (!root.SupportsWorlds) throw new InvalidDataException("共享内容目录不是用户世界目录。");
        string worldRoot = Path.GetFullPath(Path.Combine(root.Path, "minecraftWorlds"));
        string worldPath = Path.GetFullPath(world.Path).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(worldPath), worldRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("世界不属于所选数据目录。");
        BedrockDataRootFinder.EnsureNoReparsePoints(worldPath);
        if (!File.Exists(Path.Combine(worldPath, "level.dat"))) throw new InvalidDataException("世界缺少 level.dat。");
        string output = Path.GetFullPath(destination);
        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份文件必须使用 .zip 扩展名。");
        string rootPath = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (output.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份必须保存在游戏数据目录之外。");
        string parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("备份目录无效。");
        BedrockDataRootFinder.EnsureNoReparsePoints(parent);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("目标文件已经存在，请选择其他名称。");
        string temporary = output + ".partial-" + Guid.NewGuid().ToString("N");
        bool temporaryCreated = false;
        try {
            var entries = new JArray();
            var directories = new JArray();
            long total = 0;
            int files = 0;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                temporaryCreated = true;
                using var archive = new ZipArchive(file, ZipArchiveMode.Create);
                var pending = new Stack<(string Path, int Depth)>();
                pending.Push((worldPath, 0));
                while (pending.Count > 0) {
                    var (folder, depth) = pending.Pop();
                    if (depth > 64) throw new InvalidDataException("世界目录层级过深。");
                    foreach (string entryPath in Directory.EnumerateFileSystemEntries(folder)) {
                        var attributes = File.GetAttributes(entryPath);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("世界包含链接或重解析点。");
                        if ((attributes & FileAttributes.Directory) != 0) {
                            if (directories.Count >= MaxFiles) throw new InvalidDataException("世界目录数量超过备份限制。");
                            string relativeDirectory = entryPath.Substring(worldPath.Length + 1).Replace('\\', '/');
                            ValidateRelative(relativeDirectory);
                            directories.Add(relativeDirectory);
                            pending.Push((entryPath, depth + 1));
                            continue;
                        }
                        if (++files > MaxFiles) throw new InvalidDataException("世界文件数量超过备份限制。");
                        string relative = entryPath.Substring(worldPath.Length + 1).Replace('\\', '/');
                        ValidateRelative(relative);
                        using var input = new FileStream(entryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        long length = input.Length;
                        if (length > MaxBytes - total) throw new InvalidDataException("世界大小超过备份限制。");
                        // Our verifier rejects extreme ratios. Store files without compression so
                        // legitimate repetitive world data cannot produce a self-rejected backup.
                        var entry = archive.CreateEntry("world/" + relative, CompressionLevel.NoCompression);
                        using var outputStream = entry.Open();
                        using var hash = SHA256.Create();
                        byte[] buffer = new byte[65536];
                        long copied = 0;
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                            copied += count;
                            if (copied > length) throw new IOException("世界文件在备份期间发生变化。");
                            hash.TransformBlock(buffer, 0, count, null, 0);
                            outputStream.Write(buffer, 0, count);
                        }
                        hash.TransformFinalBlock([], 0, 0);
                        if (copied != length) throw new IOException("世界文件在备份期间发生变化。");
                        total += copied;
                        entries.Add(new JObject { ["path"] = relative, ["size"] = copied, ["sha256"] = Hex(hash.Hash!) });
                    }
                }
                var manifest = new JObject { ["schemaVersion"] = 1, ["worldName"] = world.Name, ["files"] = entries, ["directories"] = directories };
                using var writer = new StreamWriter(archive.CreateEntry(ManifestName).Open(), new UTF8Encoding(false));
                writer.Write(manifest.ToString(Formatting.None));
            }
            beforeSourceVerification?.Invoke();
            VerifySource(worldPath, entries, directories);
            var verified = Verify(temporary);
            File.Move(temporary, output); // CreateNew semantics: no overwrite.
            return verified;
        } catch (Exception ex) when (temporaryCreated) {
            // Do not delete a path another process might have replaced after the stream closed.
            throw new IOException("世界备份未完成：" + ex.Message + "；临时文件保留供检查：" + temporary, ex);
        }
    }

    private static void VerifySource(string worldPath, JArray files, JArray directories) {
        BedrockDataRootFinder.EnsureNoReparsePoints(worldPath);
        var expectedFiles = files.Cast<JObject>().ToDictionary(item => item.Value<string>("path")!, StringComparer.OrdinalIgnoreCase);
        var expectedDirectories = new HashSet<string>(directories.Select(item => item.Value<string>()!), StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((worldPath, 0));
        while (pending.Count > 0) {
            var (folder, depth) = pending.Pop();
            if (depth > 64) throw new IOException("世界目录在备份期间发生变化。");
            foreach (string path in Directory.EnumerateFileSystemEntries(folder)) {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("世界包含链接或重解析点。");
                string relative = path.Substring(worldPath.Length + 1).Replace('\\', '/');
                ValidateRelative(relative);
                if ((attributes & FileAttributes.Directory) != 0) {
                    if (!expectedDirectories.Contains(relative) || !seenDirectories.Add(relative)) throw new IOException("世界目录在备份期间发生变化。");
                    pending.Push((path, depth + 1));
                } else {
                    if (!expectedFiles.TryGetValue(relative, out var expected) || !seenFiles.Add(relative)) throw new IOException("世界文件在备份期间发生变化。");
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    long length = expected.Value<long>("size");
                    if (file.Length != length) throw new IOException("世界文件在备份期间发生变化。");
                    using var hash = SHA256.Create();
                    byte[] buffer = new byte[65536];
                    long actual = 0;
                    int read;
                    while ((read = file.Read(buffer, 0, buffer.Length)) > 0) {
                        actual += read;
                        if (actual > length) throw new IOException("世界文件在备份期间发生变化。");
                        hash.TransformBlock(buffer, 0, read, null, 0);
                    }
                    hash.TransformFinalBlock([], 0, 0);
                    if (actual != length || !string.Equals(Hex(hash.Hash!), expected.Value<string>("sha256"), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("世界文件在备份期间发生变化。");
                }
            }
        }
        if (seenFiles.Count != expectedFiles.Count || seenDirectories.Count != expectedDirectories.Count)
            throw new IOException("世界内容在备份期间发生变化。");
    }

    public static BedrockBackupInfo Verify(string backupPath) {
        using var input = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxFiles + 1) throw new InvalidDataException("备份文件数量超过限制。");
        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries) {
            if (entry.FullName == ManifestName) {
                if (byName.ContainsKey(ManifestName)) throw new InvalidDataException("备份清单重复。");
                byName.Add(ManifestName, entry);
            } else {
                if (!entry.FullName.StartsWith("world/", StringComparison.Ordinal)) throw new InvalidDataException("备份包含未知条目。");
                string relative = entry.FullName.Substring("world/".Length);
                ValidateRelative(relative);
                if (byName.ContainsKey("world/" + relative)) throw new InvalidDataException("备份包含重复路径。");
                byName.Add("world/" + relative, entry);
            }
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("备份包含链接。");
        }
        if (!byName.TryGetValue(ManifestName, out var manifestEntry) || manifestEntry.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("备份清单缺失或过大。");
        JObject manifest;
        using (var stream = manifestEntry.Open())
        using (var bounded = new MemoryStream()) {
            byte[] buffer = new byte[65536];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) {
                if (bounded.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("备份清单过大。");
                bounded.Write(buffer, 0, count);
            }
            bounded.Position = 0;
            using var reader = new StreamReader(bounded, new UTF8Encoding(false, true));
            using var json = new JsonTextReader(reader) { MaxDepth = 16 };
            manifest = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (json.Read()) throw new InvalidDataException("备份清单包含多余内容。");
        }
        if (manifest.Value<int?>("schemaVersion") != 1 || manifest["files"] is not JArray files || files.Count > MaxFiles ||
            manifest["directories"] is not JArray directories || directories.Count > MaxFiles)
            throw new InvalidDataException("不支持的备份清单。");
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JToken token in directories) {
            if (token.Type != JTokenType.String) throw new InvalidDataException("备份目录清单无效。");
            string directory = token.Value<string>()!;
            ValidateRelative(directory);
            if (!directoryNames.Add(directory.Normalize(NormalizationForm.FormC))) throw new InvalidDataException("备份目录重复。");
        }
        long total = 0;
        foreach (JToken token in files) {
            var item = token as JObject ?? throw new InvalidDataException("备份清单无效。");
            string path = item.Value<string>("path") ?? throw new InvalidDataException("备份路径缺失。");
            ValidateRelative(path);
            long size = item.Value<long?>("size") ?? -1;
            string hash = item.Value<string>("sha256") ?? "";
            if (size < 0 || size > MaxBytes - total || hash.Length != 64 || !visited.Add(path.Normalize(NormalizationForm.FormC)) ||
                directoryNames.Contains(path.Normalize(NormalizationForm.FormC)) ||
                !byName.TryGetValue("world/" + path, out var entry) ||
                !string.Equals(entry.FullName, "world/" + path, StringComparison.Ordinal) || entry.Length != size)
                throw new InvalidDataException("备份清单与内容不一致。");
            if (size > 1024 * 1024 && (entry.CompressedLength == 0 || size / Math.Max(entry.CompressedLength, 1) > 200))
                throw new InvalidDataException("备份文件压缩比异常。");
            total += size;
            using var stream = entry.Open();
            using var algorithm = SHA256.Create();
            byte[] buffer = new byte[65536];
            long actual = 0;
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) {
                actual += count;
                if (actual > size) throw new InvalidDataException("备份解压大小不一致。");
                algorithm.TransformBlock(buffer, 0, count, null, 0);
            }
            algorithm.TransformFinalBlock([], 0, 0);
            if (actual != size || !string.Equals(Hex(algorithm.Hash!), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份文件校验失败。");
        }
        foreach (string path in visited.Concat(directoryNames)) {
            int separator = path.IndexOf('/');
            while (separator >= 0) {
                string parent = path.Substring(0, separator);
                if (visited.Contains(parent) || !directoryNames.Contains(parent)) throw new InvalidDataException("备份包含无效的目录结构。");
                separator = path.IndexOf('/', separator + 1);
            }
        }
        if (byName.Count != files.Count + 1 || !visited.Contains("level.dat"))
            throw new InvalidDataException("备份缺少世界数据或包含未列出的条目。");
        string worldName = manifest.Value<string>("worldName") ?? "未知世界";
        if (worldName.Length > 4096) throw new InvalidDataException("备份世界名称过长。");
        return new(worldName, files.Count, total);
    }

    /// <summary>Restores a verified backup as a new world, leaving existing worlds untouched.</summary>
    public static string RestoreAsNew(BedrockDataRoot root, string backupPath) => RestoreAsNew(root, backupPath, null);

    // Allows deterministic testing of failures after staging and before the atomic move.
    internal static string RestoreAsNew(BedrockDataRoot root, string backupPath, Action<string>? beforeMove) {
        if (!root.SupportsWorlds) throw new InvalidDataException("共享内容目录不是用户世界目录。");
        string rootPath = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
        string worldsPath = Path.Combine(rootPath, "minecraftWorlds");
        BedrockDataRootFinder.EnsureNoReparsePoints(rootPath);
        if (!Directory.Exists(worldsPath)) throw new InvalidDataException("所选数据目录没有 minecraftWorlds 文件夹。");
        BedrockDataRootFinder.EnsureNoReparsePoints(worldsPath);

        // Prevent writes to the backup between verification and extraction.
        using var backupLock = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Verify(backupPath);
        backupLock.Position = 0;
        using var archive = new ZipArchive(backupLock, ZipArchiveMode.Read, leaveOpen: true);
        JObject manifest;
        using (var reader = new StreamReader(archive.GetEntry(ManifestName)!.Open(), new UTF8Encoding(false, true)))
        using (var json = new JsonTextReader(reader) { MaxDepth = 16 })
            manifest = JObject.Load(json);
        var directories = (JArray)manifest["directories"]!;
        var files = (JArray)manifest["files"]!;
        string id = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(rootPath, ".pcl-restore-" + id);
        string destination = Path.Combine(worldsPath, "pcl-" + id);
        if (Directory.Exists(stage) || File.Exists(stage) || Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("恢复目录已存在，请重试。");
        try {
            Directory.CreateDirectory(stage);
            foreach (string relative in directories.Select(token => token.Value<string>()!).OrderBy(path => path.Count(ch => ch == '/'))) {
                string path = RestorePath(stage, relative);
                Directory.CreateDirectory(path);
                BedrockDataRootFinder.EnsureNoReparsePoints(path);
            }
            foreach (JObject file in files.Cast<JObject>()) {
                string relative = file.Value<string>("path")!;
                long size = file.Value<long>("size");
                string expected = file.Value<string>("sha256")!;
                string path = RestorePath(stage, relative);
                string parent = Path.GetDirectoryName(path)!;
                BedrockDataRootFinder.EnsureNoReparsePoints(parent);
                using var input = archive.GetEntry("world/" + relative)!.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var algorithm = SHA256.Create();
                byte[] buffer = new byte[65536];
                long actual = 0;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                    actual += count;
                    if (actual > size) throw new InvalidDataException("恢复文件大小不一致。");
                    algorithm.TransformBlock(buffer, 0, count, null, 0);
                    output.Write(buffer, 0, count);
                }
                algorithm.TransformFinalBlock([], 0, 0);
                if (actual != size || !string.Equals(Hex(algorithm.Hash!), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("恢复文件校验失败。");
            }
            BedrockDataRootFinder.EnsureNoReparsePoints(stage);
            BedrockDataRootFinder.EnsureNoReparsePoints(worldsPath);
            beforeMove?.Invoke(stage);
            Directory.Move(stage, destination);
            return destination;
        } catch (Exception ex) {
            // Retain the entire stage: another process may have changed its contents after creation.
            throw new IOException("世界恢复未完成：" + ex.Message + "；临时目录保留供检查：" + stage, ex);
        }
    }

    private static string RestorePath(string stage, string relative) {
        ValidateRelative(relative);
        string basePath = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("恢复路径超出临时目录。");
        return target;
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

    private static void ValidateRelative(string path) {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.StartsWith("/") || path.Contains('\\'))
            throw new InvalidDataException("备份包含无效路径。");
        foreach (string part in path.Split('/')) {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") ||
                part.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 || part.Any(char.IsControl))
                throw new InvalidDataException("备份包含不安全的路径。");
            string device = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                System.Text.RegularExpressions.Regex.IsMatch(device, @"^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidDataException("备份包含 Windows 保留名称。");
        }
    }
}
