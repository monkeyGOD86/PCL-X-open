# Minecraft Windows 基岩版扩展：仓库审计与可行性报告

> 审计日期：2026-10-27。范围仅为当前目录中的源码快照与公开技术资料；本轮未修改业务代码、未操作游戏数据，也未在正版基岩版安装上做运行验证。
>
> 状态用语：**已证实**＝本仓库源码或官方文档可直接证实；**方案可行、待验证**＝有平台接口依据，尚未通过本项目的实机验证；**待验证**＝不得视为已支持。

## 1. 结论与推荐边界

**可以在现有 WPF 程序中增加独立的 Bedrock 后端。** Java 启动链集中在 `ModLaunch.vb`，共享任务、日志、文件工具和主窗口导航可用作接入点；不要让 Bedrock 进入 `McLaunchStart`、`McInstance`、`McLoginLoader` 或 `DlClientFix`。这是一项架构判断，**不是基岩版启动已完成验证**。

首个内部可用增量建议限于：识别当前 Windows 用户已注册的正版 Minecraft for Windows 安装、展示可可靠读取的包版本与缺失前置条件、通过系统注册的应用入口发起启动、清楚区分“系统接受启动请求”和“游戏确已运行”。对外 P0 MVP 再加入 `.mcpack`/`.mcaddon` 静态校验、预览和导入：优先验证交由游戏自身文件关联处理，无法可靠验证时才设计经确认数据目录的安全写入方案。导入操作完成不等于游戏确认安装成功；未验证前该项不能标为已支持。P1 包管理、世界写入、旧版本部署和隔离实例不应成为启动路径的依赖。

| 能力 | 本轮判断 | 依据与限制 |
|---|---|---|
| 独立 Bedrock 启动后端 | 方案可行、待验证 | 主程序允许添加模块和页面；须在真实 GDK/UWP 包上验证发现与激活。 |
| 检测当前用户已注册安装 | 方案可行、待验证 | Windows 包枚举可返回包身份、版本、安装位置；不可据目录存在判断已安装。 |
| 使用系统授权启动 | 方案可行、待验证 | Windows AUMID 应用激活机制存在；游戏内授权由 Store/Gaming Services/游戏处理，本项目不读取用户凭据，也不宣称可预判授权结果。 |
| 安全导入附加包 | 待验证 | 公开格式已明确；实际目标数据目录、文件关联和游戏导入行为随版本变化。对外 P0 MVP 需通过此门槛。 |
| 世界管理、历史版本、多实例 | 待验证 | 数据路径、云同步、包注册、授权及并发限制尚未实测。 |

## 2. 仓库真实技术架构

### 2.1 构建、目录和依赖

| 位置 | 职责与证据 |
|---|---|
| `README.md`、`LICENCE`、`.gitmodules` | README 指向 PCL 上游且提示源码按发行版同步；许可文件引用外部《PCL 分发有限许可》，并附“合理使用指南”；`MeloongCore` 作为子模块。目录中未发现独立的贡献指南。 |
| `Plain Craft Launcher 2/Plain Craft Launcher 2.vbproj` | VB.NET、**.NET Framework 4.8**、WPF、Windows `WinExe`，旧式显式文件清单项目；引用 `PCLCS`、`MeloongCore.Shared/Wpf` 和多份 `Resources/*.dll`。新增 VB/XAML 文件需显式写入项目文件。 |
| `PCLCS/PCLCS.csproj` | SDK 风格 C# `net48` 类库，C# 13、PolySharp；包含 Java 与 Minecraft 辅助模型，引用两个 MeloongCore 项目。 |
| `Plain Craft Launcher 2/Modules/Base/` | `ModLoader.vb` 的 `LoaderTask`/`LoaderCombo` 任务进度、`ModNet.vb` 下载、`PclLogger.vb` 日志和 `ModBase.vb` 的进程、INI、UI 工具。大量文件 API 来自 MeloongCore，不能在缺失子模块时推断具体实现。 |
| `Plain Craft Launcher 2/Modules/Minecraft/` | Java 版登录与启动、版本/实例、Java 运行时、客户端及 Mod 下载、整合包等。整体不是跨版本的通用游戏后端。 |
| `Plain Craft Launcher 2/Pages/`、`Controls/`、`FormMain.xaml(.vb)` | WPF 页面与控件；主窗口用 `PageType`/`PageChangeActual` 切换页面，标题栏按钮的 `Tag` 对应页面值。 |
| `.github/workflows/build.yml` | Windows runner 上恢复 PCLCS、MSBuild 编译 Debug/Release，并上传 exe；触发路径仅覆盖主 WPF 目录和工作流。未发现测试项目或测试 CI。 |
| `patches/` | 随仓库保存的发行版 patch 文件；不等同可复现的 Git 历史。 |

