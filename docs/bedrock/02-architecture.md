# Bedrock Edition 总体架构设计

> 设计日期：2026-10-27；状态：架构提案。本文以 [01-feasibility.md](01-feasibility.md) 为基础，补充当前完整检出和只读探针所得证据。除独立探针外，本阶段未实现或接入 Bedrock 业务功能。

## 1. 设计决策和范围

Java 与 Bedrock 分别维护启动、账户、安装和数据模型。P0 使用已注册的 Windows 游戏安装及系统应用入口；P1 增加附加包和世界管理；P2 版本部署、数据隔离与多实例须另行验证。**包发现已在一台机器只读验证；激活、导入、UWP、授权及数据目录均待验证。**

不引入跨版本的统一 `IGameInstance`：现有 `McInstance` 依赖 Java 的 `versions`、JAR、`PathIndie` 和设置语义；包装它会把 Java 假设传到 Bedrock。需要共享时优先共用控件、日志、任务和网络等横向能力，而不是共享游戏实例。

明确的失败边界：Bedrock 页面和服务按需创建；检测、配置、探针或平台 API 失败只影响对应 Bedrock 操作，错误不得逃逸到应用初始化。不要在主窗口构造时扫描、加载游戏目录、创建配置或调用 WinRT。保留现有 Java 页面的快捷键和启动调用。

```text
FormMain / WPF 导航
  ├─ 现有 Java 页面 → ModLaunch / McInstance / Java 登录与下载
  └─ Bedrock 页面 → Bedrock 应用服务
       ├─ 安装检测 → IWindowsPackageCatalog → Windows 包 API
       ├─ 启动服务 → IWindowsApplicationActivator → Windows 应用激活
       ├─ P0 包预览/导入 → 校验器 / 系统文件激活（须实测）
       └─ P1 包与世界管理 → 数据根解析 / 事务和备份服务

横向复用：WPF 控件、PclLogger、LoaderTask、MeloongCore 的路径/文件能力、必要时 ModNet。
依赖只向右/向下；Windows 适配器与业务核心不引用具体页面、Java 登录或 McInstance。
```

## 2. 现有仓库的接入约束

| 部位 | 当前证据 | 设计约束 |
|---|---|---|
| 入口 | `FormMain.xaml` 中顶栏按钮的 `Tag` 是 0–4；`FormMain.xaml.vb:1191` 的 `PageType` 0–9 已公开。 | 追加 `Bedrock = 10`；保留全部现有枚举值。 |
| 导航 | `FormMain.xaml.vb:1375` 使用 `PanTitleSelect.Children(Stack)`，把枚举值当控件下标；`:1402` 显式订阅五个按钮。 | 若将 Bedrock 放在顶栏，**不能只追加值 10 和第六个按钮**：第六个子控件下标为 5。改用显式 `PageType → MyRadioButton` 映射，再补对应事件和 `PageChangeActual` 分支；检查返回栈、隐藏设置、窗口宽度和自定义事件。 |
| Java 快捷键 | `FormMain.xaml.vb:836,866` 对 `PageType.Launch` 专门处理 Escape 和 Enter。 | 保持 Java 的现有处理；Bedrock 页单独决定快捷键，不转发 Java 启动按钮。 |
| 后台任务 | `ModLoader.vb` 有工作线程回调和 UI 线程状态回调；`LoaderTask` 的委托是同步的。 | 用短生命周期 `LoaderTask` 封装同步服务或明确桥接异步 API，绝不传入无法等待的 `Async Sub`；取消只取消未提交工作，不能误杀已激活游戏。 |
| 现有设置 | `Settings.vb` 的 `Sources.Instance` 参数是 `McInstance`；全局配置使用 `PCL/Setup.ini` 或 HKCU。 | 不给 Bedrock 伪造 Java 实例，也不改写旧键、旧默认值或旧配置结构。 |
| 通用 JSON 配置 | 固定子模块的 `MeloongCore/Shared/General/Config.cs` 可写原子文件，但整个 JSON 读取失败时回退空对象并可能弹 UI 提示。 | 它可供普通非关键偏好评估；事务、迁移和备份索引需自己的显式损坏错误处理，不能把损坏配置当作空配置覆盖。 |
| 项目文件 | `Plain Craft Launcher 2.vbproj` 是显式包含源码和 XAML 的旧式项目。 | 每个新增 `.vb`、XAML 的 Compile/Page 项都要显式登记；不修改 Java 模块引用图。 |

