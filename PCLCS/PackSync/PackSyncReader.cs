using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace PCLCS.PackSync;

public sealed class PackSyncTargetFile {
    public string Path { get; internal set; } = "";
    public string Sha512 { get; internal set; } = "";
    public long Size { get; internal set; }
    public bool Optional { get; internal set; }
    public IReadOnlyList<string> Downloads { get; internal set; } = Array.Empty<string>();
    /// <summary>Archive entry for an override; null for a downloadable file.</summary>
    public string? ArchiveEntry { get; internal set; }
}

public sealed class PackSyncPackage {
    public Guid PackId { get; internal set; }
    public string Name { get; internal set; } = "";
    public string ReleaseId { get; internal set; } = "";
    public string ArchiveSha512 { get; internal set; } = "";
    public IReadOnlyDictionary<string, string> Dependencies { get; internal set; } = new Dictionary<string, string>();
    public IReadOnlyList<PackSyncTargetFile> Files { get; internal set; } = Array.Empty<PackSyncTargetFile>();
}

/// <summary>Reads and validates a group mrpack without extracting files.</summary>
public static class PackSyncReader {
    private const long MaxMetadataBytes = 8 * 1024 * 1024;
    private const long MaxOverrideBytes = 512L * 1024 * 1024;
    private const long MaxTotalOverrides = 4L * 1024 * 1024 * 1024;
    private const int MaxEntries = 20000;

    public static PackSyncPackage Read(string archivePath) => Read(archivePath, CancellationToken.None);

