using System.Diagnostics;
using System.Text;
using PCLCS.Bedrock;

internal static class Program {
    private static int Main(string[] args) {
        try {
            if (args.Length == 1 && args[0] == "list") { List(); return 0; }
            if (args.Length == 1 && args[0] == "fixture") { Fixture(); return 0; }
            if (args.Length == 4 && args[0] == "plan") { _ = Plan(args[1], args[2], args[3]); return 0; }
            if (args.Length == 4 && args[0] == "install") { Install(args[1], args[2], args[3]); return 0; }
            if (args.Length == 4 && args[0] == "rollback") { Rollback(args[1], args[2], args[3]); return 0; }
            Console.Error.WriteLine("用法：list | fixture | plan <包族> <目标数据根> <测试包目录> | install <包族> <目标数据根> <测试包目录> | rollback <包族> <目标数据根> <事务目录>");
            return 2;
        } catch (Exception ex) {
            Console.Error.WriteLine("操作未完成：" + ex.Message);
            return 1;
        }
    }

    private static void List() {
        var installations = new WindowsBedrockPackageCatalog().FindInstalled();
        foreach (var installation in installations) {
            Console.WriteLine($"包族：{installation.FamilyName}  通道：{installation.Channel}  包版本：{installation.Version}");
            foreach (var root in new BedrockDataRootFinder().Find(installation))
                Console.WriteLine($"  {root.Source}：{root.Path}");
        }
        if (installations.Count == 0) Console.WriteLine("当前用户未发现受支持的已注册游戏。");
    }