本轮环境已变化：当前 HEAD 是 `87fea01e3d5066a30be46ec02800de0da5ffa087`，`MeloongCore` 子模块位于 `4c60c2962c3b50bc3fe96ed5dd9259b980173580`，已初始化；此前报告关于缺失 `.git` 和子模块的描述是**上一轮当时**的快照。现有 `MeloongCore/Tests/MeloongCore.Tests.csproj` 是子模块内的 TUnit 测试项目；顶层解决方案仍未提供 PCL/Bedrock 测试项目，主工作流未运行该子模块测试。

## 3. 模块和文件边界

以下名称是实施约定；每项在对应阶段单独提交，未进入的阶段不需要占位实现。

| 新增文件或目录 | 阶段 | 责任 |
|---|---|---|
| `Plain Craft Launcher 2/Modules/Bedrock/BedrockModels.vb` | P0 | 不可变的安装身份、通道、包类型证据、错误及启动结果；不持有 token 或游戏目录。 |
| `.../Bedrock/BedrockInstallationService.vb` | P0 | 合并平台返回的候选，识别允许的应用入口、版本和可用状态；不推断购买。 |
| `.../Bedrock/WindowsPackageCatalog.vb` | P0 | 仅负责当前用户包查询、包清单及必要的运行时证据；WinRT 投影/版本兼容经探针验证后选定。 |
| `.../Bedrock/WindowsApplicationActivator.vb`、`BedrockLaunchService.vb` | P0 | 重新验证目标身份，提交系统激活并返回细粒度结果；封装 COM/HRESULT，避免 UI 持有 COM 对象。 |
| `Plain Craft Launcher 2/Pages/PageBedrock/` | P0 | 安装列表、状态、启动、导入入口和反馈；页面只调用 Bedrock 服务，并在 Dispatcher 更新 UI。 |
| `.../Bedrock/BedrockPackReader.vb`、`BedrockArchiveValidator.vb`、`BedrockAddonImportService.vb` | P0 | 在限额内只读解析、预览和验证；经验证的系统导入提交。 |
| `.../Bedrock/BedrockDataDirectoryResolver.vb`、`BedrockAddonTransaction.vb`、`BedrockWorldBackup.vb` | P1 | 解析并确认目标数据根；安全安装/卸载、世界备份与恢复。数据根和事务不依赖 Java 路径。 |
| `tests/Bedrock.Tests/` | P0 起 | 纯模型、包格式、异常输入、平台接口替身和配置兼容测试；不依赖本机游戏。测试项目框架与 .NET 4.8 构建方式在实施时确定。 |
| `tools/bedrock-probe/` | 本阶段 | 独立只读包发现探针；不被主程序引用。 |

现有文件只计划改 `FormMain.xaml`、`FormMain.xaml.vb`、`Plain Craft Launcher 2.vbproj`；确需新全局偏好时才改 `Pages/PageSetup/Settings.vb`。测试接入阶段再改 `.github/workflows/build.yml` 的路径和测试步骤。`ModLaunch.vb`、Java 认证、`McInstance` 和 `ModDownload.vb` 不作为 Bedrock 的依赖。

## 4. 服务接口与状态约定

下面为设计草图，具体类型的可访问性和跨线程封送在实施时经编译测试确认：

```vb
' 由平台适配器实现；不访问 WPF 或 Java 全局状态。
Interface IWindowsPackageCatalog
    Function FindCandidates() As PackageQueryResult
End Interface

Interface IWindowsApplicationActivator
    Function Activate(target As BedrockInstallation) As ActivationResult
End Interface

Interface IBedrockDataDirectoryResolver
    Function Resolve(target As BedrockInstallation) As DataLocationResult
End Interface
```

