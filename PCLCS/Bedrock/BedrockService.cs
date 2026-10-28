using System.ComponentModel;

namespace PCLCS.Bedrock;

/// <summary>Registered Windows application; this does not establish game ownership.</summary>
public sealed class BedrockInstallation(string familyName, string packageFullName, string appUserModelId,
    string version, string architecture, string channel, string runtimeEvidence, string gamingServices) {
    public string FamilyName { get; } = familyName;
    public string PackageFullName { get; } = packageFullName;
    public string AppUserModelId { get; } = appUserModelId;
    public string Version { get; } = version;
    public string Architecture { get; } = architecture;
    public string Channel { get; } = channel;
    public string RuntimeEvidence { get; } = runtimeEvidence;
    public string GamingServices { get; } = gamingServices;
    public bool CanLaunch => !string.IsNullOrEmpty(AppUserModelId);
}

public sealed class BedrockDetectionResult(IReadOnlyList<BedrockInstallation> installations, string? error) {
    public IReadOnlyList<BedrockInstallation> Installations { get; } = installations;
    public string? Error { get; } = error;
}

public sealed class BedrockLaunchResult(bool accepted, string? error, uint? processId = null) {
    public bool Accepted { get; } = accepted;
    public string? Error { get; } = error;
    /// <summary>The activation API's PID; it does not prove that the game is ready.</summary>
    public uint? ProcessId { get; } = processId;
}

public interface IBedrockPackageCatalog {
    IReadOnlyList<BedrockInstallation> FindInstalled();
}

/// <summary>Reports partial results when independently queried channels fail.</summary>
public interface IBedrockCatalogDiagnostics {
    BedrockDetectionResult DetectInstalled();
}

public interface IBedrockActivator {
    uint Activate(string appUserModelId);
}

public sealed class BedrockService(IBedrockPackageCatalog catalog, IBedrockActivator activator) {
    public BedrockDetectionResult Detect() {
        try {
            return catalog is IBedrockCatalogDiagnostics diagnostics ? diagnostics.DetectInstalled() : new(catalog.FindInstalled(), null);
        } catch (Exception ex) when (IsPlatformError(ex)) {
            return new([], Describe(ex));
        }
    }

    public BedrockLaunchResult Launch(BedrockInstallation selected) {
        if (selected is null) return new(false, "尚未选择基岩版安装。");
        if (!selected.CanLaunch) return new(false, "此安装的应用入口尚不受支持。");
        try {
            // A previously selected registration can disappear or change after a Store update.
            var detection = Detect();
            var current = detection.Installations.FirstOrDefault(candidate =>
                string.Equals(candidate.FamilyName, selected.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.PackageFullName, selected.PackageFullName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.AppUserModelId, selected.AppUserModelId, StringComparison.OrdinalIgnoreCase));
            if (current is null) return new(false, detection.Error is null ? "安装状态已更改，请刷新后重新选择。" : "无法重新确认安装状态：" + detection.Error);
            return new(true, null, activator.Activate(current.AppUserModelId));
        } catch (Exception ex) when (IsPlatformError(ex)) {
            return new(false, Describe(ex));
        }
    }

    internal static bool IsPlatformError(Exception ex) => ex is Win32Exception or UnauthorizedAccessException or
        System.Runtime.InteropServices.COMException or PlatformNotSupportedException or DllNotFoundException or
        EntryPointNotFoundException or InvalidOperationException or System.IO.IOException;

    private static string Describe(Exception ex) => ex is Win32Exception win32
        ? $"Windows 包操作失败（代码 {win32.NativeErrorCode}）：{win32.Message}"
        : $"Windows 包操作失败（0x{ex.HResult:X8}）：{ex.Message}";
}
