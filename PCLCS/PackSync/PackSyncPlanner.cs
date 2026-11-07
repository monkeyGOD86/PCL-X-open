using System.IO;

namespace PCLCS.PackSync;

/// <summary>A read-only comparison of the previous team baseline, local files and the new target.</summary>
public enum PackSyncDifferenceKind {
    MissingTeamFile,
    TeamUpdate,
    TeamRemoval,
    Conflict,
    LocalExtra
}

public sealed class PackSyncDifference {
    public string Path { get; }
    public string? BaselineSha512 { get; }
    public string? LocalSha512 { get; }
    public string? TargetSha512 { get; }
    public PackSyncDifferenceKind Kind { get; }

    internal PackSyncDifference(string path, string? baseline, string? local, string? target, PackSyncDifferenceKind kind) {
        Path = path;
        BaselineSha512 = baseline;
        LocalSha512 = local;
        TargetSha512 = target;
        Kind = kind;
    }
}

public static class PackSyncPlanner {
    /// <summary>
    /// Compares SHA-512 hashes without changing any files. An absent key means the file does not exist.
    /// Callers must supply a complete local scan of the allowed scope and the selected target files.
    /// </summary>
    public static IReadOnlyList<PackSyncDifference> Compare(
        IEnumerable<KeyValuePair<string, string>> baseline,
        IEnumerable<KeyValuePair<string, string>> local,
        IEnumerable<KeyValuePair<string, string>> target) {
        var b = ValidateFiles(baseline);
        var l = ValidateFiles(local);
        var t = ValidateFiles(target);
        var paths = new HashSet<string>(b.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(l.Keys);
        paths.UnionWith(t.Keys);
        var result = new List<PackSyncDifference>();
        foreach (string path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
            b.TryGetValue(path, out var oldHash);
            l.TryGetValue(path, out var localHash);
            t.TryGetValue(path, out var targetHash);
            if (Equal(localHash, targetHash)) continue;
            PackSyncDifferenceKind kind;
            if (oldHash == null && targetHash == null) kind = PackSyncDifferenceKind.LocalExtra;
            else if (oldHash == null && localHash == null) kind = PackSyncDifferenceKind.MissingTeamFile;
            else if (oldHash == null) kind = PackSyncDifferenceKind.Conflict;
            else if (targetHash == null && Equal(localHash, oldHash)) kind = PackSyncDifferenceKind.TeamRemoval;
            else if (targetHash != null && Equal(localHash, oldHash)) kind = PackSyncDifferenceKind.TeamUpdate;
            else kind = PackSyncDifferenceKind.Conflict;
            result.Add(new PackSyncDifference(path, oldHash, localHash, targetHash, kind));
        }
        return result;
    }

    /// <summary>Validates the first-version file scope and Windows-safe relative paths.</summary>
    public static string ValidatePath(string path) {
        path = ValidateRelativePath(path);
        var parts = path.Split('/');
        if (parts.Length < 2) throw new InvalidDataException($"无效的小队文件路径：{path}");
        bool mods = parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase) && parts.Length == 2 &&
                    parts[1].EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        bool shared = new[] { "config", "defaultconfigs", "kubejs", "scripts" }
            .Contains(parts[0], StringComparer.OrdinalIgnoreCase);
        if (!mods && !shared) throw new InvalidDataException($"文件不在小队同步范围内：{path}");
        return path;
    }

    internal static string ValidateRelativePath(string path) {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\\') >= 0 || path.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidDataException($"无效的小队文件路径：{path}");
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." ||
            p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal) ||
            p.Any(c => c < 32 || "<>:\"|?*".IndexOf(c) >= 0) || IsReservedName(p)))
            throw new InvalidDataException($"无效的小队文件路径：{path}");
        return string.Join("/", parts);
    }

    private static bool IsReservedName(string part) {
        string name = part.Split('.')[0];
        return new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ValidateFiles(IEnumerable<KeyValuePair<string, string>> files) {
        if (files == null) throw new ArgumentNullException(nameof(files));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) {
            string path = ValidatePath(file.Key);
            if (file.Value == null || file.Value.Length != 128 || file.Value.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException($"文件的 SHA-512 无效：{path}");
            if (result.ContainsKey(path)) throw new InvalidDataException($"文件路径重复：{path}");
            result.Add(path, file.Value.ToLowerInvariant());
        }
        return result;
    }

    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
