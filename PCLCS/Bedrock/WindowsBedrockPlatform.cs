using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PCLCS.Bedrock;

/// <summary>Uses Windows package registration for the current user; it never reads credentials.</summary>
public sealed class WindowsBedrockPackageCatalog : IBedrockPackageCatalog {
    private const int InsufficientBuffer = 122;
    private static readonly (string Family, string Channel)[] Families = [
        ("Microsoft.MinecraftUWP_8wekyb3d8bbwe", "正式版"),
        ("Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe", "预览版")
    ];

    public IReadOnlyList<BedrockInstallation> FindInstalled() {
        var results = new List<BedrockInstallation>();
        string gamingServices = GetGamingServicesState();
        foreach (var (family, channel) in Families) {
            foreach (string fullName in GetPackageNames(family)) {
                var identity = GetIdentity(fullName);
                bool recognized = false;
                foreach (string aumid in GetApplicationIds(fullName)) {
                    // Game is the current GDK entry; App is used by older UWP packages.
                    if (!string.Equals(aumid, family + "!Game", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(aumid, family + "!App", StringComparison.OrdinalIgnoreCase)) continue;
                    recognized = true;
                    results.Add(new(family, fullName, aumid, identity.Version, identity.Architecture,
                        channel, GetRuntimeEvidence(fullName), gamingServices));
                }
                if (!recognized) results.Add(new(family, fullName, "", identity.Version, identity.Architecture, channel, "入口不受支持", gamingServices));
            }
        }
        return results;
    }

    private static string GetGamingServicesState() {
        try {
            return GetPackageNames("Microsoft.GamingServices_8wekyb3d8bbwe").Any() ? "已注册" : "未发现";
        } catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) {
            return "无法确认";
        }
    }

    private static IEnumerable<string> GetPackageNames(string family) {
        uint count = 0, chars = 0;
        int error = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref chars, IntPtr.Zero);
        if (error == 0 && count == 0) return [];
        if (error != InsufficientBuffer) throw new Win32Exception(error);
        if (count > 1024 || chars > 1024 * 1024) throw new InvalidOperationException("Windows 返回了异常的包列表大小。");
        IntPtr pointers = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try {
            pointers = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
            buffer = Marshal.AllocHGlobal(checked((int)chars * 2));
            error = GetPackagesByPackageFamily(family, ref count, pointers, ref chars, buffer);
            if (error != 0) throw new Win32Exception(error);
            var names = new List<string>((int)count);
            for (int i = 0; i < count; i++) names.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointers, i * IntPtr.Size))!);
            return names;
        } finally {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(pointers);
        }
    }

    private static IEnumerable<string> GetApplicationIds(string fullName) {
        int error = OpenPackageInfoByFullName(fullName, 0, out IntPtr package);
        if (error != 0) throw new Win32Exception(error);
        try {
            uint length = 0, count = 0;
            error = GetPackageApplicationIds(package, ref length, IntPtr.Zero, out count);
            if (error == 0 && count == 0) return [];
            if (error != InsufficientBuffer) throw new Win32Exception(error);
            if (length > 1024 * 1024) throw new InvalidOperationException("Windows 返回了异常的应用列表大小。");
            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try {
                error = GetPackageApplicationIds(package, ref length, buffer, out count);
                if (error != 0) throw new Win32Exception(error);
                if (count > length / IntPtr.Size) throw new InvalidOperationException("Windows 返回了无效的应用列表。");
                var ids = new List<string>((int)count);
                for (int i = 0; i < count; i++) ids.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size))!);
                return ids;
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        } finally {
            ClosePackageInfo(package);
        }
    }

    private static (string Version, string Architecture) GetIdentity(string fullName) {
        uint bytes = 0;
        int error = PackageIdFromFullName(fullName, 0, ref bytes, IntPtr.Zero);
        if (error != InsufficientBuffer) throw new Win32Exception(error);
        if (bytes > 1024 * 1024) throw new InvalidOperationException("Windows 返回了异常的包身份大小。");
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try {
            error = PackageIdFromFullName(fullName, 0, ref bytes, buffer);
            if (error != 0) throw new Win32Exception(error);
            var id = Marshal.PtrToStructure<PackageId>(buffer);
            ulong v = id.Version;
            string version = $"{v >> 48}.{(v >> 32) & 0xffff}.{(v >> 16) & 0xffff}.{v & 0xffff}";
            string architecture = id.Architecture switch { 0 => "x86", 5 => "ARM", 9 => "x64", 12 => "ARM64", _ => "未知" };
            return (version, architecture);
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetRuntimeEvidence(string fullName) {
        try {
            uint length = 0;
            int error = GetPackagePathByFullName(fullName, ref length, null);
            if (error != InsufficientBuffer || length > 32768) return "未知";
            var path = new StringBuilder((int)length);
            error = GetPackagePathByFullName(fullName, ref length, path);
            if (error != 0) return "未知";
            return Directory.EnumerateFiles(path.ToString(), "MicrosoftGame.config").Any() ? "GDK 候选" : "未知";
        } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException) {
            return "未知";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PackageId {
        public uint Reserved;
        public uint Architecture;
        public ulong Version;
        public IntPtr Name, Publisher, ResourceId, PublisherId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int OpenPackageInfoByFullName(string fullName, uint reserved, out IntPtr packageInfo);
    [DllImport("kernel32.dll")]
    private static extern int GetPackageApplicationIds(IntPtr packageInfo, ref uint length, IntPtr buffer, out uint count);
    [DllImport("kernel32.dll")]
    private static extern int ClosePackageInfo(IntPtr packageInfo);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageIdFromFullName(string fullName, uint flags, ref uint length, IntPtr packageId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint length, StringBuilder? path);
}

public sealed class WindowsBedrockActivator : IBedrockActivator {
    public uint Activate(string appUserModelId) {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try {
            int result = manager.ActivateApplication(appUserModelId, null, 0, out uint processId);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            return processId;
        } finally {
            Marshal.ReleaseComObject(manager);
        }
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
    }
}
