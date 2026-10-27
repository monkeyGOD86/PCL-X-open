namespace Bedrock.Tests;

internal static class TestPaths {
    public static void DeleteFixture(string path) {
        string target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string name = Path.GetFileName(target);
        if (!string.Equals(Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("pcl-", StringComparison.Ordinal) || name.Length < 36 ||
            !Guid.TryParseExact(name.Substring(name.Length - 32), "N", out _))
            throw new InvalidOperationException("Refusing to delete a non-fixture path");
        if (!Directory.Exists(target)) {
            if (File.Exists(target)) throw new InvalidOperationException("Fixture path is not a directory");
            return;
        }
        string? ancestor = target;
        while (ancestor is not null) {
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fixture path contains a link");
            ancestor = Directory.GetParent(ancestor)?.FullName;
        }
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.Count > 0) {
            foreach (string child in Directory.EnumerateFileSystemEntries(pending.Pop())) {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Fixture contains a link");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
            }
        }
        Directory.Delete(target, true);
    }
}