    public static PackSyncPackage Read(string archivePath, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string archiveHash = Hash(stream, cancellationToken);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("整合包包含过多文件。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries) {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName;
            if (name.IndexOf('\\') >= 0 || name.StartsWith("/", StringComparison.Ordinal) ||
                name.Split('/').Any(p => p == "." || p == "..") || IsLink(entry))
                throw new InvalidDataException($"整合包包含不安全的路径或链接：{name}");
            if (entries.ContainsKey(name)) throw new InvalidDataException($"整合包包含重复路径：{name}");
            entries.Add(name, entry);
        }
        var group = ReadJson(entries, "pcl-group.json");
        if ((int?)group["schemaVersion"] != 1) throw new InvalidDataException("不支持的小队标识格式。");
        if (!Guid.TryParse((string?)group["packId"], out var id) || id == Guid.Empty)
            throw new InvalidDataException("小队标识无效。");
        var index = ReadJson(entries, "modrinth.index.json");
        if ((int?)index["formatVersion"] != 1 || (string?)index["game"] != "minecraft")
            throw new InvalidDataException("不支持的 Modrinth 整合包格式。");
        string nameValue = RequiredString(index, "name");
        string version = RequiredString(index, "versionId");
        if (index["dependencies"] is not JObject deps) throw new InvalidDataException("整合包缺少游戏依赖。");
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in deps.Properties()) {
            if (prop.Name != "minecraft" && prop.Name != "forge" && prop.Name != "neoforge" && prop.Name != "fabric-loader")
                throw new InvalidDataException($"不支持的游戏依赖：{prop.Name}");
            if (prop.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)prop.Value))
                throw new InvalidDataException($"游戏依赖版本无效：{prop.Name}");
            dependencies.Add(prop.Name, (string)prop.Value!);
        }
        if (!dependencies.ContainsKey("minecraft") || dependencies.Keys.Count(k => k != "minecraft") != 1)
            throw new InvalidDataException("整合包必须指定 Minecraft 和一个受支持的加载器。");
        if (index["files"] is not JArray files) throw new InvalidDataException("整合包缺少文件清单。");
        if (files.Count > MaxEntries) throw new InvalidDataException("整合包文件清单过长。");
        var target = new Dictionary<string, PackSyncTargetFile>(StringComparer.OrdinalIgnoreCase);
        var indexPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files) {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not JObject file) throw new InvalidDataException("文件清单格式无效。");
            string path = PackSyncPlanner.ValidatePath(RequiredString(file, "path"));
            if (!indexPaths.Add(path)) throw new InvalidDataException($"文件清单包含重复路径：{path}");
            string client = (string?)file["env"]?["client"] ?? "required";
            if (client != "required" && client != "optional" && client != "unsupported")
                throw new InvalidDataException($"不支持的客户端文件类型：{path}");
            if (client == "unsupported") continue;
            string hash = (string?)file["hashes"]?["sha512"] ?? "";
            if (hash.Length != 128 || hash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException($"文件缺少有效的 SHA-512：{path}");
            if (file["fileSize"]?.Type != JTokenType.Integer || (long)file["fileSize"]! < 0)
                throw new InvalidDataException($"文件大小无效：{path}");
            if (file["downloads"] is not JArray downloads || downloads.Count == 0)
                throw new InvalidDataException($"文件缺少下载地址：{path}");
            var urls = new List<string>();
            foreach (var download in downloads) {
                string? url = (string?)download;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    throw new InvalidDataException($"文件下载地址无效：{path}");
                urls.Add(url!);
            }
            target.Add(path, new PackSyncTargetFile { Path = path, Sha512 = hash.ToLowerInvariant(), Size = (long)file["fileSize"]!, Optional = client == "optional", Downloads = urls });
        }
        long total = 0;
        ApplyOverrides(entries, target, "overrides/", ref total, cancellationToken);
        ApplyOverrides(entries, target, "client-overrides/", ref total, cancellationToken);
        foreach (string path in target.Keys) {
            int separator = path.IndexOf('/');
            while (separator >= 0) {
                string parent = path.Substring(0, separator);
                if (target.ContainsKey(parent)) throw new InvalidDataException($"整合包文件与目录路径冲突：{parent} 和 {path}");
                separator = path.IndexOf('/', separator + 1);
            }
        }
        return new PackSyncPackage { PackId = id, Name = nameValue, ReleaseId = version, ArchiveSha512 = archiveHash,
            Dependencies = dependencies, Files = target.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    private static void ApplyOverrides(Dictionary<string, ZipArchiveEntry> entries, Dictionary<string, PackSyncTargetFile> target, string prefix, ref long total, CancellationToken cancellationToken) {
        var layer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entries.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = pair.Key.Substring(prefix.Length);
            if (relative.Length == 0 || relative.EndsWith("/", StringComparison.Ordinal)) continue;
            string path = PackSyncPlanner.ValidatePath(relative);
            if (!layer.Add(path)) throw new InvalidDataException($"覆盖目录包含重复路径：{path}");
            if (pair.Value.Length > MaxOverrideBytes || pair.Value.Length > MaxTotalOverrides - total)
                throw new InvalidDataException($"覆盖文件超过大小限制：{path}");
            total += pair.Value.Length;
            using var data = pair.Value.Open();
            target[path] = new PackSyncTargetFile { Path = path, Sha512 = Hash(data, cancellationToken, pair.Value.Length), Size = pair.Value.Length, ArchiveEntry = pair.Key };
        }
    }

    private static JObject ReadJson(Dictionary<string, ZipArchiveEntry> entries, string name) {
        if (!entries.TryGetValue(name, out var entry) || entry.Length > MaxMetadataBytes)
            throw new InvalidDataException($"整合包缺少有效的 {name}。");
        using var data = entry.Open();
        using var reader = new StreamReader(data, new UTF8Encoding(false, true));
        using var json = new JsonTextReader(reader) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        try { return JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
        catch (JsonException ex) { throw new InvalidDataException($"整合包文件格式无效：{name}", ex); }
    }

    private static string RequiredString(JObject obj, string key) {
        if (obj[key]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)obj[key]))
            throw new InvalidDataException($"整合包缺少有效字段：{key}");
        return (string)obj[key]!;
    }

    private static bool IsLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    internal static string Hash(Stream stream) => Hash(stream, CancellationToken.None);
    internal static string Hash(Stream stream, CancellationToken cancellationToken, long? expectedLength = null) {
        using var hash = SHA512.Create();
        var buffer = new byte[81920];
        int count;
        long total = 0;
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            total += count;
            if (expectedLength.HasValue && total > expectedLength.Value) throw new InvalidDataException("覆盖文件实际大小超出声明值。");
            hash.TransformBlock(buffer, 0, count, buffer, 0);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedLength.HasValue && total != expectedLength.Value) throw new InvalidDataException("覆盖文件实际大小与声明值不符。");
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant();
    }
}
