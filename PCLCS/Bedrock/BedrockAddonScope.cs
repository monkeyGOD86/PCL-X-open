namespace PCLCS.Bedrock;

public sealed class BedrockAddonConflict(BedrockDataRoot root, BedrockInstalledAddon addon) {
    public BedrockDataRoot Root { get; } = root;
    public BedrockInstalledAddon Addon { get; } = addon;
}

public sealed class BedrockAddonScopeResult(IReadOnlyList<BedrockAddonConflict> conflicts, IReadOnlyList<string> incompleteRoots) {
    public IReadOnlyList<BedrockAddonConflict> Conflicts { get; } = conflicts;
    public IReadOnlyList<string> IncompleteRoots { get; } = incompleteRoots;
    public bool Complete => IncompleteRoots.Count == 0;
}

/// <summary>Checks a caller-supplied set of roots; it cannot establish which roots the game actually uses.</summary>
public static class BedrockAddonScope {
    public static BedrockAddonScopeResult Check(IReadOnlyList<BedrockDataRoot> roots, IEnumerable<Guid> ids) {
        if (roots.Count is < 1 or > 128) throw new InvalidDataException("附加包检查目录数量无效。");
        var requested = new HashSet<Guid>(ids);
        if (requested.Contains(Guid.Empty) || requested.Count > 128) throw new InvalidDataException("附加包 UUID 无效或过多。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<BedrockAddonConflict>();
        var incomplete = new List<string>();
        foreach (var root in roots) {
            string path = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
            if (!seen.Add(path)) throw new InvalidDataException("附加包检查目录重复。");
            try {
                var catalog = BedrockAddonCatalog.Read(root);
                if (catalog.Any(item => item.Manifest is null)) incomplete.Add(path);
                foreach (var addon in catalog.Where(item => item.Manifest is not null && requested.Contains(item.Manifest.Uuid)))
                    conflicts.Add(new(root, addon));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or DecoderFallbackException) {
                incomplete.Add(path);
            }
        }
        return new(conflicts, incomplete);
    }
}
