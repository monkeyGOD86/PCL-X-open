# Bedrock Windows 包只读探针

此独立探针查询**当前 Windows 用户**注册的指定包，读取包版本、应用清单中的应用 ID 和 `MicrosoftGame.config` 是否存在。它不启动应用、不查询其他用户、不读取凭据或游戏数据，也不修改注册或文件。默认查询 `Microsoft.MinecraftUWP`；这个名称不能单独证明安装类型、游戏授权或可启动性。输出中的包族和应用 ID 仅表示检测到的系统元数据。

在 Windows PowerShell 5.1 中运行：

```powershell
powershell.exe -NoProfile -File .\tools\bedrock-probe\Get-BedrockPackageInfo.ps1
```

验证不存在的包名（不需要卸载任何东西）：

```powershell
powershell.exe -NoProfile -File .\tools\bedrock-probe\Get-BedrockPackageInfo.ps1 -PackageName No.Such.Package.For.Bedrock.Probe
```

输出为 JSON：`queryStatus` 为 `Found`、`NotFound` 或 `QueryFailed`。前两者退出码为 0，查询失败为 1；`manifestStatus` 为 `Unavailable` 和 `microsoftGameConfig` 为 `Unknown` 时不得推断缺失。输出不含安装路径。此探针是技术验证工具，**不作为生产检测器或支持范围白名单**；预览版、权限异常、旧 UWP 和启动行为均待单独验证。
