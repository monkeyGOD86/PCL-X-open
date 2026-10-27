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
    });

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
        } finally { Directory.Delete(fixture, true); }
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
        } finally { Directory.Delete(fixture, true); }
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
        } finally { Directory.Delete(fixture, true); }
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
        } finally { Directory.Delete(fixture, true); }
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
        } finally { Directory.Delete(fixture, true); }
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
        } finally { Directory.Delete(fixture, true); }
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
            string transaction = BedrockAddonTransaction.Prepare(root, packs);
            Check(Directory.GetDirectories(category).Length == 0, "prepare installed a package");
            BedrockAddonTransaction.Commit(root, transaction);
            Check(Directory.GetDirectories(category).Length == 2, "transaction did not install both packages");
            BedrockAddonTransaction.Rollback(root, transaction);
            Check(Directory.GetDirectories(category).Length == 0 && Directory.Exists(Path.Combine(transaction, "pack-0")) &&
                Directory.Exists(Path.Combine(transaction, "pack-1")), "rollback did not preserve both packages");
        } finally { Directory.Delete(fixture, true); }
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
                new BedrockPreparedPack(second, BedrockDirectoryInstaller.Preview(second))]);
            string id = Path.GetFileName(transaction).Substring(".pcl-addon-transaction-".Length);
            string destination = Path.Combine(category, "pcl-" + id + "-0");
            Directory.Move(Path.Combine(transaction, "pack-0"), destination); // Simulate interruption after one move.
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
        } finally { Directory.Delete(fixture, true); }
    }

    public void RejectsTamperedTransactionJournal() {
        string fixture = Path.Combine(Path.GetTempPath(), "pcl-bedrock-transaction-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(fixture, "game"), category = Path.Combine(game, "resource_packs"), source = Path.Combine(fixture, "source");
        Directory.CreateDirectory(category); Directory.CreateDirectory(source);
        try {
            File.WriteAllText(Path.Combine(source, "manifest.json"), Manifest);
            var root = new BedrockDataRoot(game, "fixture");
            string transaction = BedrockAddonTransaction.Prepare(root, [new BedrockPreparedPack(source, BedrockDirectoryInstaller.Preview(source))]);
            string journal = Path.Combine(transaction, "journal.json");
            File.WriteAllText(journal, File.ReadAllText(journal).Replace("resource_packs", "../outside"));
            try {
                BedrockAddonTransaction.Commit(root, transaction);
                throw new Exception("tampered journal accepted");
            } catch (InvalidDataException) { }
            Check(Directory.GetDirectories(category).Length == 0 && Directory.Exists(Path.Combine(transaction, "pack-0")),
                "tampered transaction modified installed packs");
        } finally { Directory.Delete(fixture, true); }
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
