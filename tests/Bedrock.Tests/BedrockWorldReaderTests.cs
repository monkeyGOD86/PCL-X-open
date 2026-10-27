using PCLCS.Bedrock;
using System.IO.Compression;

namespace Bedrock.Tests;

public sealed class BedrockWorldReaderTests {
    public void FindsCandidateAndReadsWorldPackReferences() => WithWorld((roaming, root, world) => {
        File.WriteAllText(Path.Combine(world, "levelname.txt"), "Test world");
        File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
        File.WriteAllText(Path.Combine(world, "world_resource_packs.json"), "[{\"pack_id\":\"11111111-1111-4111-8111-111111111111\",\"version\":[1,2,3]}]");
        var installation = new BedrockInstallation("Microsoft.MinecraftUWP_8wekyb3d8bbwe", "package", "app", "1", "x64", "正式版", "未知", "未知");
        var candidates = new BedrockDataRootFinder(Path.Combine(roaming, "local"), roaming).Find(installation);
        Check(candidates.Count == 1 && candidates[0].Path == root, "candidate root was not found");
        var item = BedrockWorldReader.Read(candidates[0]).Single();
        Check(item.Name == "Test world" && item.HasLevelData && item.Warning is null, "world metadata missing");
        Check(item.ResourcePacks.Single().EndsWith("1.2.3"), "world pack reference missing");
    });

    public void ReportsInvalidWorldMetadataWithoutDiscardingWorld() => WithWorld((_, root, world) => {
        File.WriteAllText(Path.Combine(world, "world_behavior_packs.json"), "[\"bad\"]");
        var item = BedrockWorldReader.Read(new BedrockDataRoot(root, "fixture")).Single();
        Check(item.Warning is not null, "invalid metadata was not reported");
    });

