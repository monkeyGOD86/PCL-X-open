using System.IO.Compression;

namespace PCLCS.Bedrock;

public sealed class BedrockPreparedPack(string directory, BedrockDirectoryPreview preview) {
    public string Directory { get; } = directory;
    public BedrockDirectoryPreview Preview { get; } = preview;
}

/// <summary>Prepares a single mcpack for inspection in a caller-selected temporary directory.</summary>
public static class BedrockArchivePreparer {
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;

    public static BedrockPreparedPack PrepareMcpack(string archivePath, string temporaryParent) {
        if (!Path.GetExtension(archivePath).Equals(".mcpack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("此准备流程只接受单个 .mcpack 文件。");
        BedrockDataRootFinder.EnsureNoReparsePoints(temporaryParent);
        // Keep the same archive locked against other writers throughout validation and extraction.
        using var archiveLock = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = BedrockPackInspector.Inspect(archivePath);
        using var archive = new ZipArchive(archiveLock, ZipArchiveMode.Read);
        var manifest = archive.Entries.Single(entry => {
            string name = BedrockPackInspector.ValidateName(entry);
            return name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase);
        });
        string manifestName = BedrockPackInspector.ValidateName(manifest);
        string prefix = manifestName.Substring(0, manifestName.Length - "manifest.json".Length);
        // Reject unrelated files rather than guessing which content belongs to the package.
        foreach (var entry in archive.Entries) {
            string name = BedrockPackInspector.ValidateName(entry);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !(name.EndsWith("/", StringComparison.Ordinal) && prefix.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("压缩包中存在包目录之外的内容，无法安全确定导入范围。");
        }
        string parent = Path.GetFullPath(temporaryParent).TrimEnd(Path.DirectorySeparatorChar);
        string stage = Path.Combine(parent, "pcl-pack-preparation-" + Guid.NewGuid().ToString("N"));
        if (File.Exists(stage) || Directory.Exists(stage)) throw new IOException("临时目录已存在，请重试。");
        bool created = false;
        try {
            Directory.CreateDirectory(stage);
            created = true;
            long total = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            foreach (var entry in archive.Entries) {
                string name = BedrockPackInspector.ValidateName(entry);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = name.Substring(prefix.Length).TrimEnd('/');
                if (relative.Length == 0) continue;
                BedrockPackInspector.ValidatePath(relative);
                string target = Path.GetFullPath(Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("解压路径超出临时目录。");
                if (name.EndsWith("/", StringComparison.Ordinal)) {
                    Directory.CreateDirectory(target);
                    BedrockDataRootFinder.EnsureNoReparsePoints(target);
                    continue;
                }
                string targetParent = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(targetParent);
                BedrockDataRootFinder.EnsureNoReparsePoints(targetParent);
                if (entry.Length < 0 || entry.Length > MaxFileBytes || entry.Length > MaxTotalBytes - total)
                    throw new InvalidDataException("附加包超过大小限制。");
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[65536];
                long actual = 0;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                    if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new InvalidDataException("附加包解压超时。");
                    actual += count;
                    if (actual > entry.Length || actual > MaxFileBytes || actual > MaxTotalBytes - total)
                        throw new InvalidDataException("解压后的数据超过大小限制。");
                    output.Write(buffer, 0, count);
                }
                if (actual != entry.Length) throw new InvalidDataException("压缩包文件长度不一致。");
                total += actual;
            }
            return new(stage, BedrockDirectoryInstaller.Preview(stage));
        } catch (Exception ex) when (created) {
            throw new IOException("准备附加包失败，临时目录保留供检查：" + stage, ex);
        }
    }
}
