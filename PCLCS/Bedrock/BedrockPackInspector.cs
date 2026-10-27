using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PCLCS.Bedrock;

public sealed class BedrockPackInfo(string name, string description, Guid uuid, string version,
    IReadOnlyList<string> modules, IReadOnlyList<string> dependencies, string? minEngineVersion) {
    public string Name { get; } = name;
    public string Description { get; } = description;
    public Guid Uuid { get; } = uuid;
    public string Version { get; } = version;
    public IReadOnlyList<string> Modules { get; } = modules;
    public IReadOnlyList<string> Dependencies { get; } = dependencies;
    public string? MinEngineVersion { get; } = minEngineVersion;
}

/// <summary>Validates archives without extracting or executing their contents.</summary>
public static class BedrockPackInspector {
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxEntryBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;
    private const long MaxNestedBytes = 64L * 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;
    private const int MaxEntries = 4096;

    public static IReadOnlyList<BedrockPackInfo> Inspect(string filePath) {
        string extension = Path.GetExtension(filePath);
        if (!extension.Equals(".mcpack", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mcaddon", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只支持 .mcpack 和 .mcaddon 文件。");
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxArchiveBytes) throw new InvalidDataException("压缩包超过大小限制。");
        var budget = new ArchiveBudget();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var result = InspectArchive(archive, extension.Equals(".mcaddon", StringComparison.OrdinalIgnoreCase), budget);
        if (result.Count == 0) throw new InvalidDataException("没有找到受支持的资源包或行为包。");
        if (result.Count > 128) throw new InvalidDataException("附加包中包含过多的包。");
        if (result.GroupBy(pack => (pack.Uuid, pack.Version)).Any(group => group.Count() > 1))
            throw new InvalidDataException("压缩包包含重复的包 UUID 和版本。");
        return result;
    }