    public void CreatesAndVerifiesBackupWithoutChangingWorld() => WithWorld((temp, root, world) => {
        string source = Path.Combine(world, "level.dat");
        string backup = temp + ".zip";
        File.WriteAllText(source, "original data");
        try {
            var info = new BedrockWorldInfo(world, "Test world", true, [], [], null);
            var created = BedrockWorldBackup.Create(new BedrockDataRoot(root, "fixture"), info, backup);
            Check(created.Files == 1 && BedrockWorldBackup.Verify(backup).WorldName == "Test world", "backup did not verify");
            Check(File.ReadAllText(source) == "original data", "source world changed");
            using (var file = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Update)) {
                archive.GetEntry("world/level.dat")!.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("world/level.dat").Open());
                writer.Write("tampered data");
            }
            try { BedrockWorldBackup.Verify(backup); throw new Exception("tampered backup was accepted"); }
            catch (InvalidDataException) { }
        } finally { File.Delete(backup); }
    });

    public void BacksUpHighlyCompressibleWorldData() => WithWorld((temp, root, world) => {
        string source = Path.Combine(world, "level.dat");
        string backup = temp + ".zip";
        File.WriteAllText(source, new string('A', 2 * 1024 * 1024));
        try {
            var info = BedrockWorldBackup.Create(new BedrockDataRoot(root, "fixture"),
                new BedrockWorldInfo(world, "Test", true, [], [], null), backup);
            Check(info.Files == 1 && BedrockWorldBackup.Verify(backup).Files == 1, "valid repetitive world data was rejected");
            Check(new FileInfo(source).Length == 2 * 1024 * 1024, "source was changed");
        } finally { File.Delete(backup); }
    });

    public void RejectsBackupInsideGameData() => WithWorld((_, root, world) => {
        File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
        var info = new BedrockWorldInfo(world, "Test", true, [], [], null);
        try { BedrockWorldBackup.Create(new BedrockDataRoot(root, "fixture"), info, Path.Combine(root, "bad.zip")); }
        catch (InvalidDataException) { return; }
        throw new Exception("backup inside game data was accepted");
    });

    public void RefusesToOverwriteExistingBackup() => WithWorld((temp, root, world) => {
        File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
        string backup = temp + ".zip";
        File.WriteAllText(backup, "existing backup");
        try {
            var info = new BedrockWorldInfo(world, "Test", true, [], [], null);
            try { BedrockWorldBackup.Create(new BedrockDataRoot(root, "fixture"), info, backup); throw new Exception("existing backup was overwritten"); }
            catch (IOException) { Check(File.ReadAllText(backup) == "existing backup", "existing backup changed"); }
        } finally { File.Delete(backup); }
    });

    public void RestoresAsNewWithoutOverwritingOriginal() => WithWorld((temp, root, world) => {
        string source = Path.Combine(world, "level.dat");
        string backup = temp + ".zip";
        string? restored = null;
        File.WriteAllText(source, "saved state");
        string db = Path.Combine(world, "db");
        string empty = Path.Combine(db, "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(db, "data"), "database fixture");
        try {
            var dataRoot = new BedrockDataRoot(root, "fixture");
            BedrockWorldBackup.Create(dataRoot, new BedrockWorldInfo(world, "Test", true, [], [], null), backup);
            File.WriteAllText(source, "newer original");
            restored = BedrockWorldBackup.RestoreAsNew(dataRoot, backup);
            Check(File.ReadAllText(source) == "newer original", "restore changed the original world");
            Check(File.ReadAllText(Path.Combine(restored, "level.dat")) == "saved state", "restored content differs");
            Check(File.ReadAllText(Path.Combine(restored, "db", "data")) == "database fixture", "nested file differs");
            Check(Directory.Exists(Path.Combine(restored, "db", "empty")), "empty directory was lost");
            Check(Directory.GetParent(restored)!.FullName == Path.Combine(root, "minecraftWorlds"), "restored world has wrong parent");
        } finally {
            if (restored is not null) {
                File.Delete(Path.Combine(restored, "level.dat"));
                File.Delete(Path.Combine(restored, "db", "data"));
                Directory.Delete(Path.Combine(restored, "db", "empty"));
                Directory.Delete(Path.Combine(restored, "db"));
                Directory.Delete(restored);
            }
            File.Delete(Path.Combine(db, "data"));
            Directory.Delete(empty);
            Directory.Delete(db);
            File.Delete(backup);
        }
    });

    public void RejectsCorruptRestoreBeforeCreatingWorld() => WithWorld((temp, root, world) => {
        string backup = temp + ".zip";
        File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
        try {
            var dataRoot = new BedrockDataRoot(root, "fixture");
            BedrockWorldBackup.Create(dataRoot, new BedrockWorldInfo(world, "Test", true, [], [], null), backup);
            using (var file = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Update)) {
                archive.GetEntry("world/level.dat")!.Delete();
            }
            try { BedrockWorldBackup.RestoreAsNew(dataRoot, backup); throw new Exception("corrupt restore was accepted"); }
            catch (InvalidDataException) { }
            Check(Directory.GetDirectories(Path.Combine(root, "minecraftWorlds")).Length == 1, "unexpected world created");
            Check(Directory.GetDirectories(root, ".pcl-restore-*").Length == 0, "unexpected staging directory created");
        } finally { File.Delete(backup); }
    });

    public void RejectsSharedRootForWorldOperations() => WithWorld((_, root, world) => {
        File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
        var shared = new BedrockDataRoot(root, "shared", false);
        try { BedrockWorldReader.Read(shared); throw new Exception("shared root accepted for world listing"); }
        catch (InvalidDataException) { }
        try { BedrockWorldBackup.RestoreAsNew(shared, "missing.zip"); throw new Exception("shared root accepted for restoration"); }
        catch (InvalidDataException) { }
    });

    public void DistinguishesSharedContentDirectory() => WithWorld((roaming, _, _) => {
        string shared = Path.Combine(roaming, "Minecraft Bedrock", "Users", "shared");
        string games = Path.Combine(shared, "games");
        string root = Path.Combine(games, "com.mojang");
        Directory.CreateDirectory(root);
        try {
            var installation = new BedrockInstallation("Microsoft.MinecraftUWP_8wekyb3d8bbwe", "package", "app", "1", "x64", "正式版", "未知", "未知");
            var candidates = new BedrockDataRootFinder(Path.Combine(roaming, "local"), roaming).Find(installation);
            Check(candidates.Count == 2 && !candidates.Single(candidate => candidate.Path == root).SupportsWorlds,
                "shared content was not distinguished from user worlds");
        } finally { Directory.Delete(root); Directory.Delete(games); Directory.Delete(shared); }
    });

    public void DetectsWorldPackUsageAndIncompleteWorlds() => WithWorld((_, root, world) => {
        string config = Path.Combine(world, "world_resource_packs.json");
        Guid uuid = Guid.Parse("11111111-1111-4111-8111-111111111111");
        File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
        File.WriteAllText(config, "[{\"pack_id\":\"11111111-1111-4111-8111-111111111111\",\"version\":[1,2,3]}]");
        var dataRoot = new BedrockDataRoot(root, "fixture");
        var usage = BedrockWorldPackUsage.Check([dataRoot], uuid);
        Check(usage.Complete && usage.References.Count == 1 && usage.References[0].Kind == "resource", "world reference not detected");
        File.WriteAllText(config, "invalid");
        usage = BedrockWorldPackUsage.Check([dataRoot], uuid);
        Check(!usage.Complete, "unreadable world was treated as safe");
    });

    private static void WithWorld(Action<string, string, string> test) {
        string temp = Path.Combine(Path.GetTempPath(), "pcl-bedrock-world-test-" + Guid.NewGuid().ToString("N"));
        string product = Path.Combine(temp, "Minecraft Bedrock");
        string users = Path.Combine(product, "Users");
        string user = Path.Combine(users, "test-user");
        string games = Path.Combine(user, "games");
        string root = Path.Combine(games, "com.mojang");
        string worlds = Path.Combine(root, "minecraftWorlds");
        string world = Path.Combine(worlds, "test-world");
        Directory.CreateDirectory(world);
        try { test(temp, root, world); }
        finally {
            foreach (string name in new[] { "levelname.txt", "level.dat", "world_resource_packs.json", "world_behavior_packs.json" })
                File.Delete(Path.Combine(world, name));
            foreach (string directory in new[] { world, worlds, root, games, user, users, product, temp }) Directory.Delete(directory);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
