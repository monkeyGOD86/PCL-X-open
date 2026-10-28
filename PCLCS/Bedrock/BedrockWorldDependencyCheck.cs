using System.Text;

namespace PCLCS.Bedrock;

public sealed class BedrockWorldDependencyResult(BedrockWorldPackSnapshot resources, BedrockWorldPackSnapshot behaviors,
    IReadOnlyList<BedrockDependencyResult> dependencies) {
    public BedrockWorldPackSnapshot Resources { get; } = resources;
    public BedrockWorldPackSnapshot Behaviors { get; } = behaviors;
    public IReadOnlyList<BedrockDependencyResult> Dependencies { get; } = dependencies;
}

/// <summary>Compares declared UUID dependencies with one world's configuration; this does not verify game activation.</summary>
public static class BedrockWorldDependencyCheck {
    public static BedrockWorldDependencyResult Check(BedrockDataRoot root, BedrockWorldInfo world, BedrockPackInfo pack) {
        var resources = BedrockWorldPackEditor.Read(root, world, "resource");
        var behaviors = BedrockWorldPackEditor.Read(root, world, "behavior");
        var references = ReadReferences(resources).Concat(ReadReferences(behaviors)).ToArray();
        var results = new List<BedrockDependencyResult>();
        foreach (string dependency in pack.Dependencies) {
            string[] parts = dependency.Split(new[] { ' ' }, 2);
            if (!Guid.TryParse(parts[0], out Guid uuid)) {
                results.Add(new(dependency, "平台模块：世界配置无法验证"));
                continue;
            }
            var matches = references.Where(item => item.Id == uuid).ToArray();
            string status = matches.Length == 0 ? "世界配置未列出此 UUID" :
                matches.Length > 1 ? "世界配置存在重复 UUID，依赖状态不明确" :
                parts.Length > 1 && matches[0].Version != parts[1] ? "世界配置列出的版本与声明不同" :
                "世界配置已列出；游戏是否实际启用未验证";
            results.Add(new(dependency, status));
        }
        var currentResources = BedrockWorldPackEditor.Read(root, world, "resource");
        var currentBehaviors = BedrockWorldPackEditor.Read(root, world, "behavior");
        if (!SameSnapshot(resources, currentResources) || !SameSnapshot(behaviors, currentBehaviors))
            throw new IOException("世界附加包配置在依赖检查期间发生变化。");
        return new(resources, behaviors, results);
    }

    public static bool Equivalent(BedrockWorldDependencyResult left, BedrockWorldDependencyResult right) =>
        SameSnapshot(left.Resources, right.Resources) && SameSnapshot(left.Behaviors, right.Behaviors) &&
        left.Dependencies.Select(item => (item.Dependency, item.Status)).SequenceEqual(right.Dependencies.Select(item => (item.Dependency, item.Status)));

    private static bool SameSnapshot(BedrockWorldPackSnapshot left, BedrockWorldPackSnapshot right) =>
        left.Exists == right.Exists && left.Hash == right.Hash && left.Kind == right.Kind &&
        string.Equals(left.WorldPath, right.WorldPath, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(Guid Id, string Version)> ReadReferences(BedrockWorldPackSnapshot snapshot) {
        if (!snapshot.Exists) return [];
        var entries = JArray.Parse(new UTF8Encoding(false, true).GetString(snapshot.Bytes).TrimStart('\uFEFF'));
        return entries.Cast<JObject>().Select(entry => (Guid.Parse(entry.Value<string>("pack_id")!),
            string.Join(".", ((JArray)entry["version"]!).Select(item => item.Value<int>().ToString(System.Globalization.CultureInfo.InvariantCulture))))).ToArray();
    }
}
