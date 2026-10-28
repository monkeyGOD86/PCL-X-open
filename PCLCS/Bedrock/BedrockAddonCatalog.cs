namespace PCLCS.Bedrock;

public sealed class BedrockInstalledAddon(string path, string category, BedrockPackInfo? manifest, string? error) {
    public string Path { get; } = path;
    public string Category { get; } = category;
    public BedrockPackInfo? Manifest { get; } = manifest;
    public string? Error { get; } = error;
    public IReadOnlyList<string> Warnings { get; internal set; } = [];
}

/// <summary>Lists local packs and reports simple UUID/version and dependency conflicts.</summary>
public static class BedrockAddonCatalog {
    private static readonly (string Folder, string Label)[] Folders = [
        ("resource_packs", "资源包"), ("behavior_packs", "行为包"),
        ("development_resource_packs", "开发资源包"), ("development_behavior_packs", "开发行为包")
    ];

    public static IReadOnlyList<BedrockInstalledAddon> Read(BedrockDataRoot root) {
        EnsureRootDirectory(root.Path);
        var result = new List<BedrockInstalledAddon>();
        foreach (var (folder, label) in Folders) {
            string parent = System.IO.Path.Combine(root.Path, folder);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(parent); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("附加包分类路径被文件占用，无法完整检查：" + parent);
            BedrockDataRootFinder.EnsureNoReparsePoints(parent);
            foreach (string path in Directory.EnumerateDirectories(parent)) {
                if (result.Count >= 4096) throw new InvalidDataException("附加包数量超过读取限制。");
                try {
                    BedrockDataRootFinder.EnsureNoReparsePoints(path);
                    string manifestPath = System.IO.Path.Combine(path, "manifest.json");
                    if (ManifestIsPresent(manifestPath)) {
                        AddPackage(result, path, label);
                    } else {
                        // Current Windows installs can use <pack>/<version>/manifest.json.
                        int before = result.Count;
                        int children = 0;
                        foreach (string child in Directory.EnumerateDirectories(path)) {
                            if (++children > 128 || result.Count >= 4096) throw new InvalidDataException("附加包版本数量超过读取限制。");
                            BedrockDataRootFinder.EnsureNoReparsePoints(child);
                            if (ManifestIsPresent(System.IO.Path.Combine(child, "manifest.json"))) AddPackage(result, child, label);
                            else result.Add(new(child, label, null, "无法读取或识别包清单。"));
                        }
                        if (result.Count == before) result.Add(new(path, label, null, "无法读取或识别包清单。"));
                    }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or InvalidDataException) {
                    result.Add(new(path, label, null, "无法读取或识别包清单。"));
                }
            }
        }
        EnsureRootDirectory(root.Path);
        var byUuid = result.Where(item => item.Manifest is not null).GroupBy(item => item.Manifest!.Uuid)
            .ToDictionary(group => group.Key, group => group.ToArray());
        bool incomplete = result.Any(item => item.Error is not null);
        foreach (var item in result.Where(item => item.Manifest is not null)) {
            var warnings = new List<string>();
            if (incomplete) warnings.Add("部分包无法识别，冲突和依赖检查可能不完整。");
            var manifest = item.Manifest!;
            var duplicates = byUuid[manifest.Uuid];
            if (duplicates.Length > 1) {
                bool different = duplicates.Select(other => other.Manifest!.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
                warnings.Add(different ? "相同 UUID 存在不同版本。" : "相同 UUID 和版本存在多个副本。");
            }
            if (item.Category.Contains("资源") && !manifest.Modules.Contains("resources") ||
                item.Category.Contains("行为") && !manifest.Modules.Contains("data"))
                warnings.Add("包类型与所在目录不一致。");
            foreach (string dependency in manifest.Dependencies) {
                string[] parts = dependency.Split(new[] { ' ' }, 2);
                if (!Guid.TryParse(parts[0], out Guid id)) continue; // Platform script modules need separate validation.
                if (!byUuid.TryGetValue(id, out var candidates)) warnings.Add("所选目录未发现依赖（其他目录可能提供）：" + parts[0]);
                else if (parts.Length > 1 && !candidates.Any(candidate => candidate.Manifest!.Version == parts[1]))
                    warnings.Add("依赖版本不匹配：" + dependency);
            }
            item.Warnings = warnings;
        }
        return result;
    }

    private static void EnsureRootDirectory(string path) {
        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
            throw new InvalidDataException("数据目录不可用，无法完整检查附加包。");
        BedrockDataRootFinder.EnsureNoReparsePoints(path);
    }

    private static bool ManifestIsPresent(string path) {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("附加包清单路径不是普通文件。");
        return true;
    }

    private static void AddPackage(List<BedrockInstalledAddon> result, string path, string label) {
        try {
            var manifest = BedrockPackInspector.InspectManifestFile(System.IO.Path.Combine(path, "manifest.json"));
            result.Add(new(path, label, manifest, null));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or InvalidDataException) {
            result.Add(new(path, label, null, "无法读取或识别包清单。"));
        }
    }
}
