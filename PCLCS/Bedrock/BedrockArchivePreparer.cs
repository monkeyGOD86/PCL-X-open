using System.IO.Compression;

namespace PCLCS.Bedrock;

public sealed class BedrockPreparedPack(string directory, BedrockDirectoryPreview preview) {
    public string Directory { get; } = directory;
    public BedrockDirectoryPreview Preview { get; } = preview;
}

public sealed class BedrockPreparedAddon(string directory, IReadOnlyList<BedrockPreparedPack> packs) {
    public string Directory { get; } = directory;
    public IReadOnlyList<BedrockPreparedPack> Packs { get; } = packs;
}

/// <summary>Prepares a single mcpack for inspection in a caller-selected temporary directory.</summary>
public static class BedrockArchivePreparer {
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;

    /// <summary>Prepares mcaddon members for review; does not install them.</summary>
    public static BedrockPreparedAddon PrepareMcaddon(string archivePath, string temporaryParent) {
        if (!Path.GetExtension(archivePath).Equals(".mcaddon", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("此准备流程只接受 .mcaddon 文件。");
        BedrockDataRootFinder.EnsureNoReparsePoints(temporaryParent);
        using var archiveLock = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var inspected = BedrockPackInspector.Inspect(archivePath);
        using var archive = new ZipArchive(archiveLock, ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => (Entry: entry, Name: BedrockPackInspector.ValidateName(entry))).ToArray();
        var prefixes = names.Where(item => !item.Name.EndsWith("/", StringComparison.Ordinal) &&
                (item.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                 item.Name.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Name.Substring(0, item.Name.Length - "manifest.json".Length)).ToArray();
        var nested = names.Where(item => !item.Name.EndsWith("/", StringComparison.Ordinal) &&
                Path.GetExtension(item.Name).Equals(".mcpack", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (prefixes.Length + nested.Length == 0 || prefixes.Length + nested.Length > 128)
            throw new InvalidDataException("附加包成员数量无效。");
        foreach (string prefix in prefixes) {
            if (prefixes.Any(other => !string.Equals(prefix, other, StringComparison.OrdinalIgnoreCase) &&
                    other.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                nested.Any(item => item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("附加包的成员目录存在重叠。");
        }
        foreach (var item in names.Where(item => !item.Name.EndsWith("/", StringComparison.Ordinal))) {
            int matches = prefixes.Count(prefix => item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) +
                          nested.Count(child => string.Equals(child.Name, item.Name, StringComparison.OrdinalIgnoreCase));
            if (matches != 1) throw new InvalidDataException("附加包中存在无法确定归属的文件。");
        }
        string parent = Path.GetFullPath(temporaryParent).TrimEnd(Path.DirectorySeparatorChar);
        string stage = Path.Combine(parent, "pcl-addon-preparation-" + Guid.NewGuid().ToString("N"));
        if (File.Exists(stage) || Directory.Exists(stage)) throw new IOException("临时目录已存在，请重试。");
        bool created = false;
        try {
            Directory.CreateDirectory(stage);
            created = true;
            long total = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var prepared = new List<BedrockPreparedPack>();
            int index = 0;
            foreach (string prefix in prefixes) {
                string memberPath = Path.Combine(stage, "member-" + (++index) + ".mcpack");
                using (var file = new FileStream(memberPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var member = new ZipArchive(file, ZipArchiveMode.Create)) {
                    foreach (var item in names.Where(item => item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) {
                        string relative = item.Name.Substring(prefix.Length);
                        if (relative.Length == 0) continue;
                        BedrockPackInspector.ValidatePath(relative);
                        var outputEntry = member.CreateEntry(relative, CompressionLevel.NoCompression);
                        if (relative.EndsWith("/", StringComparison.Ordinal)) continue;
                        using var input = item.Entry.Open();
                        using var output = outputEntry.Open();
                        CopyMember(input, output, item.Entry.Length, ref total, timer);
                    }
                }
                prepared.Add(PrepareMcpack(memberPath, stage));
            }
            foreach (var item in nested) {
                string memberPath = Path.Combine(stage, "member-" + (++index) + ".mcpack");
                using (var input = item.Entry.Open())
                using (var output = new FileStream(memberPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    CopyMember(input, output, item.Entry.Length, ref total, timer);
                prepared.Add(PrepareMcpack(memberPath, stage));
            }
            if (prepared.Count != inspected.Count || prepared.Any(pack => !inspected.Any(original =>
                    original.Uuid == pack.Preview.Pack.Uuid && original.Version == pack.Preview.Pack.Version)))
                throw new InvalidDataException("准备后的成员与附加包清单不一致。");
            return new(stage, prepared);
        } catch (Exception ex) when (created) {
            throw new IOException("准备附加包失败，临时目录保留供检查：" + stage, ex);
        }
    }

    private static void CopyMember(Stream input, Stream output, long length, ref long total, System.Diagnostics.Stopwatch timer) {
        if (length < 0 || length > MaxFileBytes || length > MaxTotalBytes - total)
            throw new InvalidDataException("附加包超过大小限制。");
        byte[] buffer = new byte[65536];
        long actual = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new InvalidDataException("附加包解压超时。");
            actual += count;
            if (actual > length || actual > MaxFileBytes || actual > MaxTotalBytes - total)
                throw new InvalidDataException("解压后的数据超过大小限制。");
            output.Write(buffer, 0, count);
        }
        if (actual != length) throw new InvalidDataException("压缩包文件长度不一致。");
        total += actual;
    }

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
