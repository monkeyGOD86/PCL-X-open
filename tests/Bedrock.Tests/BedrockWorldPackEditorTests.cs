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
