using System.Text;

namespace PCLCS.Bedrock;

public sealed class BedrockGlobalPackUsageResult(IReadOnlyList<string> references, IReadOnlyList<string> incompletePaths) {
    public IReadOnlyList<string> References { get; } = references;
    public IReadOnlyList<string> IncompletePaths { get; } = incompletePaths;
}

/// <summary>Checks one known candidate path per supplied root; absence does not establish that global usage is absent.</summary>
public static class BedrockGlobalPackUsage {
    public static BedrockGlobalPackUsageResult Check(IReadOnlyList<BedrockDataRoot> roots, Guid uuid) {
        if (roots.Count is < 1 or > 129 || uuid == Guid.Empty) throw new InvalidDataException("全局资源检查范围或 UUID 无效。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new List<string>();
        var incomplete = new List<string>();
        foreach (var root in roots) {
            string normalized = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
            if (!seen.Add(normalized)) throw new InvalidDataException("全局资源检查范围包含重复目录。");
            string path = Path.Combine(normalized, "minecraftpe", "global_resource_packs.json");
            try {
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                BedrockDataRootFinder.EnsureNoReparsePoints(path);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > 1024 * 1024) throw new InvalidDataException("全局资源配置过大。");
                using var content = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0) {
                    if (content.Length + read > 1024 * 1024) throw new InvalidDataException("全局资源配置过大。");
                    content.Write(buffer, 0, read);
                }
                content.Position = 0;
                using var reader = new StreamReader(content, new UTF8Encoding(false, true));
                using var json = new JsonTextReader(reader) { MaxDepth = 16, DateParseHandling = DateParseHandling.None };
                var entries = JArray.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (json.Read() || entries.Count > 2048) throw new InvalidDataException("全局资源配置无效。");
                foreach (var entry in entries) {
                    if (entry is not JObject pack || pack["pack_id"]?.Type != JTokenType.String ||
                        !Guid.TryParse(pack.Value<string>("pack_id"), out Guid id) || id == Guid.Empty)
                        throw new InvalidDataException("全局资源引用无效。");
                    if (id == uuid && !references.Contains(path, StringComparer.OrdinalIgnoreCase)) references.Add(path);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or DecoderFallbackException) {
                incomplete.Add(path);
            }
        }
        return new(references, incomplete);
    }
}