    private static void Fixture() {
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        EnsureNoLinks(parent);
        foreach (var installation in new WindowsBedrockPackageCatalog().FindInstalled())
            foreach (var root in new BedrockDataRootFinder().Find(installation))
                if (IsWithin(root.Path, parent)) throw new InvalidOperationException("系统临时目录位于游戏数据中，不能创建测试包。");
        string directory = Path.Combine(Path.GetFullPath(parent), "pcl-bedrock-test-pack-" + Guid.NewGuid().ToString("N"));
        if (!Directory.Exists(parent) || Directory.Exists(directory) || File.Exists(directory)) throw new IOException("临时目录不可用。");
        Directory.CreateDirectory(directory);
        string packUuid = Guid.NewGuid().ToString("D"), moduleUuid = Guid.NewGuid().ToString("D");
        string manifest = "{\"format_version\":2,\"header\":{\"name\":\"PCL Compatibility Test\",\"description\":\"Temporary resource pack for validation\",\"uuid\":\"" + packUuid +
            "\",\"version\":[1,0,0],\"min_engine_version\":[1,20,0]},\"modules\":[{\"type\":\"resources\",\"uuid\":\"" + moduleUuid + "\",\"version\":[1,0,0]}]}";
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest, new UTF8Encoding(false));
        string texts = Path.Combine(directory, "texts");
        Directory.CreateDirectory(texts);
        File.WriteAllText(Path.Combine(texts, "languages.json"), "[\"en_US\"]", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(texts, "en_US.lang"), "pcl.test=Temporary validation pack\n", new UTF8Encoding(false));
        var preview = BedrockDirectoryInstaller.Preview(directory);
        Console.WriteLine("测试包目录：" + directory);
        Console.WriteLine("UUID：" + preview.Pack.Uuid + "  SHA-256：" + preview.Fingerprint);
        Console.WriteLine("测试包由本工具创建，不含游戏文件；创建它没有修改游戏数据。");
    }

    private static (BedrockInstallation Installation, BedrockDataRoot Target, IReadOnlyList<BedrockDataRoot> Scope) Resolve(string family, string targetPath) {
        var matches = new WindowsBedrockPackageCatalog().FindInstalled().Where(item =>
            string.Equals(item.FamilyName, family, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("包族未注册或无法唯一识别，请先运行 list。");
        var scope = new BedrockDataRootFinder().Find(matches[0]);
        string normalized = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar);
        var target = scope.SingleOrDefault(item => string.Equals(Path.GetFullPath(item.Path).TrimEnd(Path.DirectorySeparatorChar),
            normalized, StringComparison.OrdinalIgnoreCase));
        return (matches[0], target ?? throw new InvalidOperationException("目标不是该游戏发现的候选目录。"), scope);
    }

    private static void Install(string family, string targetPath, string fixture) {
        EnsureGameClosed();
        var (installation, _, scope, preview) = Plan(family, targetPath, fixture);
        Console.WriteLine("确认游戏已关闭、同步已完成，且目标目录正确。将创建可恢复事务和新的包目录，不覆盖已有包。");
        Confirm();
        EnsureGameClosed();
        var (currentInstallation, currentTarget, currentScope) = Resolve(family, targetPath);
        if (!SameRegistration(installation, currentInstallation)) throw new IOException("游戏注册状态已变化，请重新开始。");
        if (!SameScope(scope, currentScope))
            throw new IOException("候选目录已变化，请重新开始。");
        string transaction = BedrockAddonTransaction.Prepare(currentTarget, [new BedrockPreparedPack(fixture, preview)], currentScope);
        Console.WriteLine("事务目录（请保留以便恢复）：" + transaction);
        EnsureGameClosed();
        var (finalInstallation, finalTarget, finalScope) = Resolve(family, targetPath);
        if (!SameRegistration(installation, finalInstallation) || !SameScope(scope, finalScope))
            throw new IOException("准备期间游戏注册或数据目录已变化，请保留事务并检查。");
        BedrockAddonTransaction.Commit(finalTarget, transaction, finalScope);
        Console.WriteLine("包文件已提交。请手动启动游戏检查是否识别；这不代表游戏已加载或启用该包。");
    }

    private static (BedrockInstallation Installation, BedrockDataRoot Target, IReadOnlyList<BedrockDataRoot> Scope, BedrockDirectoryPreview Preview)
        Plan(string family, string targetPath, string fixture) {
        var (installation, target, scope) = Resolve(family, targetPath);
        ValidateFixture(fixture);
        var preview = BedrockDirectoryInstaller.Preview(fixture);
        if (preview.Pack.Name != "PCL Compatibility Test" || preview.Pack.Version != "1.0.0" ||
            preview.Pack.Modules.Count != 1 || preview.Pack.Modules[0] != "resources")
            throw new InvalidDataException("测试包清单不符合探针要求。");
        Console.WriteLine("目标包族：" + family + "\n注册包：" + installation.PackageFullName + "\n目标目录：" + target.Path + "\n包 UUID：" + preview.Pack.Uuid + "\n包指纹：" + preview.Fingerprint);
        Console.WriteLine("检查范围：");
        foreach (var root in scope) Console.WriteLine("  " + root.Path);
        var conflicts = BedrockAddonScope.Check(scope, [preview.Pack.Uuid]);
        if (!conflicts.Complete) throw new InvalidDataException("部分目录中的包无法识别，不能安全检查冲突：" + string.Join("；", conflicts.IncompleteRoots));
        if (conflicts.Conflicts.Count > 0) throw new InvalidDataException("发现相同 UUID 的现有包，不能测试。");
        if (!Directory.Exists(Path.Combine(target.Path, "resource_packs"))) throw new InvalidDataException("目标缺少 resource_packs 目录。");
        Console.WriteLine("只读预检查通过。计划创建事务目录及 resource_packs 下的新包目录；不覆盖现有文件。");
        Console.WriteLine("游戏对该布局的识别尚未验证；请在游戏关闭且同步完成后执行测试。");
        return (installation, target, scope, preview);
    }

    private static void Rollback(string family, string targetPath, string transaction) {
        EnsureGameClosed();
        var (installation, target, _) = Resolve(family, targetPath);
        Console.WriteLine("目标包族：" + family + "\n目标目录：" + target.Path + "\n事务目录：" + transaction);
        Console.WriteLine("确认游戏已关闭、同步已完成。未修改的包将移回暂存区，事务和文件会保留。");
        Confirm();
        EnsureGameClosed();
        var (currentInstallation, currentTarget, _) = Resolve(family, targetPath);
        if (!SameRegistration(installation, currentInstallation)) throw new IOException("游戏注册状态已变化，请重新检查。");
        BedrockAddonTransaction.Rollback(currentTarget, transaction);
        Console.WriteLine("恢复操作结束。请保留事务目录，并检查记录状态。");
    }

    private static bool SameRegistration(BedrockInstallation before, BedrockInstallation after) =>
        string.Equals(before.PackageFullName, after.PackageFullName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(before.AppUserModelId, after.AppUserModelId, StringComparison.OrdinalIgnoreCase);

    private static bool SameScope(IReadOnlyList<BedrockDataRoot> before, IReadOnlyList<BedrockDataRoot> after) =>
        before.Select(item => item.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(after.Select(item => item.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static void ValidateFixture(string fixture) {
        string path = Path.GetFullPath(fixture).TrimEnd(Path.DirectorySeparatorChar);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        EnsureNoLinks(temp);
        string name = Path.GetFileName(path);
        const string prefix = "pcl-bedrock-test-pack-";
        if (!string.Equals(Path.GetDirectoryName(path), temp, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(prefix.Length), "N", out _))
            throw new InvalidDataException("只接受此探针在系统临时目录创建的测试包。");
    }

    private static bool IsWithin(string parent, string path) {
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoLinks(string path) {
        string? current = Path.GetFullPath(path);
        while (current is not null) {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("临时路径包含链接或重解析点。");
            current = Directory.GetParent(current)?.FullName;
        }
    }

    private static void Confirm() {
        Console.Write("确认请输入 YES：");
        if (!string.Equals(Console.ReadLine(), "YES", StringComparison.Ordinal)) throw new OperationCanceledException("未确认。");
    }

    private static void EnsureGameClosed() {
        foreach (string name in new[] { "Minecraft.Windows", "GameLaunchHelper" }) {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) throw new InvalidOperationException("检测到游戏进程，请关闭游戏并等待同步后重试。"); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
