using System.Text;

namespace PCLCS.Bedrock;

public sealed class BedrockWorldDependentsResult(BedrockWorldPackSnapshot resources, BedrockWorldPackSnapshot behaviors,
    IReadOnlyList<BedrockAddonDependent> dependents, IReadOnlyList<string> incompleteRoots) {
    public BedrockWorldPackSnapshot Resources { get; } = resources;
    public BedrockWorldPackSnapshot Behaviors { get; } = behaviors;
    public IReadOnlyList<BedrockAddonDependent> Dependents { get; } = dependents;
    public IReadOnlyList<string> IncompleteRoots { get; } = incompleteRoots;
}

/// <summary>Identifies declared UUID dependencies among entries in one world's current configuration.</summary>
public static class BedrockWorldDependents {
    public static BedrockWorldDependentsResult Check(IReadOnlyList<BedrockDataRoot> roots, BedrockDataRoot worldRoot,
        BedrockWorldInfo world, Guid uuid) {
        if (uuid == Guid.Empty) throw new InvalidDataException("世界依赖检查的 UUID 无效。");
        var resources = BedrockWorldPackEditor.Read(worldRoot, world, "resource");
        var behaviors = BedrockWorldPackEditor.Read(worldRoot, world, "behavior");
        var enabled = new HashSet<Guid>();
        AddIds(enabled, resources);
        AddIds(enabled, behaviors);
        enabled.Remove(uuid);
        // The world directory is not an installed pack path, so all installed candidates are examined.
        var result = BedrockAddonDependents.Check(roots, world.Path, uuid);
        var matches = result.Dependents.Where(item => enabled.Contains(item.Addon.Manifest!.Uuid)).ToArray();
        var afterResources = BedrockWorldPackEditor.Read(worldRoot, world, "resource");
        var afterBehaviors = BedrockWorldPackEditor.Read(worldRoot, world, "behavior");
        if (!SameSnapshot(resources, afterResources) || !SameSnapshot(behaviors, afterBehaviors))
            throw new IOException("世界附加包配置在依赖检查期间发生变化。");
        return new(resources, behaviors, matches, result.IncompleteRoots);
    }

    public static bool Equivalent(BedrockWorldDependentsResult before, BedrockWorldDependentsResult after) =>
        SameSnapshot(before.Resources, after.Resources) && SameSnapshot(before.Behaviors, after.Behaviors) &&
        before.IncompleteRoots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(after.IncompleteRoots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) &&
        before.Dependents.Select(Key).OrderBy(key => key, StringComparer.Ordinal)
            .SequenceEqual(after.Dependents.Select(Key).OrderBy(key => key, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string Key(BedrockAddonDependent item) => string.Join("\0", item.Root.Path.ToUpperInvariant(), item.Addon.Path.ToUpperInvariant(),
        item.Addon.Category, item.Addon.Manifest!.Uuid.ToString("D"), item.Addon.Manifest.Version,
        string.Join(",", item.Addon.Manifest.Modules), item.Dependency);

    private static bool SameSnapshot(BedrockWorldPackSnapshot left, BedrockWorldPackSnapshot right) =>
        left.Exists == right.Exists && left.Hash == right.Hash && left.Kind == right.Kind &&
        string.Equals(left.WorldPath, right.WorldPath, StringComparison.OrdinalIgnoreCase);

    private static void AddIds(HashSet<Guid> ids, BedrockWorldPackSnapshot snapshot) {
        if (!snapshot.Exists) return;
        var entries = JArray.Parse(new UTF8Encoding(false, true).GetString(snapshot.Bytes).TrimStart('\uFEFF'));
        foreach (JObject entry in entries.Cast<JObject>()) ids.Add(Guid.Parse(entry.Value<string>("pack_id")!));
    }
}
