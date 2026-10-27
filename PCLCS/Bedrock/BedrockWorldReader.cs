using System.Text;

namespace PCLCS.Bedrock;

public sealed class BedrockDataRoot(string path, string source) {
    public string Path { get; } = path;
    public string Source { get; } = source;
}

public sealed class BedrockWorldInfo(string path, string name, bool hasLevelData,
    IReadOnlyList<string> resourcePacks, IReadOnlyList<string> behaviorPacks, string? warning) {
    public string Path { get; } = path;
    public string Name { get; } = name;
    public bool HasLevelData { get; } = hasLevelData;
    public IReadOnlyList<string> ResourcePacks { get; } = resourcePacks;
    public IReadOnlyList<string> BehaviorPacks { get; } = behaviorPacks;
    public string? Warning { get; } = warning;
}

/// <summary>Discovers candidate roots; only the user can select which root to manage.</summary>
public sealed class BedrockDataRootFinder(string localAppData, string roamingAppData) {
    public BedrockDataRootFinder() : this(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) { }

    public IReadOnlyList<BedrockDataRoot> Find(BedrockInstallation installation) {
        if (installation.FamilyName is not ("Microsoft.MinecraftUWP_8wekyb3d8bbwe" or "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe"))
            throw new ArgumentException("不支持的游戏安装。", nameof(installation));
        var result = new List<BedrockDataRoot>();
        string uwp = System.IO.Path.Combine(localAppData, "Packages", installation.FamilyName, "LocalState", "games", "com.mojang");
        AddIfPresent(result, uwp, "旧 UWP 候选");
        string productFolder = installation.FamilyName.StartsWith("Microsoft.MinecraftWindowsBeta_", StringComparison.Ordinal)
            ? "Minecraft Bedrock Preview" : "Minecraft Bedrock";
        string users = System.IO.Path.Combine(roamingAppData, productFolder, "Users");
        if (Directory.Exists(users)) {
            EnsureNoReparsePoints(users);
            int scanned = 0;
            foreach (string user in Directory.EnumerateDirectories(users).Take(129)) {
                if (++scanned > 128) throw new InvalidDataException("发现过多的数据目录。");
                AddIfPresent(result, System.IO.Path.Combine(user, "games", "com.mojang"), "GDK 用户目录候选");
            }
        }
        return result;
    }

    private static void AddIfPresent(List<BedrockDataRoot> result, string path, string source) {
        if (!Directory.Exists(path)) return;
        EnsureNoReparsePoints(path);
        result.Add(new(System.IO.Path.GetFullPath(path), source));
    }

    internal static void EnsureNoReparsePoints(string path) {
        string? current = System.IO.Path.GetFullPath(path);
        while (current is not null) {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("数据路径包含链接或重解析点，无法安全访问。");
            current = Directory.GetParent(current)?.FullName;
        }
    }
}

/// <summary>Reads world names and configured pack references without writing game data.</summary>
public static class BedrockWorldReader {
    public static IReadOnlyList<BedrockWorldInfo> Read(BedrockDataRoot root) {
        BedrockDataRootFinder.EnsureNoReparsePoints(root.Path);
        string worldRoot = System.IO.Path.Combine(root.Path, "minecraftWorlds");
        if (!Directory.Exists(worldRoot)) return [];
        BedrockDataRootFinder.EnsureNoReparsePoints(worldRoot);
        var worlds = new List<BedrockWorldInfo>();
        int totalReferences = 0;
        int scanned = 0;
        foreach (string worldPath in Directory.EnumerateDirectories(worldRoot).Take(1025)) {
            if (++scanned > 1024) throw new InvalidDataException("世界数量超过读取限制。");
            if ((File.GetAttributes(worldPath) & FileAttributes.ReparsePoint) != 0) continue;
            string name = System.IO.Path.GetFileName(worldPath);
            bool hasLevel = File.Exists(System.IO.Path.Combine(worldPath, "level.dat"));
            string? warning = null;
            IReadOnlyList<string> resources = [], behaviors = [];
            try {
                string namePath = System.IO.Path.Combine(worldPath, "levelname.txt");
                if (File.Exists(namePath)) {
                    string contents = ReadText(namePath, 4096).Trim();
                    if (contents.Length > 0) name = contents;
                }
                resources = ReadPackReferences(System.IO.Path.Combine(worldPath, "world_resource_packs.json"));
                behaviors = ReadPackReferences(System.IO.Path.Combine(worldPath, "world_behavior_packs.json"));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or InvalidDataException) {
                warning = "世界名称或附加包配置无法读取。";
            }
            totalReferences += resources.Count + behaviors.Count;
            if (totalReferences > 8192) throw new InvalidDataException("世界附加包配置数量超过读取限制。");
            worlds.Add(new(System.IO.Path.GetFullPath(worldPath), name, hasLevel, resources, behaviors, warning));
        }
        return worlds.OrderBy(world => world.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> ReadPackReferences(string path) {
        if (!File.Exists(path)) return [];
        string json = ReadText(path, 1024 * 1024);
        using var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 16, DateParseHandling = DateParseHandling.None };
        var array = JArray.Load(reader);
        if (reader.Read() || array.Count > 2048) throw new InvalidDataException("世界附加包配置无效。");
        var result = new List<string>();
        foreach (JToken item in array) {
            var pack = item as JObject ?? throw new InvalidDataException("世界附加包配置无效。");
            string? id = pack.Value<string>("pack_id");
            if (!Guid.TryParse(id, out _)) throw new InvalidDataException("世界附加包 UUID 无效。");
            var version = pack["version"];
            string versionText = version is JArray numbers && numbers.Count == 3 && numbers.All(n => n.Type == JTokenType.Integer)
                ? string.Join(".", numbers.Select(n => n.ToString())) : "未知版本";
            result.Add(id + " " + versionText);
        }
        return result;
    }

    private static string ReadText(string path, int maxBytes) {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("世界文件是链接，无法安全读取。");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (file.Length > maxBytes) throw new InvalidDataException("世界元数据过大。");
        using var content = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = file.Read(buffer, 0, buffer.Length)) > 0) {
            if (content.Length + count > maxBytes) throw new InvalidDataException("世界元数据过大。");
            content.Write(buffer, 0, count);
        }
        content.Position = 0;
        using var reader = new StreamReader(content, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }
}
