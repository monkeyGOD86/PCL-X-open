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
        string backup = Path.Combine(temp, "backup.zip");
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
        string backup = Path.Combine(temp, "backup.zip");
        File.WriteAllText(source, new string('A', 2 * 1024 * 1024));
        try {
            var info = BedrockWorldBackup.Create(new BedrockDataRoot(root, "fixture"),
                new BedrockWorldInfo(world, "Test", true, [], [], null), backup);
            Check(info.Files == 1 && BedrockWorldBackup.Verify(backup).Files == 1, "valid repetitive world data was rejected");
            Check(new FileInfo(source).Length == 2 * 1024 * 1024, "source was changed");
        } finally { File.Delete(backup); }
    });

    public void PreservesPartialBackupWhenWorldCannotBeBackedUp() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-b-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "g"), world = Path.Combine(root, "minecraftWorlds", "w");
        Directory.CreateDirectory(world);
        try {
            File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
            string nested = world;
            for (int i = 0; i < 66; i++) { nested = Path.Combine(nested, "d"); Directory.CreateDirectory(nested); }
            string output = Path.Combine(fixture, "backup.zip");
            try {
                BedrockWorldBackup.Create(new BedrockDataRoot(root, "fixture"), new BedrockWorldInfo(world, "fixture", true, [], [], null), output);
                throw new Exception("excessively deep world was backed up");
            } catch (IOException ex) { Check(ex.Message.Contains("临时文件"), "failure did not report preserved partial file"); }
            Check(!File.Exists(output) && Directory.GetFiles(fixture, "backup.zip.partial-*").Length == 1,
                "partial backup was not preserved");
            Check(File.ReadAllText(Path.Combine(world, "level.dat")) == "fixture", "source world changed");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RejectsWorldChangesBeforeBackupCompletion() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-world-mutation-test-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "game"), world = Path.Combine(root, "minecraftWorlds", "world");
        Directory.CreateDirectory(world);
        try {
            string level = Path.Combine(world, "level.dat");
            var rootInfo = new BedrockDataRoot(root, "fixture");
            var worldInfo = new BedrockWorldInfo(world, "fixture", true, [], [], null);
            foreach (bool changeContent in new[] { true, false }) {
                File.WriteAllText(level, "original");
                string output = Path.Combine(fixture, changeContent ? "content.zip" : "directory.zip");
                try {
                    BedrockWorldBackup.Create(rootInfo, worldInfo, output, () => {
                        if (changeContent) File.WriteAllText(level, "modified"); // Same length as original.
                        else Directory.CreateDirectory(Path.Combine(world, "new-directory"));
                    });
                    throw new Exception("world changed during backup but was accepted");
                } catch (IOException ex) { Check(ex.Message.Contains("临时文件"), "partial backup path not reported"); }
                Check(!File.Exists(output) && Directory.GetFiles(fixture, Path.GetFileName(output) + ".partial-*").Length == 1,
                    "changed world produced a completed backup or lost partial file");
            }
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RejectsBackupInsideGameData() => WithWorld((_, root, world) => {
        File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
        var info = new BedrockWorldInfo(world, "Test", true, [], [], null);
        try { BedrockWorldBackup.Create(new BedrockDataRoot(root, "fixture"), info, Path.Combine(root, "bad.zip")); }
        catch (InvalidDataException) { return; }
        throw new Exception("backup inside game data was accepted");
    });

    public void RejectsBackupInsideOtherProtectedGameRoot() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-world-roots-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game"), world = Path.Combine(game, "minecraftWorlds", "world");
        string other = Path.Combine(fixture, "other"), sibling = Path.Combine(fixture, "other-sibling");
        Directory.CreateDirectory(world); Directory.CreateDirectory(other); Directory.CreateDirectory(sibling);
        try {
            File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
            var root = new BedrockDataRoot(game, "account");
            var otherRoot = new BedrockDataRoot(other, "other account");
            var info = new BedrockWorldInfo(world, "fixture", true, [], [], null);
            string unsafeBackup = Path.Combine(other, "backup.zip");
            try { BedrockWorldBackup.Create(root, info, unsafeBackup, [root, otherRoot]); throw new Exception("other game root accepted"); }
            catch (InvalidDataException) { }
            try { BedrockWorldBackup.Create(root, info, Path.Combine(sibling, "backup.zip"), [otherRoot]); throw new Exception("scope without selected root accepted"); }
            catch (InvalidDataException) { }
            Check(!Directory.EnumerateFileSystemEntries(other).Any(), "rejected backup created files in other game root");
            string safeBackup = Path.Combine(sibling, "backup.zip");
            BedrockWorldBackup.Create(root, info, safeBackup, [root, otherRoot]);
            Check(File.Exists(safeBackup) && File.ReadAllText(Path.Combine(world, "level.dat")) == "fixture", "sibling backup failed or modified source");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RefusesToOverwriteExistingBackup() => WithWorld((temp, root, world) => {
        File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
        string backup = Path.Combine(temp, "backup.zip");
        File.WriteAllText(backup, "existing backup");
        try {
            var info = new BedrockWorldInfo(world, "Test", true, [], [], null);
            try { BedrockWorldBackup.Create(new BedrockDataRoot(root, "fixture"), info, backup); throw new Exception("existing backup was overwritten"); }
            catch (IOException) { Check(File.ReadAllText(backup) == "existing backup", "existing backup changed"); }
        } finally { File.Delete(backup); }
    });

    public void RestoresAsNewWithoutOverwritingOriginal() => WithWorld((temp, root, world) => {
        string source = Path.Combine(world, "level.dat");
        string backup = Path.Combine(temp, "backup.zip");
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
        string backup = Path.Combine(temp, "backup.zip");
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

    public void RejectsBackupEntryCaseMismatchBeforeRestoring() => WithWorld((temp, root, world) => {
        string backup = Path.Combine(temp, "backup.zip");
        File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
        try {
            var dataRoot = new BedrockDataRoot(root, "fixture");
            BedrockWorldBackup.Create(dataRoot, new BedrockWorldInfo(world, "Test", true, [], [], null), backup);
            using (var file = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Update)) {
                var original = archive.GetEntry("world/level.dat")!;
                byte[] bytes;
                using (var input = original.Open())
                using (var copy = new MemoryStream()) { input.CopyTo(copy); bytes = copy.ToArray(); }
                original.Delete();
                using var replacement = archive.CreateEntry("world/LEVEL.DAT").Open();
                replacement.Write(bytes, 0, bytes.Length);
            }
            try { BedrockWorldBackup.Verify(backup); throw new Exception("mismatched entry passed verification"); }
            catch (InvalidDataException) { }
            try { BedrockWorldBackup.RestoreAsNew(dataRoot, backup); throw new Exception("mismatched entry was restored"); }
            catch (InvalidDataException) { }
            Check(Directory.GetDirectories(root, ".pcl-restore-*").Length == 0 &&
                File.ReadAllText(Path.Combine(world, "level.dat")) == "fixture", "invalid backup changed world or created staging");
        } finally { File.Delete(backup); }
    });

    public void RejectsWindowsDeviceNamesInBackups() => WithWorld((temp, _, _) => {
        string backup = Path.Combine(temp, "backup.zip");
        try {
            using (var file = File.Create(backup))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create)) {
                archive.CreateEntry("world/COM².txt");
            }
            try { BedrockWorldBackup.Verify(backup); throw new Exception("reserved device path accepted"); }
            catch (InvalidDataException ex) { Check(ex.Message.Contains("保留名称"), "device path was not rejected explicitly"); }
        } finally { File.Delete(backup); }
    });

    public void PreservesStagedFilesWhenRestoreFails() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-world-restore-failure-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(fixture, "game"), worlds = Path.Combine(root, "minecraftWorlds"), world = Path.Combine(worlds, "original"), backup = Path.Combine(fixture, "backup.zip");
        Directory.CreateDirectory(world);
        try {
            File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
            var rootInfo = new BedrockDataRoot(root, "fixture");
            BedrockWorldBackup.Create(rootInfo, new BedrockWorldInfo(world, "fixture", true, [], [], null), backup);
            try {
                BedrockWorldBackup.RestoreAsNew(rootInfo, backup, stage => {
                    File.WriteAllText(Path.Combine(stage, "external-change.txt"), "keep");
                    throw new IOException("simulated failure before commit");
                });
                throw new Exception("injected restore failure did not occur");
            }
            catch (IOException ex) { Check(ex.Message.Contains("临时目录"), "failure did not report stage"); }
            string stage = Directory.GetDirectories(root, ".pcl-restore-*").Single();
            Check(File.ReadAllText(Path.Combine(stage, "level.dat")) == "fixture" &&
                File.ReadAllText(Path.Combine(stage, "external-change.txt")) == "keep", "staged or externally changed data was deleted");
            Check(Directory.EnumerateDirectories(worlds).Single() == world, "failed restore installed a world");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

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
            Check(candidates.Count == 2 && !candidates.Single(candidate => candidate.Path == root).SupportsWorlds &&
                candidates.Single(candidate => candidate.Path == root).Kind == BedrockDataRootKind.GdkShared &&
                candidates.Single(candidate => candidate.SupportsWorlds).Kind == BedrockDataRootKind.GdkAccountCandidate,
                "shared content was not distinguished from user worlds");
        } finally { Directory.Delete(root); Directory.Delete(games); Directory.Delete(shared); }
    });

    public void LimitsExperimentalActionsToObservedPackageAndRootKind() {
        var installation = new BedrockInstallation("Microsoft.MinecraftUWP_8wekyb3d8bbwe",
            "Microsoft.MinecraftUWP_1.26.5203.0_x64__8wekyb3d8bbwe", "Microsoft.MinecraftUWP_8wekyb3d8bbwe!Game",
            "1.26.5203.0", "x64", "正式版", "未知", "未知");
        var shared = new BedrockDataRoot("shared", "renamed label", false, BedrockDataRootKind.GdkShared);
        var account = new BedrockDataRoot("account", "renamed label", true, BedrockDataRootKind.GdkAccountCandidate);
        var unknown = new BedrockDataRoot("unknown", "GDK 共享内容目录", false);
        var world = new BedrockWorldInfo("world", "fixture", true, [], [], null);
        Check(BedrockCapabilities.CanManageResourcePacks(installation, shared), "verified resource target was rejected");
        Check(BedrockCapabilities.CanEditWorldPacks(installation, account, world), "experimental world target was rejected");
        Check(!BedrockCapabilities.CanManageResourcePacks(installation, unknown), "display label was trusted as root type");
        Check(!BedrockCapabilities.CanEditWorldPacks(installation, shared, world), "shared content root accepted for world edit");
        var other = new BedrockInstallation(installation.FamilyName, "other-package", installation.AppUserModelId,
            installation.Version, installation.Architecture, installation.Channel, installation.RuntimeEvidence, installation.GamingServices);
        Check(!BedrockCapabilities.CanManageResourcePacks(other, shared) && !BedrockCapabilities.CanEditWorldPacks(other, account, world),
            "unverified package accepted for experimental writes");
        var invalidWorld = new BedrockWorldInfo("world", "fixture", true, [], [], "unreadable");
        Check(!BedrockCapabilities.CanEditWorldPacks(installation, account, invalidWorld), "unreadable world accepted");
    }

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
        File.WriteAllText(config, "[{\"pack_id\":\"11111111-1111-4111-8111-111111111111\",\"pack_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"version\":[1,2,3]}]");
        usage = BedrockWorldPackUsage.Check([dataRoot], uuid);
        Check(!usage.Complete, "duplicate pack_id was treated as a complete scan");
        File.WriteAllText(config, "[{\"pack_id\":{},\"version\":[1,2,3]}]");
        usage = BedrockWorldPackUsage.Check([dataRoot], uuid);
        Check(!usage.Complete, "non-string pack_id was treated as a complete scan");
        File.WriteAllText(config, "[{\"pack_id\":\"00000000-0000-0000-0000-000000000000\",\"version\":[1,2,3]}]");
        usage = BedrockWorldPackUsage.Check([dataRoot], uuid);
        Check(!usage.Complete, "empty UUID was treated as a complete scan");
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
