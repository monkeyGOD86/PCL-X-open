using System.IO.Compression;
using System.Threading;

namespace PCLCS.PackSync;

/// <summary>A verified staging file. Its name is generated locally, never from an archive path.</summary>
public sealed class PackSyncPreparedFile {
    public string TargetPath { get; internal set; } = "";
    public string StagedPath { get; internal set; } = "";
    public string Sha512 { get; internal set; } = "";
}

public static class PackSyncPreparation {
    /// <summary>
    /// Prepares selected final target files in a new staging directory. The caller owns the
    /// directory and supplies the downloader; this method does not write to game files.
    /// An incomplete directory is intentionally retained for diagnosis.
    /// </summary>
    public static IReadOnlyList<PackSyncPreparedFile> Prepare(string archivePath, PackSyncPackage previewPackage,
        IEnumerable<string> paths, string stagingDirectory, Func<PackSyncTargetFile, CancellationToken, Stream> openDownload,
        CancellationToken cancellationToken) {
        if (previewPackage == null) throw new ArgumentNullException(nameof(previewPackage));
        if (paths == null) throw new ArgumentNullException(nameof(paths));
        if (openDownload == null) throw new ArgumentNullException(nameof(openDownload));
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths) {
            string valid = PackSyncPlanner.ValidatePath(path);
            if (!selected.Add(valid)) throw new InvalidDataException($"准备列表包含重复文件：{valid}");
        }
        var current = PackSyncReader.Read(archivePath, cancellationToken);
        if (!string.Equals(current.ArchiveSha512, previewPackage.ArchiveSha512, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("整合包在预览后发生变化，请重新检查差异。");
        var files = current.Files.Where(f => selected.Contains(f.Path)).ToArray();
        if (files.Length != selected.Count) throw new InvalidDataException("准备列表包含整合包中不存在的文件。");
        cancellationToken.ThrowIfCancellationRequested();
        string staging = Path.GetFullPath(stagingDirectory);
        if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("暂存目录已存在。");
        CheckParents(staging);
        Directory.CreateDirectory(staging);
        CheckParents(staging);
        var result = new List<PackSyncPreparedFile>();
        using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(PackSyncReader.Hash(archiveStream, cancellationToken), current.ArchiveSha512, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("整合包在准备前发生变化，请重新检查差异。");
        archiveStream.Position = 0;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        int index = 0;
        foreach (var file in files) {
            cancellationToken.ThrowIfCancellationRequested();
            string outputPath = Path.Combine(staging, (++index).ToString("D8") + ".data");
            using (var source = file.ArchiveEntry == null ? openDownload(file, cancellationToken) :
                   (archive.GetEntry(file.ArchiveEntry) ?? throw new InvalidDataException($"找不到覆盖文件：{file.Path}")).Open())
            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) != 0) {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += read;
                    if (total > file.Size) throw new InvalidDataException($"文件大小不符：{file.Path}");
                    output.Write(buffer, 0, read);
                }
                if (total != file.Size) throw new InvalidDataException($"文件大小不符：{file.Path}");
                output.Flush(true);
            }
            using (var input = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (!string.Equals(PackSyncReader.Hash(input, cancellationToken), file.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"文件哈希不符：{file.Path}");
            }
            result.Add(new PackSyncPreparedFile { TargetPath = file.Path, StagedPath = outputPath, Sha512 = file.Sha512 });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static void CheckParents(string path) {
        string? current = path;
        while (!string.IsNullOrEmpty(current)) {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"暂存路径包含链接：{current}");
            current = Path.GetDirectoryName(current);
        }
    }
}