### 4.1 安装模型

`BedrockInstallation` 至少包含：`PackageFamilyName`、`PackageFullName`、`ApplicationId`、`Aumid`、包版本、架构、通道（Release/Preview/Unknown）、安装类型（Gdk/Uwp/Unknown）、包状态、证据来源和当前用户范围。类型识别必须来自**经过验证的多项证据**，不得从 `Microsoft.MinecraftUWP` 名称推断 UWP。`MicrosoftGame.config` 存在是 GDK 候选信号，不能单独构成支持承诺。检测时只接受经过验证的 Microsoft 包身份和应用声明；未知身份显示不支持，不能用显示名或 exe 路径作为信任依据。

查询结果区分 `Found`、`NotFound`、`AccessDenied`、`Unavailable`、`Unknown`；查询或 manifest 失败不当作未安装。包版本只表示 Windows 包版本，未确认与游戏内显示版本一致时不要叫作“游戏版本”。每次启动前重新获取当前用户注册状态、身份、应用 ID 和包状态；已选记录消失、更新或改变身份时要求刷新选择。

### 4.2 启动状态和错误

| 状态 | 含义与可展示信息 |
|---|---|
| `Idle` / `Detecting` / `Ready` | 未开始、正在查询、已找到可提交的候选；Ready 不代表授权通过。 |
| `Requesting` | 正在向系统提交激活请求。 |
| `RequestAccepted` | API 或 Shell 接受请求；显示“已请求启动”，保留平台错误/返回信息。 |
| `ProcessObserved` | 仅在有可靠绑定到目标应用的观测证据时显示“已检测到游戏进程”；PID、已有进程和重用实例均需实测。 |
| `Failed` / `CanceledBeforeRequest` / `ObservationTimedOut` | 显示已知失败或仍无法确认；超时不等于游戏启动失败。提交后取消只停止等待，不结束游戏。 |

候选 `IApplicationActivationManager.ActivateApplication` 的 HRESULT 与 PID 语义见 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication)。它对本机 Minecraft GDK/UWP 的真实效果尚未测试；需先比较开始菜单、`shell:AppsFolder` 和 API。Shell 调用若只启动 Explorer，则只能报告提交，不能把 Explorer 的 PID 当作游戏 PID。错误分类保持 `NotInstalled`、`UnsupportedPackage`、`PackageQueryFailed`、`RuntimeUnavailable`、`ActivationFailed`、`PermissionDenied`、`ObservationUnknown`；系统未给出明确许可错误时不得猜测“未购买”。

### 4.3 P0 附加包导入

流程：选择目标安装 → 选择文件 → 有界读取并校验 → 展示所有包、UUID、类型、版本、依赖和警告 → 用户确认导入 → 将文件交给**经验证属于目标游戏的系统导入入口** → 显示请求结果。系统文件关联可能被任意其他应用注册，不能盲目 `ShellExecute`；须确认处理者与目标包/应用身份匹配，或使用经实测的目标包文件激活接口。无法确认处理者、Release/Preview 目标或安全处理时禁用一键导入并解释原因。操作系统接受文件请求不等于游戏已导入成功；游戏自身写入后的撤销机制未验证，不能声称启动器可回滚。

