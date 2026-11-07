namespace PCLCS.PackSync;

public enum PackSyncChoice { KeepLocal, UseTeam }

public sealed class PackSyncPlannedChange {
    public string Path { get; internal set; } = "";
    public string? BeforeSha512 { get; internal set; }
    public string? AfterSha512 { get; internal set; }
}

/// <summary>A confirmed, immutable description of the intended changes, not an instruction to mutate files.</summary>
public sealed class PackSyncOperationPlan {
    public string ArchiveSha512 { get; internal set; } = "";
    public Guid PackId { get; internal set; }
    public IReadOnlyList<PackSyncPlannedChange> Changes { get; internal set; } = Array.Empty<PackSyncPlannedChange>();
    public IReadOnlyDictionary<string, string> TargetBaseline { get; internal set; } = new Dictionary<string, string>();
    public IReadOnlyList<string> OptionalSelections { get; internal set; } = Array.Empty<string>();
    public IReadOnlyList<string> RetainedDifferences { get; internal set; } = Array.Empty<string>();
    private IReadOnlyDictionary<string, string> localSnapshot = new Dictionary<string, string>();
    private string instanceDirectory = "";
    private string stateSnapshot = "";

    /// <summary>Rechecks the current installer manifest while the instance is locked.</summary>
    public void ValidateComponents(IEnumerable<string> componentPaths) {
        if (componentPaths == null) throw new ArgumentNullException(nameof(componentPaths));
        var components = new HashSet<string>(componentPaths.Select(PackSyncPlanner.ValidateRelativePath), StringComparer.OrdinalIgnoreCase);
        var protectedChange = Changes.FirstOrDefault(change => components.Contains(change.Path));
        if (protectedChange != null)
            throw new InvalidDataException($"安装器组件不能原地修改，请重新导入实例：{protectedChange.Path}");
    }

    /// <summary>Re-scans the entire scope before mutation; call while holding the instance lock.</summary>
    public void ValidateStillCurrent(string archivePath, string instanceDirectory, string gameDirectory, IDictionary<string, string> environment) {
        var actual = PackSyncPreview.Create(archivePath, instanceDirectory, gameDirectory, environment, OptionalSelections);
        if (!string.Equals(this.instanceDirectory, actual.InstanceDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ArchiveSha512, actual.Package.ArchiveSha512, StringComparison.OrdinalIgnoreCase) || PackId != actual.Package.PackId ||
            stateSnapshot != actual.StateSnapshot || localSnapshot.Count != actual.LocalFiles.Count ||
            localSnapshot.Any(entry => !actual.LocalFiles.TryGetValue(entry.Key, out var hash) || !string.Equals(entry.Value, hash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("预览后文件或状态发生变化，请重新检查差异。");
    }

    /// <summary>Before committing, check that the complete local scope equals the confirmed result.</summary>
    public void ValidateApplied(string archivePath, string instanceDirectory, string gameDirectory, IDictionary<string, string> environment) {
        var actual = PackSyncPreview.Create(archivePath, instanceDirectory, gameDirectory, environment, OptionalSelections);
        var expected = localSnapshot.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var change in Changes) {
            if (change.AfterSha512 == null) expected.Remove(change.Path);
            else expected[change.Path] = change.AfterSha512;
        }
        if (!string.Equals(this.instanceDirectory, actual.InstanceDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ArchiveSha512, actual.Package.ArchiveSha512, StringComparison.OrdinalIgnoreCase) || PackId != actual.Package.PackId ||
            stateSnapshot != actual.StateSnapshot || expected.Count != actual.LocalFiles.Count ||
            expected.Any(entry => !actual.LocalFiles.TryGetValue(entry.Key, out var hash) || !string.Equals(entry.Value, hash, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("应用期间文件或状态发生变化，无法提交同步结果。");
    }

    /// <summary>Requires explicit decisions for conflicts and extra mods, and rejects installer component changes.</summary>
    public static PackSyncOperationPlan Create(PackSyncPreviewResult preview, IEnumerable<string> selectedOptionalPaths,
        IDictionary<string, PackSyncChoice> decisions, IEnumerable<string> componentPaths) {
        if (preview == null) throw new ArgumentNullException(nameof(preview));
        if (selectedOptionalPaths == null) throw new ArgumentNullException(nameof(selectedOptionalPaths));
        if (decisions == null) throw new ArgumentNullException(nameof(decisions));
        if (componentPaths == null) throw new ArgumentNullException(nameof(componentPaths));
        var optional = new HashSet<string>(selectedOptionalPaths, StringComparer.OrdinalIgnoreCase);
        if (!optional.SetEquals(preview.OptionalSelections)) throw new InvalidDataException("可选文件选择已改变，请重新检查差异。");
        foreach (string path in optional) {
            PackSyncPlanner.ValidatePath(path);
            if (!preview.Package.Files.Any(f => f.Optional && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"所选可选文件不存在：{path}");
        }
        var choices = new Dictionary<string, PackSyncChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in decisions) {
            string path = PackSyncPlanner.ValidatePath(item.Key);
            if (!Enum.IsDefined(typeof(PackSyncChoice), item.Value) || choices.ContainsKey(path))
                throw new InvalidDataException($"文件处理选择无效：{path}");
            choices.Add(path, item.Value);
        }
        var components = new HashSet<string>(componentPaths.Select(PackSyncPlanner.ValidateRelativePath), StringComparer.OrdinalIgnoreCase);
        var target = preview.Package.Files.Where(f => !f.Optional || optional.Contains(f.Path))
            .ToDictionary(f => f.Path, f => f.Sha512, StringComparer.OrdinalIgnoreCase);
        // A preview calculated with different optional selections cannot safely be confirmed.
        foreach (var difference in preview.Differences) {
            target.TryGetValue(difference.Path, out var desired);
            if (!string.Equals(desired, difference.TargetSha512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("可选文件选择已改变，请重新检查差异。");
        }
        var changes = new List<PackSyncPlannedChange>();
        var retained = new List<string>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var diff in preview.Differences) {
            bool extraMod = diff.Kind == PackSyncDifferenceKind.LocalExtra && diff.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase);
            bool needsChoice = diff.Kind == PackSyncDifferenceKind.Conflict || extraMod;
            bool apply;
            if (needsChoice) {
                if (!choices.TryGetValue(diff.Path, out var choice)) throw new InvalidDataException($"请选择文件的处理方式：{diff.Path}");
                consumed.Add(diff.Path);
                apply = choice == PackSyncChoice.UseTeam;
            } else apply = diff.Kind != PackSyncDifferenceKind.LocalExtra;
            if (!apply) { retained.Add(diff.Path); continue; }
            if (components.Contains(diff.Path)) throw new InvalidDataException($"安装器组件不能原地修改，请重新导入实例：{diff.Path}");
            changes.Add(new PackSyncPlannedChange { Path = diff.Path, BeforeSha512 = diff.LocalSha512, AfterSha512 = diff.TargetSha512 });
        }
        if (choices.Keys.Any(path => !consumed.Contains(path))) throw new InvalidDataException("文件处理选择已过期，请重新检查差异。");
        return new PackSyncOperationPlan { ArchiveSha512 = preview.Package.ArchiveSha512, PackId = preview.Package.PackId,
            Changes = changes.AsReadOnly(), TargetBaseline = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(target),
            OptionalSelections = optional.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(), RetainedDifferences = retained.AsReadOnly(),
            localSnapshot = preview.LocalFiles, instanceDirectory = preview.InstanceDirectory, stateSnapshot = preview.StateSnapshot };
    }
}
