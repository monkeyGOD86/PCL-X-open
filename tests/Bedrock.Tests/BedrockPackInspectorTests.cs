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