解析遵循 [Minecraft manifest 规范](https://learn.microsoft.com/en-us/minecraft/creator/reference/content/addonsreference/packmanifest)：保留 `format_version`、header UUID、原始版本与已知版本表示、modules、依赖 UUID **或** `module_name`、`min_engine_version` 和未识别字段；本地化键无法解析时显示键或安全回退。未知格式/结构明确拒绝或标记无法判断，不静默删字段。`.mcaddon` 可能含多个 `.mcpack` 和 `.mcworld`；P0 处理受支持附加包，遇到世界或未知内容明确提示并拒绝部分静默导入。

安全处理要求：限制输入大小、ZIP 条目数、每项及总实际解压字节、压缩比、嵌套层数、JSON 大小和时间；不要只信任头部声明大小。拒绝绝对路径、`..`、盘符/UNC、交替数据流、设备名、大小写或 Unicode 归一化碰撞、重复条目和链接/重解析点。目录包需要同样的根目录约束。读取外部资源只使用受限临时区；不直接把不可信 ZIP 交给现有通用解压工具并假设安全。阈值通过配置和测试样本确定，不能把阈值宣称为 Minecraft 官方上限。

## 5. 数据存储、迁移和恢复

### 5.1 所有权与生命周期

| 数据 | 拟存储位置及规则 |
|---|---|
| Java 设置/实例 | 保持既有 `PCL/Setup.ini`、HKCU 和 Java 实例配置；Bedrock 不读取 Java 凭据或覆盖其目录。 |
| Bedrock 启动器偏好 | 按需保存在 `Path.Combine(Paths.Base, "PCL", "Bedrock", "settings.json")`；`Paths.Base` 是可执行文件目录。首次只读检测不创建文件；写入失败向 Bedrock 页面报告，不自动迁移到另一处。 |
| 安装检测结果 | 内存缓存，显示前及启动前可刷新；Windows 注册状态是权威来源，配置中的上次选择只是提示。 |
| 游戏数据根 | 不存放于启动器私有目录；由适配器按具体安装和 Windows 用户发现并验证，必要时用户选择并确认。无可靠路径时只读或禁用修改。 |
| P1 备份及事务日志 | 独立于游戏目录的启动器管理目录，默认 `PCL/Bedrock/backups`、`PCL/Bedrock/transactions`；先检查写入权限和空间，必要时让用户选择可写位置。记录版本、目标身份、备份哈希和恢复步骤，避免在普通日志泄漏用户路径。 |
| 临时文件 | 每次操作独立目录，严格验证根与目标，任务完成或可安全恢复后清理；未完成事务的暂存不得静默删除。 |

示例偏好只供设计，字段不代表已实现：

```json
{
  "schemaVersion": 1,
  "selectedInstallation": {
    "packageFamilyName": "Microsoft.MinecraftUWP_8wekyb3d8bbwe",
    "applicationId": "Game"
  },
  "showImportWarnings": true
}
```

上述身份值来自本机观察，**不是硬编码的全版本清单**。配置不得保存账号密码、OAuth token、授权令牌或世界内容。新增数据使用有版本 schema；文件不存在时内存使用默认值，损坏、版本超前或无法读取时保留原文件、明确报错且禁止覆盖。需要迁移时先读并校验旧版本、创建带时间戳的可恢复备份、写同目录临时文件、刷新并验证后原子替换；失败保留源文件与备份。首次发布不迁移 Java 配置，也不移动游戏文件。必须经测试覆盖无文件、旧版本、未知新版本、损坏、权限不足、失败中断和回退。

### 5.2 P1 文件事务

1. 在进入世界/包修改前解析并向用户展示目标安装、Windows 用户范围、实际数据根与具体更改；如无法证明目标身份或游戏是否关闭，禁止写入并提示用户处理。检查游戏进程本身可能不可靠，不能仅凭进程名保证没有游戏占用或云同步。
2. 创建操作清单、校验内容、估算空间并备份每个会被替换或删除的既有文件；记录哈希、目录所有权和操作状态。只删除或替换清单中确认的对象，不把未识别内容归属启动器。
3. 在目标卷内受控的临时位置写入并验证，写入时再次检查根目录、符号链接/junction、锁和目标身份；对单文件采用同卷原子替换。多文件/整个世界更新**不是整体原子操作**，必须记录每步状态并提供失败后的逐项回滚。
4. 完成后再更新索引；若中断，下次读取事务日志，提示用户恢复或回滚，不自动删除备份。恢复前核对现状、哈希及冲突，目标已变化时停止并提供另存方案。世界备份不保证包含云端最新状态；需单独验证同步约束和游戏侧可读性。

## 6. 技术验证与测试门槛

### 6.1 本阶段已验证

- 当前用户的 Windows 包查询返回 `Microsoft.MinecraftUWP_8wekyb3d8bbwe`，包版本 `1.26.5203.0`，应用 ID `Game`，入口声明 `Windows.FullTrustApplication`，且安装根发现 `MicrosoftGame.config`；这是**GDK 候选证据**，未验证类型或启动。本机还查询到 Gaming Services 包，不能推断其运行正常。
- `tools/bedrock-probe/Get-BedrockPackageInfo.ps1` 在本机对默认包返回 `Found`，对虚构包名返回 `NotFound`；脚本只读当前用户元数据，未激活游戏或接触用户游戏数据。使用方式与结果语义见 [探针说明](../../tools/bedrock-probe/README.md)。
- 对已初始化的子模块执行 `dotnet restore PCLCS/PCLCS.csproj` 和 Visual Studio 2022 MSBuild Debug 构建，均成功。**仅验证构建**，没有运行 Java 游戏或完整回归测试。

### 6.2 后续验证矩阵

| 目标 | 方法 | 通过条件或失败处理 |
|---|---|---|
| Windows SDK/WinRT 与 net48 接入 | 独立最小程序调用当前用户包 API，记录 SDK/运行环境、权限和异常 | 不引入管理员权限或持久系统更改；不可访问返回 Unknown/AccessDenied。 |
| GDK 与 UWP 发现 | Release、Preview 和可合法获得的 UWP；另测未安装、不同用户、更新、卸载、无权限 | 身份、清单、版本与候选类型可解释；无法确认明确标 Unknown。 |
| 应用激活 | 基线开始菜单，对比系统应用入口和激活 API；记录 HRESULT、PID、重复激活、游戏退出与离线情况 | 仅在有证据时区分请求接受/进程观察；不将安装等同授权或游戏就绪。 |
| 附加包导入 | 验证目标游戏注册的文件入口、Release/Preview 指向和游戏实际导入提示 | 目标可确认才启用；关联异常时禁用，不能把请求提交当完成。 |
| P0 自动测试 | 模拟包服务和激活器；构造多包、失败、超时、取消、路径和 ZIP 异常数据 | 不依赖用户安装；状态、边界、配置不被改写可断言。 |
| P1 数据保护 | 临时数据目录故障注入：断电式中断、权限不足、空间不足、碰撞、备份和恢复 | 原始数据可从已验证备份恢复；路径越界和链接攻击被拒绝。 |
| Java 回归 | 完整构建及原有 Java 登录、版本选择和一次启动；比较配置与快捷键 | 无 Bedrock 包、探测失败、配置损坏时 Java 仍可工作；不打印账号/token。 |
| CI | Windows 构建与纯逻辑测试；扩大路径过滤 | 不把需要真实 Microsoft Store 游戏和账号的测试伪装成 CI 自动验证。 |

旧 UWP 安装、授权失败、Gaming Services 故障、游戏占用、ZIP 极端输入和云同步冲突尚未实测。P2 需以两个合法可获得且受支持的版本或实例实证隔离与切换，不能达成时输出限制，不操作受保护文件。

## 7. 实施顺序与接受条件

1. 完成平台最小验证并冻结受支持的 Windows 版本、包身份和入口；独立验证 GDK 与 UWP。不能验证的类型显示未知或不支持。
2. 实现安装模型、平台查询和替身测试；提供独立页面，修正导航枚举与子控件下标的映射；完成 Debug/Release 构建与 Java 回归。
3. 实现启动适配器和状态机；用真实正版安装验证请求、错误及进程观察，保留系统授权流程。
4. 实现包解析、安全预览和经验证的系统导入；经目标身份和导入行为验证后才进入对外 P0。
5. P1 单独实现数据根、事务、备份与恢复，再接入 UI；P2/P3 不阻塞 P0/P1。

许可证仍以 `LICENCE` 引用的完整《PCL 分发有限许可》及各依赖实际许可证为准；第一阶段未能读取该外部许可全文，发布和移植第三方实现前需完成核对。架构优先使用 Windows 官方接口与现有依赖，BedrockBoot、BMCBL 和 LeviLauncher 的 GPL 代码不直接复制。
