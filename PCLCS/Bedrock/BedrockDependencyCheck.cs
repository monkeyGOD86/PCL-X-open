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
    public static BedrockDependencyReport Analyze(BedrockPackInfo pack, IReadOnlyList<BedrockDataRoot> roots) => Analyze(pack, roots, []);

    public static BedrockDependencyReport Analyze(BedrockPackInfo pack, IReadOnlyList<BedrockDataRoot> roots,
        IReadOnlyList<BedrockPackInfo> planned) {
        if (roots.Count is < 1 or > 128) throw new InvalidDataException("依赖检查目录数量无效。");
        if (planned.Count > 128) throw new InvalidDataException("待安装的包过多。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<BedrockInstalledAddon>();
        var incomplete = new List<string>();
        foreach (var root in roots) {
            string path = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
            if (!seen.Add(path)) throw new InvalidDataException("依赖检查目录重复。");
            try {
                var catalog = BedrockAddonCatalog.Read(root);
                if (catalog.Any(item => item.Manifest is null)) incomplete.Add(path);
                installed.AddRange(catalog.Where(item => item.Manifest is not null));
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
            var matches = installed.Where(item => item.Manifest!.Uuid == id).ToArray();
            var pending = planned.Where(item => item.Uuid == id).ToArray();
            var matchingInstalled = matches.Where(item => parts.Length == 1 || item.Manifest!.Version == parts[1]).ToArray();
            bool installedMatch = matchingInstalled.Any(BedrockAddonCatalog.MatchesCategory);
            bool categoryProblem = matchingInstalled.Any(item => !BedrockAddonCatalog.MatchesCategory(item));
            bool pendingMatch = pending.Any(item => parts.Length == 1 || item.Version == parts[1]);
            if (installedMatch) results.Add(new(dependency, "已在所选目录发现；" +
                (categoryProblem ? "其他副本的类型或目录不符；" : "") + "世界启用状态未检查"));
            else if (pendingMatch) results.Add(new(dependency, "本次待安装；" +
                (categoryProblem ? "现有副本的类型或目录不符，仍需检查安装冲突；" : "") + "世界启用状态未检查"));
            else if (categoryProblem) results.Add(new(dependency, "发现声明版本，但包类型不明确或与所在目录不符；游戏可用性未验证"));
            else if (matches.Length + pending.Length == 0) results.Add(new(dependency, incomplete.Count == 0 ? "所选目录未发现" : "无法确认：部分目录未读全"));
            else results.Add(new(dependency, incomplete.Count == 0 ? "未发现声明版本" : "无法确认声明版本：部分目录未读全"));
        }
        return new(results, incomplete);
    }
}
