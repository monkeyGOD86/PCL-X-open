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