    /// <summary>Inspects an unpacked pack without following links or modifying its files.</summary>
    public static BedrockPackInfo InspectDirectory(string directory) {
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("未选择附加包文件夹。");
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择具体的附加包文件夹。");
        BedrockDataRootFinder.EnsureNoReparsePoints(root);
        var pending = new Stack<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);
        int entries = 0;
        long total = 0;
        while (pending.Count > 0) {
            string folder = pending.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(folder)) {
                if (++entries > MaxEntries) throw new InvalidDataException("附加包条目过多。");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("附加包包含链接或重解析点。");
                string relative = ValidatePath(path.Substring(root.Length + 1).Replace('\\', '/'));
                if (!names.Add(relative)) throw new InvalidDataException("附加包包含重复或冲突的路径。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else {
                    long size = new FileInfo(path).Length;
                    if (size > MaxEntryBytes || size > MaxTotalBytes - total) throw new InvalidDataException("附加包超过大小限制。");
                    total += size;
                }
            }
        }
        return InspectManifestFile(Path.Combine(root, "manifest.json"));
    }

    internal static BedrockPackInfo InspectManifestFile(string path) {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("清单是链接。");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxManifestBytes) throw new InvalidDataException("清单过大。");
        using var content = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
            if (content.Length + count > MaxManifestBytes) throw new InvalidDataException("清单过大。");
            content.Write(buffer, 0, count);
        }
        return ParseManifest(content.ToArray());
    }

    private static List<BedrockPackInfo> InspectArchive(ZipArchive archive, bool addon, ArchiveBudget budget) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifests = new List<BedrockPackInfo>();
        foreach (var entry in archive.Entries) {
            if (++budget.Entries > MaxEntries) throw new InvalidDataException("压缩包条目过多。");
            string name = ValidateName(entry);
            if (!seen.Add(name.TrimEnd('/'))) throw new InvalidDataException("压缩包包含重复或冲突的路径。");
            if (name.EndsWith("/", StringComparison.Ordinal)) continue;
            files.Add(name);
            if (entry.Length > MaxEntryBytes || entry.Length < 0 || entry.CompressedLength < 0)
                throw new InvalidDataException("压缩包中的文件超过大小限制。");
            if (entry.Length > 1024 * 1024 && (entry.CompressedLength == 0 || entry.Length / Math.Max(entry.CompressedLength, 1) > 200))
                throw new InvalidDataException("压缩比异常，已停止读取。");
            string extension = Path.GetExtension(name);
            if (extension.Equals(".mcworld", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mcaddon", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("附加包中包含不支持的世界或嵌套附加包。");
            bool nestedPack = extension.Equals(".mcpack", StringComparison.OrdinalIgnoreCase);
            if (nestedPack && !addon) throw new InvalidDataException("资源包中不能包含嵌套资源包。");
            bool manifest = name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase);
            long captureLimit = nestedPack ? MaxNestedBytes : manifest ? MaxManifestBytes : 0;
            using var input = entry.Open();
            byte[]? content = ReadEntry(input, entry.Length, captureLimit, budget);
            if (nestedPack) {
                using var nestedStream = new MemoryStream(content!, writable: false);
                using var nested = new ZipArchive(nestedStream, ZipArchiveMode.Read);
                manifests.AddRange(InspectArchive(nested, false, budget));
            } else if (manifest) {
                manifests.Add(ParseManifest(content!));
            }
            if (manifests.Count > 128) throw new InvalidDataException("附加包中包含过多的包。");
        }
        foreach (string name in seen) {
            int separator = name.IndexOf('/');
            while (separator >= 0) {
                if (files.Contains(name.Substring(0, separator))) throw new InvalidDataException("压缩包包含文件与目录冲突。");
                separator = name.IndexOf('/', separator + 1);
            }
        }
        if (!addon && manifests.Count != 1) throw new InvalidDataException("每个 .mcpack 必须包含一个包清单。");
        return manifests;
    }

    private static byte[]? ReadEntry(Stream input, long declaredLength, long captureLimit, ArchiveBudget budget) {
        if (captureLimit > 0 && declaredLength > captureLimit) throw new InvalidDataException("清单或嵌套包超过大小限制。");
        using var captured = captureLimit > 0 ? new MemoryStream() : null;
        byte[] buffer = new byte[65536];
        long actual = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
            if (budget.Timer.Elapsed > TimeSpan.FromSeconds(30)) throw new InvalidDataException("压缩包检查超时。");
            actual += read;
            budget.Bytes += read;
            if (actual > MaxEntryBytes || budget.Bytes > MaxTotalBytes || (captureLimit > 0 && actual > captureLimit))
                throw new InvalidDataException("解压后的数据超过大小限制。");
            captured?.Write(buffer, 0, read);
        }
        if (actual != declaredLength) throw new InvalidDataException("压缩包文件长度不一致。");
        return captured?.ToArray();
    }

    private static string ValidateName(ZipArchiveEntry entry) {
        int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        if (unixType == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
            throw new InvalidDataException("压缩包包含链接或重解析点。");
        return ValidatePath(entry.FullName.Replace('\\', '/'));
    }

    private static string ValidatePath(string path) {
        string name = path.Normalize(NormalizationForm.FormC);
        if (name.Length == 0 || name.Length > 1024 || name.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidDataException("压缩包包含无效路径。");
        var parts = name.TrimEnd('/').Split('/');
        if (parts.Length > 32) throw new InvalidDataException("压缩包路径层级过深。");
        foreach (string part in parts) {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") ||
                part.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 || part.Any(char.IsControl))
                throw new InvalidDataException("压缩包包含不安全的路径。");
            string device = part.Split('.')[0].ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" ||
                Regex.IsMatch(device, @"^(COM|LPT)[1-9]$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("压缩包包含 Windows 保留名称。");
        }
        return name;
    }

    private static BedrockPackInfo ParseManifest(byte[] content) {
        try {
            using var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(content).TrimStart('\uFEFF'))) {
                MaxDepth = 32, DateParseHandling = DateParseHandling.None
            };
            var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException("清单中含有多余内容。");
            int format = root.Value<int?>("format_version") ?? 0;
            if (format < 1 || format > 3) throw new InvalidDataException("不支持的清单格式版本。");
            var header = root["header"] as JObject ?? throw new InvalidDataException("清单缺少 header。");
            if (!Guid.TryParse(header.Value<string>("uuid"), out Guid uuid) || uuid == Guid.Empty)
                throw new InvalidDataException("清单的包 UUID 无效。");
            string version = ReadVersion(header["version"]);
            var moduleArray = root["modules"] as JArray ?? throw new InvalidDataException("清单缺少 modules。");
            if (moduleArray.Count > 128) throw new InvalidDataException("清单中的模块过多。");
            var moduleIds = new HashSet<Guid>();
            var modules = new List<string>();
            foreach (JToken moduleToken in moduleArray) {
                var module = moduleToken as JObject ?? throw new InvalidDataException("清单中的 module 无效。");
                if (!Guid.TryParse(module.Value<string>("uuid"), out Guid moduleId) || moduleId == Guid.Empty ||
                    moduleId == uuid || !moduleIds.Add(moduleId)) throw new InvalidDataException("清单中的模块 UUID 无效或重复。");
                _ = ReadVersion(module["version"]);
                string type = module.Value<string>("type") ?? "";
                if (type.Length == 0 || type.Length > 128) throw new InvalidDataException("清单中的模块类型无效。");
                modules.Add(type);
            }
            if (!modules.Any(value => value is "data" or "resources"))
                throw new InvalidDataException("清单不是受支持的资源包或行为包。");
            if (root["dependencies"] is not null && root["dependencies"] is not JArray)
                throw new InvalidDataException("清单中的 dependencies 无效。");
            if ((root["dependencies"] as JArray)?.Count > 128) throw new InvalidDataException("清单中的依赖过多。");
            var dependencies = new List<string>();
            foreach (JToken dependencyToken in (root["dependencies"] as JArray ?? new JArray())) {
                var dependency = dependencyToken as JObject ?? throw new InvalidDataException("清单中的依赖无效。");
                string? dependencyId = dependency.Value<string>("uuid");
                string? moduleName = dependency.Value<string>("module_name");
                if (dependencyId is not null && (!Guid.TryParse(dependencyId, out Guid dependencyUuid) || dependencyUuid == Guid.Empty))
                    throw new InvalidDataException("清单中的依赖 UUID 无效。");
                if (string.IsNullOrWhiteSpace(dependencyId) && string.IsNullOrWhiteSpace(moduleName))
                    throw new InvalidDataException("清单中的依赖没有标识。");
                if (moduleName?.Length > 256) throw new InvalidDataException("清单中的依赖名称过长。");
                dependencies.Add((dependencyId ?? moduleName!) +
                    (dependency["version"] is null ? "" : " " + ReadVersion(dependency["version"])));
            }
            string? minEngine = header["min_engine_version"] is null ? null : ReadVersion(header["min_engine_version"]);
            string name = header.Value<string>("name") ?? "未命名";
            string description = header.Value<string>("description") ?? "";
            if (name.Length > 256 || description.Length > 4096) throw new InvalidDataException("清单中的名称或描述过长。");
            return new(name, description,
                uuid, version, modules, dependencies, minEngine);
        } catch (Exception ex) when (ex is JsonException or DecoderFallbackException or FormatException or OverflowException) {
            throw new InvalidDataException("附加包清单无效。", ex);
        }
    }

    private static string ReadVersion(JToken? token) {
        if (token is JArray a && a.Count == 3 && a.All(value => value.Type == JTokenType.Integer && value.Value<long>() >= 0))
            return string.Join(".", a.Select(value => value.Value<long>().ToString()));
        string? value = token?.Type == JTokenType.String ? token.Value<string>() : null;
        if (value is not null && value.Length <= 100 && Regex.IsMatch(value, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant))
            return value;
        throw new InvalidDataException("清单版本格式无效。");
    }

    private sealed class ArchiveBudget {
        public int Entries;
        public long Bytes;
        public readonly System.Diagnostics.Stopwatch Timer = System.Diagnostics.Stopwatch.StartNew();
    }
}
