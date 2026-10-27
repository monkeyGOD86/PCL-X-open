namespace PCLCS.Bedrock;

public sealed class BedrockWorldPackReference(BedrockDataRoot root, BedrockWorldInfo world, string kind, string reference) {
    public BedrockDataRoot Root { get; } = root;
    public BedrockWorldInfo World { get; } = world;
    public string Kind { get; } = kind;
    public string Reference { get; } = reference;
}

public sealed class BedrockWorldPackUsageResult(IReadOnlyList<BedrockWorldPackReference> references, IReadOnlyList<string> incompleteRoots) {
    public IReadOnlyList<BedrockWorldPackReference> References { get; } = references;
    public IReadOnlyList<string> IncompleteRoots { get; } = incompleteRoots;
    public bool Complete => IncompleteRoots.Count == 0;
}

/// <summary>Checks accessible local worlds in explicitly supplied roots; it cannot discover cloud-only worlds.</summary>
public static class BedrockWorldPackUsage {
    public static BedrockWorldPackUsageResult Check(IReadOnlyList<BedrockDataRoot> roots, Guid uuid) {
        if (roots.Count is < 1 or > 128 || uuid == Guid.Empty) throw new InvalidDataException("世界检查范围或附加包 UUID 无效。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<BedrockWorldPackReference>();
        var incomplete = new List<string>();
        foreach (var root in roots) {
            string path = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar);
            if (!root.SupportsWorlds || !seen.Add(path)) throw new InvalidDataException("世界检查范围包含共享目录或重复目录。");
            try {
                string worldsPath = Path.Combine(path, "minecraftWorlds");
                if (!Directory.Exists(worldsPath)) { incomplete.Add(path); continue; }
                BedrockDataRootFinder.EnsureNoReparsePoints(worldsPath);
                var worlds = BedrockWorldReader.Read(root);
                bool hasLinks = Directory.EnumerateDirectories(worldsPath).Any(world => (File.GetAttributes(world) & FileAttributes.ReparsePoint) != 0);
                if (hasLinks || worlds.Any(world => world.Warning is not null || !world.HasLevelData ||
                        (File.GetAttributes(Path.Combine(world.Path, "level.dat")) & FileAttributes.ReparsePoint) != 0)) incomplete.Add(path);
                foreach (var world in worlds) {
                    Add(found, root, world, "resource", world.ResourcePacks, uuid);
                    Add(found, root, world, "behavior", world.BehaviorPacks, uuid);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or DecoderFallbackException) {
                incomplete.Add(path);
            }
        }
        return new(found, incomplete);
    }

    private static void Add(List<BedrockWorldPackReference> found, BedrockDataRoot root, BedrockWorldInfo world,
        string kind, IReadOnlyList<string> references, Guid uuid) {
        foreach (string reference in references) {
            string id = reference.Split(new[] { ' ' }, 2)[0];
            if (Guid.TryParse(id, out Guid parsed) && parsed == uuid) found.Add(new(root, world, kind, reference));
        }
    }
}
