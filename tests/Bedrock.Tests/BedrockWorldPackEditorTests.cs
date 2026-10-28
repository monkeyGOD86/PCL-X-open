using PCLCS.Bedrock;

namespace Bedrock.Tests;

public sealed class BedrockWorldPackEditorTests {
    private static readonly BedrockPackInfo Pack = new("fixture", "", Guid.Parse("11111111-1111-4111-8111-111111111111"),
        "1.2.3", ["resources"], [], null);

    public void UpdatesAndRestoresWorldConfigWithoutDiscardingFields() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        string original = "[{\"pack_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"version\":[1,0,0],\"custom\":{\"keep\":true}}]";
        File.WriteAllText(config, original);
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        var change = BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup);
        Check(change.BackupPath is not null && File.Exists(change.BackupPath), "external backup missing");
        Check(File.ReadAllText(config).Contains("\"custom\"") && File.ReadAllText(config).Contains(Pack.Uuid.ToString()), "existing or new data missing");
        Check(change.DisplacedPath is not null && File.ReadAllText(change.DisplacedPath) == original, "displaced original missing");
        File.AppendAllText(config, " ");
        try { BedrockWorldPackEditor.Restore(root, world, change.BackupPath!); throw new Exception("changed configuration was overwritten"); }
        catch (IOException) { }
        File.WriteAllText(config, File.ReadAllText(config).TrimEnd());
        string displaced = BedrockWorldPackEditor.Restore(root, world, change.BackupPath!);
        Check(File.ReadAllText(config) == original && File.Exists(displaced), "backup did not restore original bytes");
    });

    public void CreatesAndRevertsMissingConfigWithoutDeletingIt() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        Check(!snapshot.Exists, "missing configuration treated as existing");
        var change = BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup);
        Check(File.Exists(config), "new configuration missing");
        string displaced = BedrockWorldPackEditor.Restore(root, world, change.BackupPath!);
        Check(!File.Exists(config) && File.ReadAllText(displaced).Contains(Pack.Uuid.ToString()), "new configuration was not preserved on restore");
    });

    public void RejectsStaleSnapshotAndConflictingVersion() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        File.WriteAllText(config, "[]");
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        File.WriteAllText(config, "[ ]");
        try { BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup); throw new Exception("stale snapshot accepted"); }
        catch (IOException) { }
        Check(!Directory.EnumerateFiles(backup).Any(), "stale snapshot created a backup");
        File.WriteAllText(config, "[{\"pack_id\":\"11111111-1111-4111-8111-111111111111\",\"version\":[2,0,0]}]");
        snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        try { BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup); throw new Exception("conflicting version accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(config).Contains("2,0,0"), "existing version was changed");
    });

    public void DisablesOnlySelectedPackAndRejectsTamperedBackup() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        string original = "[{\"pack_id\":\"11111111-1111-4111-8111-111111111111\",\"version\":[1,2,3]}," +
            "{\"pack_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"version\":[1,0,0],\"extra\":42}]";
        File.WriteAllText(config, original);
        Check(!Directory.Exists(Path.Combine(root.Path, "resource_packs")), "orphan-reference fixture unexpectedly has installed packs");
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        var change = BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, false, backup);
        string updated = File.ReadAllText(config);
        Check(!updated.Contains(Pack.Uuid.ToString()) && updated.Contains("\"extra\""), "disable changed unrelated pack");
        File.WriteAllText(change.BackupPath!, File.ReadAllText(change.BackupPath!).Replace("\"originalHash\":\"", "\"originalHash\":\"0"));
        try { BedrockWorldPackEditor.Restore(root, world, change.BackupPath!); throw new Exception("tampered backup accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(config) == updated, "tampered restore changed configuration");
    });

    public void RejectsBackupInGameDataAndDuplicateUuid() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        File.WriteAllText(config, "[]");
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        try { BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, root.Path); throw new Exception("game data backup accepted"); }
        catch (InvalidDataException) { }
        string duplicate = "{\"pack_id\":\"11111111-1111-4111-8111-111111111111\",\"version\":[1,2,3]}";
        File.WriteAllText(config, "[" + duplicate + "," + duplicate + "]");
        snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        try { BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, false, backup); throw new Exception("duplicate UUID accepted"); }
        catch (InvalidDataException) { }
        Check(!Directory.EnumerateFiles(backup).Any(), "invalid edit wrote a backup");
    });

    public void RefusesGlobalOrUnknownResourceScopeInWorld() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        File.WriteAllText(config, "[]");
        foreach (string scope in new[] { "global", "future-scope" }) {
            var pack = new BedrockPackInfo("fixture", "", Pack.Uuid, "1.2.3", ["resources"], [], null, scope);
            var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
            try { BedrockWorldPackEditor.SetEnabled(root, world, snapshot, pack, true, backup); throw new Exception("restricted scope accepted"); }
            catch (InvalidDataException) { }
        }
        Check(File.ReadAllText(config) == "[]" && !Directory.EnumerateFiles(backup).Any(), "restricted scope changed world data");
    });

    public void BacksUpWorldBeforeConfigChangeAndRestoresBoth() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        string original = "[]";
        File.WriteAllText(config, original);
        string worldBackup = Path.Combine(backup, "world.zip");
        BedrockWorldBackup.Create(root, world, worldBackup);
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        var change = BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup);
        Check(File.ReadAllText(config).Contains(Pack.Uuid.ToString()), "config did not change");
        string restoredWorld = BedrockWorldBackup.RestoreAsNew(root, worldBackup);
        Check(File.ReadAllText(Path.Combine(restoredWorld, "world_resource_packs.json")) == original, "full world backup lost original config");
        BedrockWorldPackEditor.Restore(root, world, change.BackupPath!);
        Check(File.ReadAllText(config) == original, "config backup did not restore original config");
    });

    public void RefusesOccupiedWorldConfigBackupPaths() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        File.WriteAllText(config, "[]");
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        string? stage = null;
        try {
            BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup, path => {
                stage = path;
                File.WriteAllText(Path.Combine(path, "original.json"), "do not overwrite");
            });
            throw new Exception("occupied backup accepted");
        } catch (IOException) { }
        Check(File.ReadAllText(config) == "[]" && stage is not null &&
            File.ReadAllText(Path.Combine(stage, "original.json")) == "do not overwrite", "preexisting data was overwritten");
        var change = BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup);
        string updated = File.ReadAllText(config);
        string? restoreStage = null;
        try {
            BedrockWorldPackEditor.Restore(root, world, change.BackupPath!, path => {
                restoreStage = path;
                File.WriteAllText(Path.Combine(path, "previous.json"), "keep restore file");
            });
            throw new Exception("occupied restore backup accepted");
        } catch (IOException) { }
        Check(File.ReadAllText(config) == updated && restoreStage is not null &&
            File.ReadAllText(Path.Combine(restoreStage, "previous.json")) == "keep restore file", "restore overwrote existing data");
    });

    public void SerializesWorldBackupWithConfigChanges() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json"), archive = Path.Combine(backup, "world.zip");
        File.WriteAllText(config, "[]");
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        using var paused = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var editStarted = new ManualResetEventSlim();
        Task<BedrockWorldPackChange>? edit = null;
        var create = Task.Run(() => BedrockWorldBackup.Create(root, world, archive, () => {
            paused.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("backup test was not released");
        }));
        try {
            Check(paused.Wait(TimeSpan.FromSeconds(10)), "backup did not reach verification");
            edit = Task.Run(() => { editStarted.Set(); return BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup); });
            Check(editStarted.Wait(TimeSpan.FromSeconds(10)) && !edit.Wait(250), "world edit was not serialized behind backup");
        } finally {
            release.Set();
            Task.WaitAll(edit is null ? [create] : [create, edit]);
        }
        Check(BedrockWorldBackup.Verify(archive).Files > 0 && File.ReadAllText(config).Contains(Pack.Uuid.ToString()), "serialized operations failed");
        string restored = BedrockWorldBackup.RestoreAsNew(root, archive);
        Check(File.ReadAllText(Path.Combine(restored, "world_resource_packs.json")) == "[]", "backup included later edit");
    });

    public void LocksAllCandidateRootsThroughWorldEdit() => WithFixture((root, world, backup) => {
        string otherPath = Path.Combine(Path.GetDirectoryName(root.Path)!, "other");
        Directory.CreateDirectory(otherPath);
        var other = new BedrockDataRoot(otherPath, "fixture");
        try { using var invalid = BedrockOperationScope.Acquire([root, root]); throw new Exception("duplicate roots accepted"); }
        catch (InvalidDataException) { }
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        File.WriteAllText(config, "[]");
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        using var started = new ManualResetEventSlim();
        Task? competitor = null;
        bool blocked = false;
        try {
            using (BedrockOperationScope.Acquire([root, other])) {
                competitor = Task.Run(() => { started.Set(); using var scope = BedrockOperationScope.Acquire([other, root]); });
                blocked = started.Wait(TimeSpan.FromSeconds(10)) && !competitor.Wait(250);
                BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup);
            }
        } finally { competitor?.Wait(); }
        Check(blocked && File.ReadAllText(config).Contains(Pack.Uuid.ToString()), "candidate root lock did not serialize the world edit");
    });

    public void SerializesWorldRestoreWithOtherRootOperations() => WithFixture((root, world, backup) => {
        string archive = Path.Combine(backup, "world.zip");
        BedrockWorldBackup.Create(root, world, archive);
        using var started = new ManualResetEventSlim();
        Task<string> restore;
        bool blocked;
        using (BedrockRootLock.Acquire(root.Path)) {
            restore = Task.Run(() => { started.Set(); return BedrockWorldBackup.RestoreAsNew(root, archive); });
            blocked = started.Wait(TimeSpan.FromSeconds(10)) && !restore.Wait(250);
        }
        restore.Wait();
        Check(blocked && File.Exists(Path.Combine(restore.Result, "level.dat")), "world restore ignored the root lock or did not complete");
    });

    public void ChecksWorldDependencyVersionsAndDetectsChanges() => WithFixture((root, world, _) => {
        string resources = Path.Combine(world.Path, "world_resource_packs.json");
        string behaviors = Path.Combine(world.Path, "world_behavior_packs.json");
        string first = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", second = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        string third = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        File.WriteAllText(resources, "[{\"pack_id\":\"" + first + "\",\"version\":[1,2,3]}]");
        File.WriteAllText(behaviors, "[{\"pack_id\":\"" + second + "\",\"version\":[4,0,0]}]");
        var pack = new BedrockPackInfo("fixture", "", Pack.Uuid, "1.2.3", ["resources"],
            [first + " 1.2.3", second + " 1.0.0", third + " 1.0.0", "@minecraft/server 1.2.0"], null);
        var before = BedrockWorldDependencyCheck.Check(root, world, pack);
        Check(before.Dependencies[0].Status.Contains("已列出") && before.Dependencies[0].Status.Contains("未验证"), "matching world reference not reported");
        Check(before.Dependencies[1].Status.Contains("版本") && before.Dependencies[2].Status.Contains("未列出") &&
            before.Dependencies[3].Status.Contains("无法验证"), "dependency mismatch or unknown module not reported");
        File.WriteAllText(behaviors, "[{\"pack_id\":\"" + second + "\",\"version\":[4,0,0]}," +
            "{\"pack_id\":\"" + first + "\",\"version\":[1,2,3]}]");
        var after = BedrockWorldDependencyCheck.Check(root, world, pack);
        Check(after.Dependencies[0].Status.Contains("重复") && !BedrockWorldDependencyCheck.Equivalent(before, after), "duplicate UUID or config change not detected");
        var firstReport = new BedrockDependencyReport([new BedrockDependencyResult(first, "found")], [root.Path]);
        var secondReport = new BedrockDependencyReport([new BedrockDependencyResult(first, "missing")], [root.Path]);
        Check(!BedrockDependencyCheck.Equivalent(firstReport, secondReport), "installed dependency change was not detected");
    });

    public void PreservesConcurrentChangesDuringConfigReplacement() => WithFixture((root, world, backup) => {
        string config = Path.Combine(world.Path, "world_resource_packs.json");
        File.WriteAllText(config, "[]");
        var snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        string? stage = null;
        try {
            BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup, path => {
                stage = path;
                File.WriteAllText(config, "[ ]");
            });
            throw new Exception("concurrent change was not reported");
        } catch (IOException) { }
        Check(stage is not null && File.ReadAllText(Path.Combine(stage, "original.json")) == "[ ]",
            "concurrent original was not preserved");
        Check(File.ReadAllText(config).Contains(Pack.Uuid.ToString()), "replacement state was not preserved for inspection");

        File.WriteAllText(config, "[]");
        snapshot = BedrockWorldPackEditor.Read(root, world, "resource");
        var change = BedrockWorldPackEditor.SetEnabled(root, world, snapshot, Pack, true, backup);
        string? restoreStage = null;
        string concurrent = File.ReadAllText(config) + " ";
        try {
            BedrockWorldPackEditor.Restore(root, world, change.BackupPath!, path => {
                restoreStage = path;
                File.WriteAllText(config, concurrent);
            });
            throw new Exception("concurrent restore change was not reported");
        } catch (IOException) { }
        Check(restoreStage is not null && File.ReadAllText(Path.Combine(restoreStage, "previous.json")) == concurrent,
            "concurrent restore state was not preserved");
        Check(File.ReadAllText(config) == "[]", "restored state was not preserved for inspection");
    });

    private static void WithFixture(Action<BedrockDataRoot, BedrockWorldInfo, string> run) {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-world-config-test-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "game"), world = Path.Combine(root, "minecraftWorlds", "test"), backup = Path.Combine(fixture, "backups");
        Directory.CreateDirectory(world); Directory.CreateDirectory(backup);
        try {
            File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
            run(new BedrockDataRoot(root, "fixture"), new BedrockWorldInfo(world, "fixture", true, [], [], null), backup);
            Check(File.ReadAllText(Path.Combine(world, "level.dat")) == "fixture", "world data changed");
        } finally { TestPaths.DeleteFixture(fixture); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
