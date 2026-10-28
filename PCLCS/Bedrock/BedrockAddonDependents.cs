namespace PCLCS.Bedrock;

public sealed class BedrockAddonDependent(BedrockDataRoot root, BedrockInstalledAddon addon, string dependency) {
    public BedrockDataRoot Root { get; } = root;
    public BedrockInstalledAddon Addon { get; } = addon;
    public string Dependency { get; } = dependency;
}

public sealed class BedrockAddonDependentsResult(IReadOnlyList<BedrockAddonDependent> dependents, IReadOnlyList<string> incompleteRoots) {
    public IReadOnlyList<BedrockAddonDependent> Dependents { get; } = dependents;
    public IReadOnlyList<string> IncompleteRoots { get; } = incompleteRoots;
    public bool Complete => IncompleteRoots.Count == 0;
}

/// <summary>Checks declared UUID dependencies in explicit local roots; it cannot determine whether packs are enabled.</summary>
public static class BedrockAddonDependents {
    public static BedrockAddonDependentsResult Check(IReadOnlyList<BedrockDataRoot> roots, string selectedPath, Guid uuid) {
        if (roots.Count is < 1 or > 129 || uuid == Guid.Empty || string.IsNullOrWhiteSpace(selectedPath))
            throw new InvalidDataException("反向依赖检查范围或目标无效。");
        string selected = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependents = new List<BedrockAddonDependent>();
        var incomplete = new List<string>();
        foreach (var root in roots) {
            string rootPath = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
            if (!seen.Add(rootPath)) throw new InvalidDataException("反向依赖检查范围包含重复目录。");
            try {
                var catalog = BedrockAddonCatalog.Read(root);
                if (catalog.Any(item => item.Manifest is null)) incomplete.Add(rootPath);
                foreach (var addon in catalog.Where(item => item.Manifest is not null)) {
                    if (string.Equals(Path.GetFullPath(addon.Path).TrimEnd(Path.DirectorySeparatorChar), selected, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string dependency in addon.Manifest!.Dependencies) {
                        string idText = dependency.Split(new[] { ' ' }, 2)[0];
                        if (Guid.TryParse(idText, out Guid id) && id == uuid) {
                            if (dependents.Count >= 4096) throw new InvalidDataException("反向依赖数量过多。");
                            dependents.Add(new(root, addon, dependency));
                        }
                    }
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or DecoderFallbackException) {
                if (!incomplete.Contains(rootPath, StringComparer.OrdinalIgnoreCase)) incomplete.Add(rootPath);
            }
        }
        return new(dependents, incomplete);
    }
}
