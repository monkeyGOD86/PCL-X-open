namespace PCLCS.Bedrock;

/// <summary>Locks candidate roots for synchronous operations by cooperating launchers.</summary>
public static class BedrockOperationScope {
    /// <remarks>The returned scope must be disposed on the thread that acquired it. It cannot lock the game or cloud sync.</remarks>
    public static IDisposable Acquire(IReadOnlyList<BedrockDataRoot> roots) {
        if (roots is null || roots.Count is < 1 or > 128) throw new InvalidDataException("数据目录范围无效。");
        string[] paths = roots.Select(root => Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new InvalidDataException("数据目录重复。");
        Validate(paths);
        IDisposable held = BedrockRootLock.AcquireAll(paths);
        try { Validate(paths); return held; }
        catch { held.Dispose(); throw; }
    }

    private static void Validate(IEnumerable<string> paths) {
        foreach (string path in paths) {
            if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw new InvalidDataException("数据目录不是文件夹。");
            BedrockDataRootFinder.EnsureNoReparsePoints(path);
        }
    }
}
