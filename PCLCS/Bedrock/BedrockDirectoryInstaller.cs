using System.Security.Cryptography;
using System.Text;

namespace PCLCS.Bedrock;

public sealed class BedrockDirectoryPreview(BedrockPackInfo pack, string fingerprint) {
    public BedrockPackInfo Pack { get; } = pack;
    public string Fingerprint { get; } = fingerprint;
}

/// <summary>
/// Experimental single-directory installer. Callers must confirm the selected root,
/// ensure the game and sync are stopped, and validate game compatibility first.
/// </summary>
public static class BedrockDirectoryInstaller {
    private const int MaxEntries = 4096;
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;

    public static BedrockDirectoryPreview Preview(string source) {
        _ = BedrockPackInspector.InspectDirectory(source);
        string fingerprint = Fingerprint(source);
        var pack = BedrockPackInspector.InspectDirectory(source);
        if (!string.Equals(fingerprint, Fingerprint(source), StringComparison.Ordinal))
            throw new IOException("附加包在预览期间发生变化，请重试。");
        return new(pack, fingerprint);
    }

    public static string Install(BedrockDataRoot root, string source, BedrockDirectoryPreview confirmed) {
        string rootPath = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
        string sourcePath = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        BedrockDataRootFinder.EnsureNoReparsePoints(rootPath);
        BedrockDataRootFinder.EnsureNoReparsePoints(sourcePath);
        if (ContainsPath(rootPath, sourcePath) || ContainsPath(sourcePath, rootPath))
            throw new InvalidDataException("来源目录和游戏数据目录不能相互包含。");

        // This cannot prevent the game or other tools from writing.
        using (BedrockRootLock.Acquire(rootPath)) {
            var preview = BedrockPackInspector.InspectDirectory(sourcePath);
            if (preview.Uuid != confirmed.Pack.Uuid || preview.Version != confirmed.Pack.Version ||
                !string.Equals(Fingerprint(sourcePath), confirmed.Fingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("附加包与确认的预览不一致，请重新预览。");
            bool resource = preview.Modules.Contains("resources");
            bool behavior = preview.Modules.Contains("data");
            if (resource == behavior) throw new InvalidDataException("无法确定唯一的附加包类型。");
            string category = Path.Combine(rootPath, resource ? "resource_packs" : "behavior_packs");
            if (!Directory.Exists(category)) throw new InvalidDataException("目标附加包目录不存在，无法确认游戏数据布局。");
            BedrockDataRootFinder.EnsureNoReparsePoints(category);
            EnsureNoConflict(root, preview);
            string id = Guid.NewGuid().ToString("N");
            string stage = Path.Combine(rootPath, ".pcl-addon-stage-" + id);
            string destination = Path.Combine(category, "pcl-" + id);
            if (Directory.Exists(stage) || File.Exists(stage) || Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("安装目录已存在，请重试。");
            bool stageCreated = false;
            try {
                Directory.CreateDirectory(stage);
                stageCreated = true;
                CopyTree(sourcePath, stage);
                var staged = BedrockPackInspector.InspectDirectory(stage);
                if (staged.Uuid != preview.Uuid || staged.Version != preview.Version ||
                    !staged.Modules.SequenceEqual(preview.Modules) ||
                    !string.Equals(Fingerprint(stage), confirmed.Fingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("复制后的附加包与预览不一致。");
                EnsureNoConflict(root, staged);
                BedrockDataRootFinder.EnsureNoReparsePoints(stage);
                BedrockDataRootFinder.EnsureNoReparsePoints(category);
                Directory.Move(stage, destination); // Same-volume move; never replaces an existing directory.
                return destination;
            } catch (Exception ex) when (stageCreated) {
                // Preserve incomplete data rather than recursively deleting a potentially changed tree.
                throw new IOException("附加包安装未完成，临时目录保留供检查：" + stage, ex);
            }
        }
    }

    private static void EnsureNoConflict(BedrockDataRoot root, BedrockPackInfo pack) {
        var existing = BedrockAddonCatalog.Read(root);
        if (existing.Any(item => item.Manifest is null))
            throw new InvalidDataException("部分现有附加包无法识别，不能安全检查重复项。");
        if (existing.Any(item => item.Manifest!.Uuid == pack.Uuid))
            throw new InvalidDataException("目标目录已存在相同 UUID 的附加包；不会覆盖或安装另一版本。");
    }

    internal static void CopyTree(string source, string stage) {
        var pending = new Stack<(string Source, string Target)>();
        pending.Push((source, stage));
        int entries = 0;
        long total = 0;
        while (pending.Count > 0) {
            var (folder, target) = pending.Pop();
            BedrockDataRootFinder.EnsureNoReparsePoints(folder);
            BedrockDataRootFinder.EnsureNoReparsePoints(target);
            foreach (string path in Directory.EnumerateFileSystemEntries(folder)) {
                if (++entries > MaxEntries) throw new InvalidDataException("附加包条目过多。");
                BedrockDataRootFinder.EnsureNoReparsePoints(path);
                string relative = path.Substring(source.Length + 1).Replace('\\', '/');
                BedrockPackInspector.ValidatePath(relative);
                string next = Path.Combine(target, Path.GetFileName(path));
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) {
                    Directory.CreateDirectory(next);
                    pending.Push((path, next));
                } else {
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    long length = input.Length;
                    if (length > MaxFileBytes || length > MaxTotalBytes - total) throw new InvalidDataException("附加包超过大小限制。");
                    using var output = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    byte[] buffer = new byte[65536];
                    long copied = 0;
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                        copied += count;
                        if (copied > length) throw new IOException("来源文件在复制期间发生变化。");
                        output.Write(buffer, 0, count);
                    }
                    if (copied != length) throw new IOException("来源文件在复制期间发生变化。");
                    total += copied;
                }
            }
        }
    }

    // Hash paths, empty directories and file contents, so confirmation applies to the complete tree.
    private static string Fingerprint(string directory) {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        BedrockDataRootFinder.EnsureNoReparsePoints(root);
        var pending = new Stack<string>();
        var paths = new List<string>();
        pending.Push(root);
        while (pending.Count > 0) {
            string folder = pending.Pop();
            BedrockDataRootFinder.EnsureNoReparsePoints(folder);
            foreach (string path in Directory.EnumerateFileSystemEntries(folder)) {
                if (paths.Count >= MaxEntries) throw new InvalidDataException("附加包条目过多。");
                BedrockDataRootFinder.EnsureNoReparsePoints(path);
                BedrockPackInspector.ValidatePath(path.Substring(root.Length + 1).Replace('\\', '/'));
                paths.Add(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
        paths.Sort((left, right) => StringComparer.Ordinal.Compare(left.Substring(root.Length + 1), right.Substring(root.Length + 1)));
        using var algorithm = SHA256.Create();
        using var sink = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write);
        using var writer = new BinaryWriter(sink, new UTF8Encoding(false), leaveOpen: true);
        long total = 0;
        foreach (string path in paths) {
            BedrockDataRootFinder.EnsureNoReparsePoints(path);
            bool isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            writer.Write(isDirectory);
            writer.Write(path.Substring(root.Length + 1).Replace('\\', '/'));
            if (isDirectory) continue;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = input.Length;
            if (length > MaxFileBytes || length > MaxTotalBytes - total) throw new InvalidDataException("附加包超过大小限制。");
            writer.Write(length);
            writer.Flush();
            byte[] buffer = new byte[65536];
            long countTotal = 0;
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                countTotal += count;
                if (countTotal > length) throw new IOException("来源文件在读取期间发生变化。");
                sink.Write(buffer, 0, count);
            }
            if (countTotal != length) throw new IOException("来源文件在读取期间发生变化。");
            total += countTotal;
        }
        writer.Flush();
        sink.FlushFinalBlock();
        return BitConverter.ToString(algorithm.Hash!).Replace("-", "");
    }

    private static bool ContainsPath(string parent, string path) =>
        string.Equals(parent, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
