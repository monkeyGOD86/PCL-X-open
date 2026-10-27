namespace PCLCS.Bedrock;

public sealed class BedrockDependencyResult(string dependency, string status) {
    public string Dependency { get; } = dependency;
    public string Status { get; } = status;
}

public sealed class BedrockDependencyReport(IReadOnlyList<BedrockDependencyResult> results, IReadOnlyList<string> incompleteRoots) {
    public IReadOnlyList<BedrockDependencyResult> Results { get; } = results;
    public IReadOnlyList<string> IncompleteRoots { get; } = incompleteRoots;
}

/// <summary>Checks declared pack UUIDs across selected local roots; it does not resolve platform modules or activate dependencies.</summary>
public static class BedrockDependencyCheck {
    public static BedrockDependencyReport Analyze(BedrockPackInfo pack, IReadOnlyList<BedrockDataRoot> roots) {
        if (roots.Count is < 1 or > 128) throw new InvalidDataException("依赖检查目录数量无效。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<BedrockPackInfo>();
        var incomplete = new List<string>();
        foreach (var root in roots) {
            string path = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
            if (!seen.Add(path)) throw new InvalidDataException("依赖检查目录重复。");
            try {
                var catalog = BedrockAddonCatalog.Read(root);
                if (catalog.Any(item => item.Manifest is null)) incomplete.Add(path);
                installed.AddRange(catalog.Where(item => item.Manifest is not null).Select(item => item.Manifest!));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or DecoderFallbackException) {
                incomplete.Add(path);
            }
        }
        var results = new List<BedrockDependencyResult>();
        foreach (string dependency in pack.Dependencies) {
            string[] parts = dependency.Split(new[] { ' ' }, 2);
            if (!Guid.TryParse(parts[0], out Guid id)) {
                results.Add(new(dependency, "平台模块：未验证"));
                continue;
            }
            var matches = installed.Where(item => item.Uuid == id).ToArray();
            if (matches.Length == 0) results.Add(new(dependency, incomplete.Count == 0 ? "所选目录未发现" : "无法确认：部分目录未读全"));
            else if (parts.Length > 1 && !matches.Any(item => item.Version == parts[1]))
                results.Add(new(dependency, incomplete.Count == 0 ? "未发现声明版本" : "无法确认声明版本：部分目录未读全"));
            else results.Add(new(dependency, "已在所选目录发现；世界启用状态未检查"));
        }
        return new(results, incomplete);
    }
}