依赖方向（按项目引用和源码调用）：

```text
Plain Craft Launcher 2 (VB.NET / WPF / net48)
  ├─ PCLCS (C# / net48) ─┬─ MeloongCore.Shared
  │                      └─ MeloongCore.Wpf
  ├─ MeloongCore.Shared / Wpf
  ├─ Modules/Base (任务、网络、日志、文件/进程封装)
  └─ Modules/Minecraft (Java 专用) ← Pages/PageLaunch、PageInstance、PageDownload

建议增加：Pages/PageBedrock → Modules/Bedrock → Windows 包与激活适配器
                                      └─ 复用 Modules/Base 与 MeloongCore 的通用能力
```

目标操作系统为 Windows 桌面（WPF、Windows 注册表和 Win32 API）；并非跨平台启动器。`MeloongCore/` 在当前快照为空，且目录没有 `.git`；因此本轮不能完成本地编译或核对被固定的子模块代码。`PCLCS` 本身没有 Bedrock 实现。

### 2.2 Java 启动链与相关状态

1. `Pages/PageLaunch/PageLaunchLeft.xaml.vb:611-630` 的按钮事件调用 `McLaunchStart()`；另有 `FormMain.xaml.vb` 的键盘快捷路径进入同一按钮逻辑。
2. `Modules/Minecraft/ModLaunch.vb:31-69` 在 UI 线程检查已有任务、选中 Java 实例及锁，随后启动 `McLaunchLoader`。该函数的返回值只表示已开始启动任务。
3. `ModLaunch.vb:106-151` 先调用 `McLaunchPrecheck`，再组装 `LoaderCombo`：Java 运行时、`McLoginLoader`、`DlClientFix`、启动参数、natives、预启动命令、`McLaunchRun`、等待窗口、结束处理。`ModLaunch.vb:234` 的购买/赞助提示也属于 Java 预检查；不要因引入 Bedrock 而删改。
4. `ModLaunch.vb:2093` 的 `McLaunchRun` 以 Java 可执行文件创建 `ProcessStartInfo`，传入 Java 参数并持有进程；`McLaunchWait` 再检查窗口。这套逻辑不适用于 Windows 注册的基岩版应用。
5. Java Microsoft OAuth、第三方/离线登录都位于 `ModLaunch.vb:296-607` 等段落；已有 Java token 不是 Windows 基岩版的授权证明。Bedrock 不读取或转用这些设置。
6. `Modules/Minecraft/ModMinecraft.vb:30-189` 维护 `.minecraft` 文件夹；`McInstance` 在同文件 `:218` 起按 `versions/<名称>` 建模，并使用 `PCL.ini`、`PCL/Setup.ini` 和 `PCL/config.json`。`PathIndie` 是 Java 版数据隔离策略，不可视为基岩版实例隔离。

### 2.3 可复用与须新建

**可复用**：`ModLoader.vb` 的任务/进度与任务栏反馈、`PclLogger.vb` 的日志和提示、`ModNet.vb` 的通用下载器（将来仅用于合法来源）、主窗导航与控件、`Settings.vb` 的全局设置机制、经过安全审查的 `FileUtils`/`PathUtils`。`ModModpack.vb` 的 ZIP 使用只能参考交互方式，**不能直接当作安全的 Bedrock 解压器**。

