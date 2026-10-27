using System.IO.Compression;
using System.Text;
using PCLCS.Bedrock;

namespace Bedrock.Tests;

public sealed class BedrockPackInspectorTests {
    private const string Manifest = """
        {"format_version":2,"header":{"name":"pack.name","description":"demo",
        "uuid":"11111111-1111-4111-8111-111111111111","version":[1,2,3],"min_engine_version":[1,20,0]},
        "modules":[{"type":"resources","uuid":"22222222-2222-4222-8222-222222222222","version":[1,0,0]}],
        "dependencies":[{"module_name":"@minecraft/server","version":"1.2.0"}]}
        """;

    public void ParsesResourcePackMetadata() => WithArchive(".mcpack", zip => Add(zip, "manifest.json", Manifest), path => {
        var pack = BedrockPackInspector.Inspect(path).Single();
        Check(pack.Name == "pack.name" && pack.Version == "1.2.3" && pack.MinEngineVersion == "1.20.0", "metadata was not preserved");
        Check(pack.Modules.Contains("resources") && pack.Dependencies.Single() == "@minecraft/server 1.2.0", "modules or dependencies missing");
        Check(pack.PackScope == "any", "missing scope did not default to any");
    });

    public void ReadsResourcePackScopeAndRejectsInvalidType() {
        WithArchive(".mcpack", zip => Add(zip, "manifest.json", Manifest.Replace("\"description\":\"demo\"", "\"description\":\"demo\",\"pack_scope\":\"global\"")),
            path => Check(BedrockPackInspector.Inspect(path).Single().PackScope == "global", "scope not preserved"));
        WithArchive(".mcpack", zip => Add(zip, "manifest.json", Manifest.Replace("\"description\":\"demo\"", "\"description\":\"demo\",\"pack_scope\":42")), Reject);
    }

    public void ParsesNestedAddon() {
        using var nested = new MemoryStream();
        using (var zip = new ZipArchive(nested, ZipArchiveMode.Create, true)) Add(zip, "manifest.json", Manifest);
        WithArchive(".mcaddon", zip => {
            using var stream = zip.CreateEntry("packs/example.mcpack").Open();
            byte[] bytes = nested.ToArray();
            stream.Write(bytes, 0, bytes.Length);
        }, path => Check(BedrockPackInspector.Inspect(path).Count == 1, "nested pack missing"));
    }

