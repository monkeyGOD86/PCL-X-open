using System.ComponentModel;
using PCLCS.Bedrock;

namespace Bedrock.Tests;

public class BedrockServiceTests {
    public static int Main() {
        var tests = new BedrockServiceTests();
        var packTests = new BedrockPackInspectorTests();
        var worldTests = new BedrockWorldReaderTests();
        var worldPackTests = new BedrockWorldPackEditorTests();
        Action[] cases = [tests.DetectionDistinguishesNoInstallationFromQueryFailure,
            tests.LaunchRejectsRegistrationChangedSinceSelection,
            tests.LaunchRevalidatesAndPassesTheRegisteredApplicationToWindows,
            tests.UnsupportedApplicationIsNotActivated,
            tests.ActivationErrorIsReportedRatherThanClaimedAsSuccess,
            packTests.ParsesResourcePackMetadata,
            packTests.ParsesNestedAddon,
            packTests.RejectsUnsafePathsAndDuplicateNames,
            packTests.RejectsWorldAndInvalidManifest,
            worldTests.FindsCandidateAndReadsWorldPackReferences,
            worldTests.ReportsInvalidWorldMetadataWithoutDiscardingWorld,
            worldTests.CreatesAndVerifiesBackupWithoutChangingWorld,
            worldTests.RejectsBackupInsideGameData,
            worldTests.RefusesToOverwriteExistingBackup,
            worldTests.RestoresAsNewWithoutOverwritingOriginal,
            worldTests.RejectsCorruptRestoreBeforeCreatingWorld,
            worldTests.RejectsSharedRootForWorldOperations,
            worldTests.DistinguishesSharedContentDirectory,
            packTests.InspectsUnpackedPackAndDetectsInstalledConflicts,
            packTests.InstallsDirectoryWithoutOverwritingAndRejectsDuplicates,
            packTests.RejectsChangedPreviewAndUnrecognisedInstalledPack,
            packTests.PreparesArchiveAndDetectsLaterChanges,
            packTests.RejectsArchiveOutsidePackDirectory,
            packTests.PreparesMultiPackAddonWithoutInstalling,
            packTests.RejectsAddonWithAmbiguousFiles,
            packTests.CommitsAndRollsBackMultiPackTransaction,
            packTests.RecoversInterruptedTransactionAndRefusesChangedContent,
            packTests.RejectsTamperedTransactionJournal,
            packTests.RemovesAndRestoresVersionedPackWithExternalBackup,
            packTests.RefusesRestorationOverExistingOrModifiedPack,
            packTests.RejectsChangedRemovalAndTamperedRemovalJournal,
            packTests.ChecksConflictsAcrossExplicitRootsAndReportsIncompleteScan,
            packTests.TransactionRechecksCrossRootScopeBeforeCommit,
            packTests.LegacyTransactionCanBeRecoveredButNotCommitted,
            worldPackTests.UpdatesAndRestoresWorldConfigWithoutDiscardingFields,
            worldPackTests.CreatesAndRevertsMissingConfigWithoutDeletingIt,
            worldPackTests.RejectsStaleSnapshotAndConflictingVersion,
            worldPackTests.DisablesOnlySelectedPackAndRejectsTamperedBackup,
            worldPackTests.RejectsBackupInGameDataAndDuplicateUuid];
        int failures = 0;
        foreach (var test in cases) {
            try { test(); Console.WriteLine($"PASS {test.Method.Name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Method.Name}: {ex.Message}"); }
        }
        Console.WriteLine($"{cases.Length - failures}/{cases.Length} passed");
        return failures == 0 ? 0 : 1;
    }
    private static BedrockInstallation Installation(string fullName = "package-v1", string appId = "family!Game") =>
        new("family", fullName, appId, "1.2.3.4", "x64", "正式版", "未知", "无法确认");

    public void DetectionDistinguishesNoInstallationFromQueryFailure() {
        var catalog = new FakeCatalog();
        var service = new BedrockService(catalog, new FakeActivator());
        var empty = service.Detect();
        Assert.Empty(empty.Installations);
        Assert.Null(empty.Error);

        catalog.Failure = new Win32Exception(5);
        var failure = service.Detect();
        Assert.Empty(failure.Installations);
        Assert.Contains("5", failure.Error);
    }

    public void LaunchRejectsRegistrationChangedSinceSelection() {
        var catalog = new FakeCatalog { Installations = [Installation("package-v2")] };
        var activator = new FakeActivator();
        var result = new BedrockService(catalog, activator).Launch(Installation());
        Assert.False(result.Accepted);
        Assert.Contains("刷新", result.Error);
        Assert.Null(activator.LastId);
    }

    public void LaunchRevalidatesAndPassesTheRegisteredApplicationToWindows() {
        var catalog = new FakeCatalog { Installations = [Installation()] };
        var activator = new FakeActivator { ProcessId = 123 };
        var result = new BedrockService(catalog, activator).Launch(Installation());
        Assert.True(result.Accepted);
        Assert.Equal((uint)123, result.ProcessId);
        Assert.Equal("family!Game", activator.LastId);
    }

    public void UnsupportedApplicationIsNotActivated() {
        var unsupported = Installation(appId: "");
        var activator = new FakeActivator();
        var result = new BedrockService(new FakeCatalog { Installations = [unsupported] }, activator).Launch(unsupported);
        Assert.False(result.Accepted);
        Assert.Null(activator.LastId);
    }

    public void ActivationErrorIsReportedRatherThanClaimedAsSuccess() {
        var catalog = new FakeCatalog { Installations = [Installation()] };
        var activator = new FakeActivator { Failure = new Win32Exception(5) };
        var result = new BedrockService(catalog, activator).Launch(Installation());
        Assert.False(result.Accepted);
        Assert.Contains("5", result.Error);
    }

    private sealed class FakeCatalog : IBedrockPackageCatalog {
        public IReadOnlyList<BedrockInstallation> Installations { get; set; } = [];
        public Exception? Failure { get; set; }
        public IReadOnlyList<BedrockInstallation> FindInstalled() => Failure is null ? Installations : throw Failure;
    }

    private sealed class FakeActivator : IBedrockActivator {
        public uint ProcessId { get; set; }
        public string? LastId { get; private set; }
        public Exception? Failure { get; set; }
        public uint Activate(string appUserModelId) {
            LastId = appUserModelId;
            if (Failure is not null) throw Failure;
            return ProcessId;
        }
    }

    private static class Assert {
        public static void True(bool value) { if (!value) throw new Exception("Expected true"); }
        public static void False(bool value) { if (value) throw new Exception("Expected false"); }
        public static void Null(object? value) { if (value is not null) throw new Exception("Expected null"); }
        public static void Empty<T>(IEnumerable<T> value) { if (value.Any()) throw new Exception("Expected empty collection"); }
        public static void Equal<T>(T expected, T actual) {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
        }
        public static void Contains(string expected, string? actual) {
            if (actual is null || !actual.Contains(expected)) throw new Exception($"Expected text containing {expected}");
        }
    }
}