**必须新建**：Bedrock 安装模型与检测服务、Windows 包枚举与激活适配器、与 Java 分离的启动状态/错误码、Bedrock 页面、附加包静态解析和安全安装事务、数据目录解析器、世界备份事务。后四项随 P0/P1 逐步交付。Windows 适配层必须集中封装包身份、AUMID、包版本、运行时前置条件及启动请求；UI 不应散落 `Get-AppxPackage` 或硬编码游戏目录。

配置现状：`Pages/PageSetup/Settings.vb:200-271,331-339` 按 Normal/Registry/Instance 三种来源保存到 `Setup` INI、`HKCU` 或 Java 实例 `PCL/Setup.ini`。推荐 Bedrock 新设置使用独立前缀与独立配置文件，保持旧键、旧默认值和 Java 实例文件原样；只在需要显示偏好时添加新的全局键。新增元数据写入建议放在 PCL 自有的独立 `Bedrock` 子目录，**不把它误作游戏数据目录**，最终路径以 `Paths` 实际语义和权限测试为准。任何世界路径必须经平台适配器重新发现并由用户确认。

## 3. Windows UWP / GDK 可行性与限制

| 主题 | 已查证事实 | 本项目的实现判断 |
|---|---|---|
| 安装识别 | Windows 的 `PackageManager.FindPackagesForUser` 可查询当前用户安装包信息；AUMID 由包 family name 与应用 ID 构成，且独立于安装路径。参见 [包枚举](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuser)、[AUMID 查找](https://learn.microsoft.com/en-us/windows/configuration/store/find-aumid)。 | 先用受支持包身份的白名单与注册信息识别 Release/Preview、UWP/GDK；识别规则和当前 Minecraft 实际包身份须实机确认。不可仅检查 `WindowsApps` 或 exe 名。 |
| 启动 | 微软文档给出 `shell:AppsFolder\<AUMID>` 启动商店应用的方式；`IApplicationActivationManager.ActivateApplication` 返回 HRESULT 与一个进程 ID。GDK 文档强调正确的 **app-launch** 会提供游戏运行时上下文，直接运行 exe 可能没有该上下文。参见 [Store 启动例子](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-transition-users-from-your-web-unpackaged-app-to-store-packaged-app)、[激活 API](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication)、[GDK app-launch](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/game-config/microsoftgameconfig-overview)。 | 优先验证系统入口激活。调用成功只报告“启动请求已提交/系统已接受”；进程 ID 或窗口只能作为后续探测信号，不能直接推断游戏已成功进入主菜单。GDK 与 UWP 分开测试。 |
| 授权 | GDK 零售游戏授权由 Microsoft Store/Gaming Services 等平台组件和游戏处理；有授权也可能受离线或并发限制。参见 [GDK 授权说明](https://learn.microsoft.com/en-us/gaming/gdk/docs/store/commerce/getting-started/xstore-licensing-setup)、[共享/并发模型](https://learn.microsoft.com/en-us/xbox/gdk/docs/store/commerce/fundamentals/xstore-product-sharing-model-for-games)。 | **可以不接触账号密码而向系统请求启动正版游戏**。安装检测不是购买证明；不模拟登录、不缓存 Bedrock 凭据、不绕过许可，也不以 Java OAuth 结果判断 Bedrock 授权。 |
| GDK 与 UWP | GDK 是 Win32/Gaming Runtime 路径，零售 GDK 包需配置身份与注册；UWP 为 AppContainer 模型。包可安装在非默认位置，不能依赖固定安装根目录。参见 [GDK 配置](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/game-config/microsoftgameconfig-overview)、[打包桌面应用](https://learn.microsoft.com/en-us/windows/uwp/porting/desktop-to-uwp-behind-the-scenes)。 | 两类包共用上层接口，但检测、运行时检查和数据目录规则按安装类型分支；Unsupported/Unknown 要明确显示。 |
| 用户数据与世界 | GDK 游戏可使用多种保存机制，微软文档说明直接改本地游戏存档可能与云同步冲突。社区/微软问答列举过 UWP `LocalState` 和迁移后的 GDK `Roaming` 路径，但它们不是本项目可固定依赖的契约。参见 [GDK 游戏存档](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/game-save/game-saves-walkthroughs-and-samples)、[迁移案例](https://learn.microsoft.com/en-gb/answers/questions/5614191/i-lost-my-minecraft-world-after-restoring-my-minec)。 | 只把旧路径作为候选探测；GDK 账户子目录、Preview、云同步状态及权限须在真实安装中验证。写世界前备份、校验、原子替换，并要求游戏关闭。 |
| 包格式 | 官方将 `.mcpack` 定义为 ZIP 资源/行为包、`.mcaddon` 定义为包含 `.mcpack`/`.mcworld` 的 ZIP；`manifest.json` 含 header UUID、版本、modules、dependencies，名称可能需要进一步本地化处理。参见 [格式](https://learn.microsoft.com/en-us/minecraft/creator/documents/minecraftfileextensions)、[manifest](https://learn.microsoft.com/en-us/minecraft/creator/reference/content/addonsreference/packmanifest)。 | 静态解析可独立于游戏安装完成；导入与卸载需确认有效目标和恢复策略。拒绝路径穿越、绝对路径、链接重解析、重复条目、超大展开量/嵌套深度等。 |

**最小技术验证程序（下一阶段先做）**：在有正版 Windows 安装的测试机上，分别记录 Release/Preview 的包身份、manifest 应用 ID、版本与安装类型；以开始菜单正常启动作为基线，比较 `shell:AppsFolder` 和激活 API 的行为；覆盖游戏未安装、仅另一 Windows 用户安装、Gaming Services 故障、离线、无权运行和已运行状态；每项只记录包元数据、HRESULT/系统错误及观察到的状态，不记录凭据或 token。至少一台 GDK 和一台可用 UWP 环境才可宣称两种类型兼容。旧 UWP 是否还能合法安装取得也是**待验证**，不能为测试而改动受保护包。

## 4. 候选开源项目与许可证

| 项目 | 可借鉴的公开事实 | 许可证与引入建议 |
|---|---|---|
| [BedrockBoot](https://github.com/Round-Studio/BedrockBoot) | C#/.NET/Avalonia、Windows/Linux 的 Bedrock 启动器，可作包识别、分层与失败状态的技术参照。 | 仓库标识 GPL-3.0，并在 README 说明附加的命名/版权声明要求；具体可移植性须按目标文件、许可证全文及权利人确认。当前**只参考行为与公开文档，不复制代码或二进制**。 |
| [BMCBL 活跃仓库](https://github.com/Chlna6666/Better-Minecraft-Bedrock-Launcher) | Rust/GPUI，自述支持 Windows UWP/GDK、版本、世界和附加包；它的功能表属于项目自述，不等于本项目验证。 | GPL-3.0。`BMCBL` 组织下的[同名旧仓库](https://github.com/BMCBL/Better-Minecraft-Bedrock-Launcher)是另一仓库，勿混淆版本和许可。Rust/GPUI 实现难以直接复用至 WPF/net48；仅作设计比较。 |
| [LeviLauncher](https://github.com/LiteLDev/LeviLauncher) | 自述支持 GDK/UWP、正版授权、版本与实例；其[要求文档](https://levilauncher.levimc.org/guide/requirements-installation)分别列出 GDK 与 UWP 的前置条件。 | README 明示 GPL-3.0-only，另有第三方组件声明。实现含包部署、账号、原生加载等超出 P0/P1 的高风险范围；只作对照。 |
| [MeloongCore](https://github.com/Meloong-Git/MeloongCore) | 已是项目子模块，提供通用文件、配置和 WPF 工具。 | 上游公开仓库标示 Apache-2.0；本地固定版本内容尚未取得，实施前核对固定提交的 LICENSE/NOTICE。 |

**本仓库许可门槛**：本地 `LICENCE` 引用外部《PCL 分发有限许可》全文，本轮无法读取该外部页面；同文件“合理使用指南”自己说明并非法定协议。故不能仅凭指南判定改版名称、分发、开源范围或 GPL 代码合并已获许可。Fork 已属于指南所述“重度使用”典型情况；发布前应取得并核对有效许可全文、上游署名/改版要求、内置 DLL/JAR 授权与公告。**GPL 项目代码与现有 PCL 自定义许可的兼容性尚未确认**；默认独立实现 Windows 平台 API 与公开文件格式，避免复制 GPL 源码。以上为工程许可风险评估，不是法律意见。

远端 Fork 与上游当前 `main` 的 [GitHub compare](https://github.com/Meloong-Git/PCL/compare/main...monkeyGOD86:main) 经 API 查询：2026-10-27 时 Fork 领先 1 个提交、落后 0，唯一变更文件为 `Plain Craft Launcher 2/Modules/ModSecret.vb`（1 行替换，提交说明 `open game download`）。本地快照无 `.git`，无法证明它与该远端提交逐字一致，也无法检查本地未提交更改；实施前用完整 Git 检出和子模块初始化重新确认。敏感配置差异未在本报告展开。

## 5. 风险清单与候选路线

| 风险 | 级别 | 控制与门槛 |
|---|---|---|
| GDK/UWP 包身份、应用 ID、激活方式因通道/更新变化 | 高 | 使用注册包信息，不固定 exe 路径；在双类型实机验证并保留 Unknown 状态。 |
| 请求被接受但游戏因授权或运行时失败退出 | 高 | 分层状态与可读错误；不把 `Process.Start` 或 HRESULT 成功写成“游戏已运行”。 |
| 世界/附加包路径变化或云存档竞争 | 高 | 先只读扫描；写入前验证目标、游戏退出、备份、哈希、原子写和恢复演练。 |
| `.mcaddon` 压缩包攻击 | 高 | 独立安全解析器与测试；限制条目、大小、压缩比、深度和最终目标根路径。 |
| GPL 与 PCL 分发许可冲突 | 高 | 不拷贝候选项目代码；发布和外部依赖引入前核对许可全文。 |
| 子模块缺失、无测试和 CI 覆盖不足 | 中 | 完整检出后建立构建基线；下一阶段补 Bedrock 纯逻辑测试与 CI 路径。 |
| 历史版本来源、部署、包注册及多实例并发 | 高 | 独立 P2 实验；找不到合法包或系统不支持隔离时提供限制说明，不覆盖原安装。 |

路线对比：

| 路线 | 优点 | 代价/结论 |
|---|---|---|
| A. 系统包发现 + 系统应用激活 | 不接触凭据/游戏包；最符合正版优先与现有架构 | **推荐 P0**；仍须 GDK/UWP 实机验证。 |
| B. 调用官方启动器或让用户从开始菜单启动 | 平台行为稳定，适合激活失败回退 | 体验较弱；可以作为清晰回退入口，不能声称由 PCL 启动成功。 |
| C. 自行下载/注册历史游戏包并切换实例 | 理论上可支持 P2 | 合法来源、授权、注册冲突、数据隔离和长期维护风险高；P2 独立立项。 |
| D. 移植 BedrockBoot/BMCBL/LeviLauncher 的登录或原生注入链 | 可能带来高级能力 | 许可与安全/维护成本过高，且不符合首期目标；不推荐。 |

## 6. 下一阶段的文件级实施计划（本轮未实施）

按可审查的小提交推进；文件名为建议，最终以完整检出后的约定为准。

| 顺序 | 新增 / 修改文件 | 目标与完成门槛 |
|---|---|---|
| 0 | `docs/bedrock/02-architecture.md`；独立 `tools/bedrock-probe/`（是否新建项目待定） | 记录包身份、激活与数据目录实机矩阵、异常码和选定接口；先消除 GDK/UWP 不确定性。探针不得改游戏数据。 |
| 1 | 新增 `Plain Craft Launcher 2/Modules/Bedrock/BedrockModels.vb`、`BedrockInstallationDetector.vb`、`WindowsBedrockPackageAdapter.vb`；修改 `Plain Craft Launcher 2/Plain Craft Launcher 2.vbproj` | `Unknown/Uwp/Gdk` 安装类型及明确检测错误；对系统接口设边界，离线单元测试使用替身。 |
| 2 | 新增 `Plain Craft Launcher 2/Modules/Bedrock/WindowsBedrockLaunchAdapter.vb`、`BedrockLaunchService.vb` | 接受安装模型、发起应用激活、返回 `Requested/Accepted/Observed/Failed` 及错误；不调用 Java `McLaunchStart` 或读取登录缓存。 |
| 3 | 新增 `Plain Craft Launcher 2/Pages/PageBedrock/PageBedrockLeft.xaml(.vb)`、`PageBedrockRight.xaml(.vb)`；修改 `FormMain.xaml`、`FormMain.xaml.vb`、`Plain Craft Launcher 2.vbproj` | 独立入口、状态/版本/启动按钮和错误反馈。`PageType` 是公开事件 API，保留现有枚举值并追加新值；检查页面隐藏设置与导航动画。 |
| 4 | 新增 `Plain Craft Launcher 2/Modules/Bedrock/BedrockPackManifest.vb`、`BedrockPackReader.vb`、`BedrockArchiveValidator.vb`、`BedrockAddonImportService.vb`；新建测试项目 | 只读预览 `.mcpack`/`.mcaddon`、目录包；覆盖 UUID、版本、依赖、本地化名称占位、异常 ZIP 与路径安全。实机验证文件关联导入及结果提示；可验证后才启用 P0 导入按钮。 |
| 5 | 新增 `BedrockDataDirectoryResolver.vb`、`BedrockAddonTransaction.vb`、`BedrockWorldBackup.vb` 和对应页面/测试 | P1：先确定实例/数据根，再做安装、卸载、世界备份恢复。保留操作日志与可执行回滚。 |
| 6 | 修改 `.github/workflows/build.yml`；新增 Bedrock 测试项目和诊断文档 | CI 触发包含 `PCLCS/**`、`MeloongCore` 相关更新及测试目录；Windows build + 独立纯逻辑测试。实机包/授权测试为人工门槛。 |

可能的接口草图：`IBedrockPackageAdapter.FindInstalledForCurrentUser() -> IReadOnlyList(Of BedrockInstallation)`、`IBedrockLaunchAdapter.RequestLaunch(installation) -> BedrockLaunchResult`、`IBedrockDataDirectoryResolver.Resolve(installation, userSelection) -> BedrockDataLocation`。接口只在 Bedrock 命名空间使用；不强制改造现有 Java 抽象。配置采用新命名空间与独立存储，读取旧配置的默认路径保持不变。

## 7. 工作量、验证计划和阶段出口

在完整检出、可用 Windows 测试机与至少一个正版 GDK 安装具备的前提下，**粗估 1 人日＝8 小时**：仓库/平台验证与架构文档 4–7 人日；P0 检测、启动、独立 UI 和错误状态 8–15 人日；安全静态包解析与预览 5–8 人日；文件关联导入验证、交互与结果提示 3–6 人日；P1 附加包事务、世界备份/恢复和测试 12–22 人日；CI/回归与文档 4–7 人日。对外 P0 合计约 **20–36 人日**，连同 P1 约 **36–65 人日**。这是排期区间，不含 GDK/UWP 包版本兼容问题、许可处理或 P2/P3；若文件关联导入不可用，改为安全写入的工作量须重新评估。如需同时保证 UWP 与 GDK 实机兼容，须按测试矩阵追加时间。P2 版本/多实例不做可靠承诺，应先用独立 5–10 人日验证可否合法部署并隔离数据。

本轮验证结果：已阅读 README、许可提示、解决方案/项目文件、CI 和主要模块；完成源码静态追踪、远端 Fork 对比与官方 Windows/Minecraft 文档查证。**未编译、未运行测试、未在本机验证基岩版安装或启动**。原因是本地 `MeloongCore` 子模块为空、无 Git 元数据，且未提供可确认的正版测试安装；不存在可报告的 P0 功能验收结果。

下一阶段的退出条件：完整检出与固定子模块可编译；探针在目标 Windows 机器上记录 GDK/UWP 适用范围；合法授权的现有安装可经系统入口请求启动；错误信息能区分未安装、未知包、平台前置条件、激活失败；Java 原有启动和配置经回归；如某安装类型或目录无法验证，在 UI 与文档中标为“不支持/待验证”，不默认写入用户数据。