    public void RejectsUnsafePathsAndDuplicateNames() {
        foreach (string dangerous in new[] { "../escape.txt", "C:/escape.txt", "folder/file:stream", "folder/CON.txt" })
            WithArchive(".mcpack", zip => { Add(zip, "manifest.json", Manifest); Add(zip, dangerous, "x"); }, Reject);
        WithArchive(".mcpack", zip => { Add(zip, "manifest.json", Manifest); Add(zip, "Manifest.JSON", Manifest); }, Reject);
        WithArchive(".mcpack", zip => { Add(zip, "manifest.json", Manifest); Add(zip, "folder", "x"); Add(zip, "folder/file.txt", "x"); }, Reject);
        WithArchive(".mcpack", zip => {
            Add(zip, "manifest.json", Manifest);
            var link = zip.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)0xA0000000);
        }, Reject);
        WithArchive(".mcpack", zip => { Add(zip, "manifest.json", Manifest); Add(zip, "inflated.txt", new string('A', 2 * 1024 * 1024)); }, Reject);
    }

    public void RejectsWorldAndInvalidManifest() {
        WithArchive(".mcaddon", zip => { Add(zip, "world.mcworld", "x"); Add(zip, "manifest.json", Manifest); }, Reject);
        WithArchive(".mcpack", zip => Add(zip, "manifest.json", Manifest.Replace("11111111-1111-4111-8111-111111111111", "invalid")), Reject);
        WithArchive(".mcpack", zip => Add(zip, "manifest.json", Manifest.Replace("22222222-2222-4222-8222-222222222222", "11111111-1111-4111-8111-111111111111")), Reject);
    }

    public void InspectsUnpackedPackAndDetectsInstalledConflicts() {
        string root = Path.Combine(Path.GetTempPath(), "pcl-bedrock-pack-test-" + Guid.NewGuid().ToString("N"));
        string packs = Path.Combine(root, "resource_packs");
        string first = Path.Combine(packs, "first");
        string secondParent = Path.Combine(packs, "second");
        string second = Path.Combine(secondParent, "2.0.0");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try {
            File.WriteAllText(Path.Combine(first, "manifest.json"), Manifest);
            File.WriteAllText(Path.Combine(second, "manifest.json"), Manifest.Replace("[1,2,3]", "[2,0,0]"));
            Check(BedrockPackInspector.InspectDirectory(first).Version == "1.2.3", "directory pack not parsed");
            var installed = BedrockAddonCatalog.Read(new BedrockDataRoot(root, "fixture", false));
            Check(installed.Count == 2 && installed.All(pack => pack.Warnings.Any(warning => warning.Contains("不同版本"))),
                "UUID conflict was not reported");
        } finally {
            File.Delete(Path.Combine(first, "manifest.json"));
            File.Delete(Path.Combine(second, "manifest.json"));
            Directory.Delete(first); Directory.Delete(second); Directory.Delete(secondParent); Directory.Delete(packs); Directory.Delete(root);
        }
    }

    public void InstallsDirectoryWithoutOverwritingAndRejectsDuplicates() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-install-test-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(fixture, "source");
        string rootPath = Path.Combine(fixture, "game");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(rootPath, "resource_packs"));
        try {
            File.WriteAllText(Path.Combine(source, "manifest.json"), Manifest);
            Directory.CreateDirectory(Path.Combine(source, "textures"));
            File.WriteAllText(Path.Combine(source, "textures", "test.txt"), "original");
            var root = new BedrockDataRoot(rootPath, "fixture");
            var preview = BedrockDirectoryInstaller.Preview(source);
            string installed = BedrockDirectoryInstaller.Install(root, source, preview);
            Check(File.ReadAllText(Path.Combine(installed, "textures", "test.txt")) == "original", "installed contents differ");
            File.WriteAllText(Path.Combine(source, "textures", "test.txt"), "changed");
            try {
                BedrockDirectoryInstaller.Install(root, source, BedrockDirectoryInstaller.Preview(source));
                throw new Exception("duplicate was accepted");
            } catch (InvalidDataException) { }
            Check(File.ReadAllText(Path.Combine(installed, "textures", "test.txt")) == "original", "existing package was overwritten");
            Check(Directory.GetDirectories(Path.Combine(rootPath, "resource_packs")).Length == 1, "duplicate directory created");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RejectsChangedPreviewAndUnrecognisedInstalledPack() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-install-test-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(fixture, "source");
        string rootPath = Path.Combine(fixture, "game");
        Directory.CreateDirectory(source);
        string category = Path.Combine(rootPath, "resource_packs");
        Directory.CreateDirectory(category);
        try {
            File.WriteAllText(Path.Combine(source, "manifest.json"), Manifest);
            var root = new BedrockDataRoot(rootPath, "fixture");
            var preview = BedrockDirectoryInstaller.Preview(source);
            File.WriteAllText(Path.Combine(source, "manifest.json"), Manifest.Replace("demo", "test"));
            try {
                BedrockDirectoryInstaller.Install(root, source, preview);
                throw new Exception("changed preview was accepted");
            } catch (InvalidDataException) { }
            preview = BedrockDirectoryInstaller.Preview(source);
            Directory.CreateDirectory(Path.Combine(category, "unknown"));
            try {
                BedrockDirectoryInstaller.Install(root, source, preview);
                throw new Exception("incomplete catalog was accepted");
            } catch (InvalidDataException) { }
            Check(Directory.GetDirectories(category).Length == 1, "unexpected package installed");
            Check(!Directory.GetDirectories(rootPath, ".pcl-addon-stage-*").Any(), "unexpected stage created");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void PreparesArchiveAndDetectsLaterChanges() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-prepare-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try {
            string path = Path.Combine(fixture, "test.mcpack");
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
                Add(zip, "pack/manifest.json", Manifest);
                Add(zip, "pack/textures/example.txt", "hello");
            }
            var prepared = BedrockArchivePreparer.PrepareMcpack(path, fixture);
            Check(File.ReadAllText(Path.Combine(prepared.Directory, "textures", "example.txt")) == "hello", "archive content missing");
            Check(prepared.Preview.Pack.Version == "1.2.3", "prepared metadata missing");
            File.WriteAllText(Path.Combine(prepared.Directory, "textures", "example.txt"), "other");
            string root = Path.Combine(fixture, "game");
            Directory.CreateDirectory(Path.Combine(root, "resource_packs"));
            try {
                BedrockDirectoryInstaller.Install(new BedrockDataRoot(root, "fixture"), prepared.Directory, prepared.Preview);
                throw new Exception("changed preparation accepted");
            } catch (InvalidDataException) { }
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RejectsArchiveOutsidePackDirectory() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-prepare-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try {
            string path = Path.Combine(fixture, "test.mcpack");
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
                Add(zip, "pack/manifest.json", Manifest);
                Add(zip, "outside.txt", "outside");
            }
            try {
                BedrockArchivePreparer.PrepareMcpack(path, fixture);
                throw new Exception("unrelated file accepted");
            } catch (InvalidDataException) { }
            Check(!Directory.GetDirectories(fixture, "pcl-pack-preparation-*").Any(), "invalid archive produced a stage");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void PreparesMultiPackAddonWithoutInstalling() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-addon-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try {
            using var nestedBytes = new MemoryStream();
            using (var nested = new ZipArchive(nestedBytes, ZipArchiveMode.Create, true))
                Add(nested, "manifest.json", Manifest.Replace("11111111-1111-4111-8111-111111111111", "33333333-3333-4333-8333-333333333333"));
            string path = Path.Combine(fixture, "test.mcaddon");
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
                Add(zip, "first/manifest.json", Manifest);
                Add(zip, "first/textures/a.txt", "first");
                using var stream = zip.CreateEntry("other/second.mcpack").Open();
                byte[] bytes = nestedBytes.ToArray();
                stream.Write(bytes, 0, bytes.Length);
            }
            var addon = BedrockArchivePreparer.PrepareMcaddon(path, fixture);
            Check(addon.Packs.Count == 2 && addon.Packs.Select(p => p.Preview.Pack.Uuid).Distinct().Count() == 2,
                "multiple package members missing");
            Check(addon.Packs.Any(p => File.Exists(Path.Combine(p.Directory, "textures", "a.txt"))), "folder member content missing");
            Check(!Directory.Exists(Path.Combine(fixture, "resource_packs")), "preparation modified game data");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RejectsAddonWithAmbiguousFiles() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-addon-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try {
            string path = Path.Combine(fixture, "test.mcaddon");
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
                Add(zip, "first/manifest.json", Manifest);
                Add(zip, "unrelated.txt", "unrelated");
            }
            try {
                BedrockArchivePreparer.PrepareMcaddon(path, fixture);
                throw new Exception("ambiguous addon accepted");
            } catch (InvalidDataException) { }
            Check(!Directory.GetDirectories(fixture, "pcl-addon-preparation-*").Any(), "invalid addon produced a stage");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void CommitsAndRollsBackMultiPackTransaction() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-transaction-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game");
        string category = Path.Combine(game, "resource_packs");
        Directory.CreateDirectory(category);
        try {
            string first = Path.Combine(fixture, "first"), second = Path.Combine(fixture, "second");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "manifest.json"), Manifest);
            File.WriteAllText(Path.Combine(second, "manifest.json"), Manifest.Replace("11111111-1111-4111-8111-111111111111", "33333333-3333-4333-8333-333333333333"));
            var packs = new[] { new BedrockPreparedPack(first, BedrockDirectoryInstaller.Preview(first)),
                new BedrockPreparedPack(second, BedrockDirectoryInstaller.Preview(second)) };
            var root = new BedrockDataRoot(game, "fixture");
            string transaction = BedrockAddonTransaction.Prepare(root, packs, [root]);
            Check(Directory.GetDirectories(category).Length == 0, "prepare installed a package");
            Check(BedrockAddonTransaction.List(root).Single().State == "已暂存", "prepared transaction not reported");
            BedrockAddonTransaction.Commit(root, transaction, [root]);
            Check(Directory.GetDirectories(category).Length == 2, "transaction did not install both packages");
            Check(BedrockAddonTransaction.List(root).Single().State == "已移动", "committed transaction not reported");
            BedrockAddonTransaction.Rollback(root, transaction);
            Check(Directory.GetDirectories(category).Length == 0 && Directory.Exists(Path.Combine(transaction, "pack-0")) &&
                Directory.Exists(Path.Combine(transaction, "pack-1")), "rollback did not preserve both packages");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RecoversInterruptedTransactionAndRefusesChangedContent() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-transaction-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game"), category = Path.Combine(game, "resource_packs"), source = Path.Combine(fixture, "source");
        string second = Path.Combine(fixture, "second");
        Directory.CreateDirectory(category); Directory.CreateDirectory(source); Directory.CreateDirectory(second);
        try {
            File.WriteAllText(Path.Combine(source, "manifest.json"), Manifest);
            File.WriteAllText(Path.Combine(second, "manifest.json"), Manifest.Replace("11111111-1111-4111-8111-111111111111", "33333333-3333-4333-8333-333333333333"));
            var root = new BedrockDataRoot(game, "fixture");
            string transaction = BedrockAddonTransaction.Prepare(root, [new BedrockPreparedPack(source, BedrockDirectoryInstaller.Preview(source)),
                new BedrockPreparedPack(second, BedrockDirectoryInstaller.Preview(second))], [root]);
            string id = Path.GetFileName(transaction).Substring(".pcl-addon-transaction-".Length);
            string destination = Path.Combine(category, "pcl-" + id + "-0");
            Directory.Move(Path.Combine(transaction, "pack-0"), destination); // Simulate interruption after one move.
            Check(BedrockAddonTransaction.List(root).Single().State == "部分移动", "interrupted transaction not reported");
            File.WriteAllText(Path.Combine(destination, "new.txt"), "changed");
            try {
                BedrockAddonTransaction.Rollback(root, transaction);
                throw new Exception("modified installed content was moved");
            } catch (InvalidDataException) { }
            Check(File.Exists(Path.Combine(destination, "new.txt")), "modified package was lost");
            File.Delete(Path.Combine(destination, "new.txt"));
            BedrockAddonTransaction.Rollback(root, transaction);
            Check(!Directory.Exists(destination) && Directory.Exists(Path.Combine(transaction, "pack-0")) &&
                Directory.Exists(Path.Combine(transaction, "pack-1")), "interrupted transaction was not recovered");
            BedrockAddonTransaction.Rollback(root, transaction); // Recovery can be retried.
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RejectsTamperedTransactionJournal() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-transaction-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game"), category = Path.Combine(game, "resource_packs"), source = Path.Combine(fixture, "source");
        Directory.CreateDirectory(category); Directory.CreateDirectory(source);
        try {
            File.WriteAllText(Path.Combine(source, "manifest.json"), Manifest);
            var root = new BedrockDataRoot(game, "fixture");
            string transaction = BedrockAddonTransaction.Prepare(root, [new BedrockPreparedPack(source, BedrockDirectoryInstaller.Preview(source))], [root]);
            string journal = Path.Combine(transaction, "journal.json");
            File.WriteAllText(journal, File.ReadAllText(journal).Replace("resource_packs", "../outside"));
            Check(BedrockAddonTransaction.List(root).Single().State == "记录不可读取", "damaged journal not reported");
            try {
                BedrockAddonTransaction.Commit(root, transaction, [root]);
                throw new Exception("tampered journal accepted");
            } catch (InvalidDataException) { }
            Check(Directory.GetDirectories(category).Length == 0 && Directory.Exists(Path.Combine(transaction, "pack-0")),
                "tampered transaction modified installed packs");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RemovesAndRestoresVersionedPackWithExternalBackup() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-remove-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game"), backup = Path.Combine(fixture, "backup"),
            pack = Path.Combine(game, "resource_packs", "package", "1.2.3");
        Directory.CreateDirectory(pack); Directory.CreateDirectory(backup); Directory.CreateDirectory(Path.Combine(game, "minecraftWorlds"));
        try {
            File.WriteAllText(Path.Combine(pack, "manifest.json"), Manifest);
            File.WriteAllText(Path.Combine(pack, "data.txt"), "user data");
            var root = new BedrockDataRoot(game, "fixture");
            var selected = BedrockAddonCatalog.Read(root).Single();
            var removal = BedrockAddonRemoval.Remove(root, selected, BedrockDirectoryInstaller.Preview(pack), backup, [root]);
            Check(!Directory.Exists(pack) && File.ReadAllText(Path.Combine(removal.BackupPath, "data.txt")) == "user data", "backup or quarantine missing");
            var listed = BedrockAddonRemoval.List(root).Single();
            Check(listed.State == "已隔离" && listed.OriginalPath == pack && listed.BackupPath == removal.BackupPath, "removal not listed");
            Check(BedrockAddonRemoval.Restore(root, removal.TransactionPath, [root]) == pack, "original location not restored");
            Check(BedrockAddonRemoval.List(root).Single().State == "原位置存在", "restored removal state not reported");
            Check(File.ReadAllText(Path.Combine(pack, "data.txt")) == "user data" && Directory.Exists(removal.BackupPath), "restore lost data");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RefusesRestorationOverExistingOrModifiedPack() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-remove-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game"), backup = Path.Combine(fixture, "backup"), pack = Path.Combine(game, "resource_packs", "package");
        Directory.CreateDirectory(pack); Directory.CreateDirectory(backup); Directory.CreateDirectory(Path.Combine(game, "minecraftWorlds"));
        try {
            File.WriteAllText(Path.Combine(pack, "manifest.json"), Manifest);
            var root = new BedrockDataRoot(game, "fixture");
            var removal = BedrockAddonRemoval.Remove(root, BedrockAddonCatalog.Read(root).Single(), BedrockDirectoryInstaller.Preview(pack), backup, [root]);
            string other = Path.Combine(fixture, "other"), duplicate = Path.Combine(other, "resource_packs", "duplicate");
            Directory.CreateDirectory(duplicate);
            File.WriteAllText(Path.Combine(duplicate, "manifest.json"), Manifest);
            try { BedrockAddonRemoval.Restore(root, removal.TransactionPath, [root, new BedrockDataRoot(other, "other")]); throw new Exception("cross-root duplicate accepted"); }
            catch (InvalidDataException) { }
            Check(Directory.Exists(Path.Combine(removal.TransactionPath, "content")), "cross-root conflict moved quarantine");
            File.Delete(Path.Combine(duplicate, "manifest.json")); Directory.Delete(duplicate);
            Directory.CreateDirectory(pack);
            File.WriteAllText(Path.Combine(pack, "existing.txt"), "keep");
            try { BedrockAddonRemoval.Restore(root, removal.TransactionPath, [root]); throw new Exception("existing destination accepted"); }
            catch (IOException) { }
            Check(File.ReadAllText(Path.Combine(pack, "existing.txt")) == "keep", "existing destination changed");
            File.Delete(Path.Combine(pack, "existing.txt")); Directory.Delete(pack);
            string content = Path.Combine(removal.TransactionPath, "content");
            File.WriteAllText(Path.Combine(content, "extra.txt"), "modified");
            try { BedrockAddonRemoval.Restore(root, removal.TransactionPath, [root]); throw new Exception("changed quarantine accepted"); }
            catch (InvalidDataException) { }
            Check(File.Exists(Path.Combine(content, "extra.txt")) && Directory.Exists(removal.BackupPath), "changed or backup data lost");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RejectsChangedRemovalAndTamperedRemovalJournal() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-remove-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game"), backup = Path.Combine(fixture, "backup"), pack = Path.Combine(game, "resource_packs", "package");
        Directory.CreateDirectory(pack); Directory.CreateDirectory(backup); Directory.CreateDirectory(Path.Combine(game, "minecraftWorlds"));
        try {
            File.WriteAllText(Path.Combine(pack, "manifest.json"), Manifest);
            var root = new BedrockDataRoot(game, "fixture");
            var selected = BedrockAddonCatalog.Read(root).Single();
            var preview = BedrockDirectoryInstaller.Preview(pack);
            File.WriteAllText(Path.Combine(pack, "extra.txt"), "change");
            try { BedrockAddonRemoval.Remove(root, selected, preview, backup, [root]); throw new Exception("changed pack accepted"); }
            catch (InvalidDataException) { }
            Check(Directory.Exists(pack) && !Directory.EnumerateFileSystemEntries(backup).Any(), "failed removal changed data");
            var removal = BedrockAddonRemoval.Remove(root, selected, BedrockDirectoryInstaller.Preview(pack), backup, [root]);
            string journal = Path.Combine(removal.TransactionPath, "journal.json");
            File.WriteAllText(journal, File.ReadAllText(journal).Replace("resource_packs/package", "../outside"));
            Check(BedrockAddonRemoval.List(root).Single().State == "记录不可读取", "tampered removal was not reported");
            try { BedrockAddonRemoval.Restore(root, removal.TransactionPath, [root]); throw new Exception("tampered journal accepted"); }
            catch (InvalidDataException) { }
            Check(Directory.Exists(Path.Combine(removal.TransactionPath, "content")) && Directory.Exists(removal.BackupPath), "tampered journal lost data");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void RefusesRemovalWhenAccountWorldReferencesSharedPack() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-remove-usage-test-" + Guid.NewGuid().ToString("N"));
        string shared = Path.Combine(fixture, "shared"), account = Path.Combine(fixture, "account"), backup = Path.Combine(fixture, "backup");
        string pack = Path.Combine(shared, "resource_packs", "package"), world = Path.Combine(account, "minecraftWorlds", "world");
        Directory.CreateDirectory(pack); Directory.CreateDirectory(world); Directory.CreateDirectory(backup);
        try {
            File.WriteAllText(Path.Combine(pack, "manifest.json"), Manifest);
            File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
            File.WriteAllText(Path.Combine(world, "world_resource_packs.json"), "[{\"pack_id\":\"11111111-1111-4111-8111-111111111111\",\"version\":[1,2,3]}]");
            var target = new BedrockDataRoot(shared, "shared", false);
            var worldRoot = new BedrockDataRoot(account, "account");
            try {
                BedrockAddonRemoval.Remove(target, BedrockAddonCatalog.Read(target).Single(), BedrockDirectoryInstaller.Preview(pack), account, [worldRoot]);
                throw new Exception("backup inside account data was accepted");
            } catch (InvalidDataException) { }
            try {
                BedrockAddonRemoval.Remove(target, BedrockAddonCatalog.Read(target).Single(), BedrockDirectoryInstaller.Preview(pack), backup, [worldRoot]);
                throw new Exception("in-use pack was removed");
            } catch (InvalidDataException) { }
            Check(Directory.Exists(pack) && !Directory.EnumerateFileSystemEntries(backup).Any(), "referenced pack changed");
            File.WriteAllText(Path.Combine(world, "world_resource_packs.json"), "invalid");
            try {
                BedrockAddonRemoval.Remove(target, BedrockAddonCatalog.Read(target).Single(), BedrockDirectoryInstaller.Preview(pack), backup, [worldRoot]);
                throw new Exception("incomplete world scan accepted");
            } catch (InvalidDataException) { }
            Check(Directory.Exists(pack), "pack changed after incomplete scan");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void ChecksConflictsAcrossExplicitRootsAndReportsIncompleteScan() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-scope-test-" + Guid.NewGuid().ToString("N"));
        string account = Path.Combine(fixture, "account"), shared = Path.Combine(fixture, "shared");
        string pack = Path.Combine(shared, "resource_packs", "existing"), unknown = Path.Combine(account, "behavior_packs", "unknown");
        Directory.CreateDirectory(pack); Directory.CreateDirectory(unknown);
        try {
            File.WriteAllText(Path.Combine(pack, "manifest.json"), Manifest);
            var first = new BedrockDataRoot(account, "account");
            var second = new BedrockDataRoot(shared, "shared", false);
            Guid uuid = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var result = BedrockAddonScope.Check([first, second], [uuid]);
            Check(result.Conflicts.Count == 1 && result.Conflicts[0].Root.Path == shared, "shared-root conflict missing");
            Check(!result.Complete && result.IncompleteRoots.Single() == account, "incomplete scan not reported");
            Directory.Delete(unknown);
            result = BedrockAddonScope.Check([first, second], [uuid]);
            Check(result.Complete && result.Conflicts.Count == 1, "complete cross-root scan failed");
            try { BedrockAddonScope.Check([first, first], [uuid]); throw new Exception("duplicate roots accepted"); }
            catch (InvalidDataException) { }
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void ChecksDependenciesAcrossRootsWithoutClaimingWorldActivation() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-dependency-test-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(fixture, "account"), shared = Path.Combine(fixture, "shared"), pack = Path.Combine(shared, "resource_packs", "dep");
        Directory.CreateDirectory(first); Directory.CreateDirectory(pack);
        try {
            File.WriteAllText(Path.Combine(pack, "manifest.json"), Manifest);
            var source = new BedrockPackInfo("source", "", Guid.NewGuid(), "1.0.0", ["data"],
                ["11111111-1111-4111-8111-111111111111 1.2.3", "@minecraft/server 1.2.0"], null);
            var roots = new[] { new BedrockDataRoot(first, "account"), new BedrockDataRoot(shared, "shared", false) };
            var report = BedrockDependencyCheck.Analyze(source, roots);
            Check(report.Results[0].Status.Contains("世界启用状态未检查") && report.Results[1].Status.Contains("未验证"),
                "dependency status was overstated");
            File.Delete(Path.Combine(pack, "manifest.json")); Directory.Delete(pack);
            var planned = new BedrockPackInfo("dependency", "", Guid.Parse("11111111-1111-4111-8111-111111111111"), "1.2.3", ["resources"], [], null);
            report = BedrockDependencyCheck.Analyze(source, roots, [planned]);
            Check(report.Results[0].Status.Contains("本次待安装"), "planned dependency was not distinguished from installed");
            Directory.CreateDirectory(pack); File.WriteAllText(Path.Combine(pack, "manifest.json"), Manifest);
            var wrongVersion = new BedrockPackInfo("source", "", Guid.NewGuid(), "1.0.0", ["data"],
                ["11111111-1111-4111-8111-111111111111 9.9.9"], null);
            Check(BedrockDependencyCheck.Analyze(wrongVersion, roots).Results.Single().Status.Contains("未发现声明版本"), "version mismatch missed");
            Directory.CreateDirectory(Path.Combine(first, "behavior_packs", "unreadable"));
            Check(BedrockDependencyCheck.Analyze(wrongVersion, roots).Results.Single().Status.Contains("无法确认"), "incomplete scan was not reported");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void TransactionRechecksCrossRootScopeBeforeCommit() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-scope-transaction-test-" + Guid.NewGuid().ToString("N"));
        string account = Path.Combine(fixture, "account"), shared = Path.Combine(fixture, "shared"), source = Path.Combine(fixture, "source");
        Directory.CreateDirectory(Path.Combine(account, "resource_packs"));
        Directory.CreateDirectory(Path.Combine(shared, "resource_packs"));
        Directory.CreateDirectory(source);
        try {
            File.WriteAllText(Path.Combine(source, "manifest.json"), Manifest);
            var root = new BedrockDataRoot(account, "account");
            var sharedRoot = new BedrockDataRoot(shared, "shared", false);
            var packs = new[] { new BedrockPreparedPack(source, BedrockDirectoryInstaller.Preview(source)) };
            string transaction = BedrockAddonTransaction.Prepare(root, packs, [root, sharedRoot]);
            try { BedrockAddonTransaction.Commit(root, transaction, [root]); throw new Exception("changed scope accepted"); }
            catch (InvalidDataException) { }
            string duplicate = Path.Combine(shared, "resource_packs", "duplicate");
            Directory.CreateDirectory(duplicate); File.WriteAllText(Path.Combine(duplicate, "manifest.json"), Manifest);
            try { BedrockAddonTransaction.Commit(root, transaction, [sharedRoot, root]); throw new Exception("new shared conflict accepted"); }
            catch (InvalidDataException) { }
            Check(Directory.GetDirectories(Path.Combine(account, "resource_packs")).Length == 0, "conflict caused an install");
            File.Delete(Path.Combine(duplicate, "manifest.json")); Directory.Delete(duplicate);
            BedrockAddonTransaction.Commit(root, transaction, [sharedRoot, root]);
            BedrockAddonTransaction.Rollback(root, transaction);
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void LegacyTransactionCanBeRecoveredButNotCommitted() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-legacy-transaction-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game"), source = Path.Combine(fixture, "source"), category = Path.Combine(game, "resource_packs");
        Directory.CreateDirectory(category); Directory.CreateDirectory(source);
        try {
            File.WriteAllText(Path.Combine(source, "manifest.json"), Manifest);
            var root = new BedrockDataRoot(game, "fixture");
            string transaction = BedrockAddonTransaction.Prepare(root, [new BedrockPreparedPack(source, BedrockDirectoryInstaller.Preview(source))], [root]);
            string journal = Path.Combine(transaction, "journal.json");
            string legacy = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(journal), ",\"scope\":\\[.*?\\]", "")
                .Replace("\"schemaVersion\":2", "\"schemaVersion\":1");
            File.WriteAllText(journal, legacy);
            Check(BedrockAddonTransaction.List(root).Single().State == "已暂存", "legacy transaction not readable");
            try { BedrockAddonTransaction.Commit(root, transaction, [root]); throw new Exception("legacy transaction committed"); }
            catch (InvalidDataException) { }
            string id = Path.GetFileName(transaction).Substring(".pcl-addon-transaction-".Length);
            string destination = Path.Combine(category, "pcl-" + id + "-0");
            Directory.Move(Path.Combine(transaction, "pack-0"), destination);
            BedrockAddonTransaction.Rollback(root, transaction);
            Check(Directory.Exists(Path.Combine(transaction, "pack-0")) && !Directory.Exists(destination), "legacy recovery failed");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    public void PreparesArchiveThenInstallsAndRecoversIt() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-archive-transaction-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game"), category = Path.Combine(game, "resource_packs");
        Directory.CreateDirectory(category);
        try {
            string archive = Path.Combine(fixture, "pack.mcpack");
            using (var file = File.Create(archive))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
                Add(zip, "manifest.json", Manifest);
                Add(zip, "textures/content.txt", "fixture");
            }
            var prepared = BedrockArchivePreparer.PrepareMcpack(archive, fixture);
            var root = new BedrockDataRoot(game, "fixture");
            string transaction = BedrockAddonTransaction.Prepare(root, [prepared], [root]);
            BedrockAddonTransaction.Commit(root, transaction, [root]);
            var installed = BedrockAddonCatalog.Read(root).Single();
            Check(installed.Manifest?.Uuid == prepared.Preview.Pack.Uuid &&
                File.ReadAllText(Path.Combine(installed.Path, "textures", "content.txt")) == "fixture", "archive was not installed");
            BedrockAddonTransaction.Rollback(root, transaction);
            Check(!Directory.Exists(installed.Path) && File.Exists(Path.Combine(transaction, "pack-0", "manifest.json")),
                "installed archive was not recovered");
        } finally { TestPaths.DeleteFixture(fixture); }
    }

    private static void Add(ZipArchive zip, string name, string text) {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void WithArchive(string extension, Action<ZipArchive> build, Action<string> test) {
        string path = Path.Combine(Path.GetTempPath(), "pcl-bedrock-test-" + Guid.NewGuid().ToString("N") + extension);
        try {
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create)) build(archive);
            test(path);
        } finally { File.Delete(path); }
    }

    private static void Reject(string path) {
        try { BedrockPackInspector.Inspect(path); }
        catch (InvalidDataException) { return; }
        throw new Exception("Unsafe archive was accepted");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
