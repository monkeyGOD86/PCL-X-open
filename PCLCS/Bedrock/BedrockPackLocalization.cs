using System.Text;

namespace PCLCS.Bedrock;

/// <summary>Best-effort display hints; manifest values remain authoritative and unchanged.</summary>
internal static class BedrockPackLocalization {
    internal const int MaxFileBytes = 256 * 1024;
    private static readonly string[] Languages = ["zh_CN", "en_US"];

    internal static bool IsCandidate(string path) => Languages.Any(language =>
        path.Equals("texts/" + language + ".lang", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/texts/" + language + ".lang", StringComparison.OrdinalIgnoreCase));

    internal static BedrockPackInfo FromDirectory(BedrockPackInfo pack, string directory) {
        foreach (string language in Languages) {
            string path = Path.Combine(directory, "texts", language + ".lang");
            try {
                if (!File.Exists(path)) continue;
                BedrockDataRootFinder.EnsureNoReparsePoints(path);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > MaxFileBytes) continue;
                using var bytes = new MemoryStream();
                byte[] buffer = new byte[4096];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
                    if (bytes.Length + read > MaxFileBytes) throw new InvalidDataException("语言文件过大。");
                    bytes.Write(buffer, 0, read);
                }
                var localized = Apply(pack, language, bytes.ToArray());
                if (localized is not null) return localized;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException) { }
        }
        return pack;
    }

    internal static BedrockPackInfo FromArchive(BedrockPackInfo pack, string prefix, IReadOnlyDictionary<string, byte[]> files) {
        foreach (string language in Languages) {
            if (!files.TryGetValue(prefix + "texts/" + language + ".lang", out byte[]? bytes)) continue;
            try {
                var localized = Apply(pack, language, bytes);
                if (localized is not null) return localized;
            } catch (DecoderFallbackException) { }
        }
        return pack;
    }

    private static BedrockPackInfo? Apply(BedrockPackInfo pack, string language, byte[] bytes) {
        string text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null) {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            int separator = line.IndexOf('=');
            if (separator < 0) continue;
            string key = line.Substring(0, separator).Trim();
            if (key != pack.Name && key != pack.Description) continue;
            string value = line.Substring(separator + 1);
            int comment = value.IndexOf("\t##", StringComparison.Ordinal);
            if (comment >= 0) value = value.Substring(0, comment);
            value = value.Trim();
            if (value.Length == 0 || value.Length > 4096 || value.Any(char.IsControl) || values.ContainsKey(key)) return null;
            values.Add(key, value);
        }
        bool hasName = values.TryGetValue(pack.Name, out string? name) && name!.Length <= 256;
        bool hasDescription = values.TryGetValue(pack.Description, out string? description);
        if (!hasName && !hasDescription) return null;
        return new(pack.Name, pack.Description, pack.Uuid, pack.Version, pack.Modules, pack.Dependencies,
            pack.MinEngineVersion, pack.PackScope, hasName ? name : null, hasDescription ? description : null, language);
    }
}
