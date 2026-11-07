# 小队整合包同步开发指南

日期：2026-11-04。面向几位朋友共同游玩的 Java 版整合包。本文件是开发方案，所列新增类、页面和文件格式扩展尚未实现；现有代码入口已按当前工作区核对。

## 1. 第一版要做成什么

维护者在自己的实例中整理好 Mod 和共享配置，导出一个小队 `.mrpack` 文件，通过群文件或网盘发给成员。成员选择这个文件，看到新增、替换、移除及本地修改，确认后更新自己的实例。

第一版采用**本地文件分享、单向更新、人工确认差异、保留一次撤销**。小团体靠维护者发文件即可，不需要账号系统、后台服务、实时同步或自动更新服务器。

### 范围约定

| 项目 | 第一版决定 |
| --- | --- |
| 同步对象 | 已启用的普通 Mod JAR，以及维护者勾选的共享配置、脚本 |
| 游戏环境 | 支持项目已有的 Forge、NeoForge、Fabric 实例；原地同步要求 Minecraft 和加载器种类、版本一致 |
| 环境升级 | Minecraft 或加载器版本变化时，调用现有整合包导入流程新建实例 |
| 实例隔离 | 只对独立实例操作；确认 `PathIndie` 与 `PathVersion` 指向同一目录 |
| 传输 | 发送完整 `.mrpack` 描述包；成员只下载或解压本次需要的文件，不做二进制差分 |
| 个人内容 | 本地额外 Mod 默认保留，提供明确的移出选项；个人设置和存档不纳入同步 |
| 配置冲突 | 提供“保留我的文件 / 使用团队文件”；第一版不做配置文件内容合并 |
| 恢复 | 保存最近一次成功同步的文件备份和旧状态，支持撤销；失败任务材料单独保留 |

“团队文件已对齐”仅表示文件和环境检查结果，不等同于保证所有个人附加 Mod 都能联机。

## 2. 用户流程与界面

### 维护者

1. 在现有“导出整合包”页面勾选新增的“用于小队同步”。第一次生成小队标识，以后导出沿用同一标识。
2. 默认选择已启用的普通 Mod；逐项选择要统一的配置文件。提供“沿用上次选择”。
3. 填写版本名，例如 `周末生存团 1.2`，导出 `.mrpack`，发到群里。

小队模式固定输出单层 `.mrpack`，关闭“包含启动器、Java、存档”等选项；游戏与加载器通过标准依赖字段描述，不将普通导出模式的所有默认选项直接带入。

从维护者的客户端实例导出，不直接扫描服务端目录。客户端成员使用同一份基础环境，个人附加项由成员自行选择。

### 成员

- **首次加入、没有实例：** 使用现有导入功能安装到新实例；安装完成后进入“小队同步”，用同一包核对实际文件并建立绑定。
- **首次加入、已有实例：** 在该实例的“小队同步”页选择包，核对游戏环境，确认首次绑定。首次接入没有旧基线，任何同路径不同内容的本地文件都需要处理。
- **后续更新：** 选择新包 → 检查小队标识和环境 → 展示差异 → 选择冲突处理方式 → 应用 → 显示结果和撤销入口。

### 页面只保留三个区域

| 区域 | 展示与操作 |
| --- | --- |
| 顶部 | 小队名称、上次处理的版本、本次版本、“选择整合包” |
| 中部 | 新增、替换、移除、冲突、本地附加项；列出文件名、变化原因和处理选择 |
| 底部 | 待下载大小、处理进度、“应用更新”“撤销上次同步”“打开备份文件夹” |

结果至少区分：`团队文件已对齐`、`团队文件已对齐，保留 N 个本地附加项`、`存在 N 项本地差异`、`同步未完成`。取消必需文件的修改后，不能只显示“已更新至 1.2”。

## 3. 复用当前项目的哪些代码

| 现有入口 | 本功能如何使用 |
| --- | --- |
| [PageInstanceExport.xaml.vb](../../Plain%20Craft%20Launcher%202/Pages/PageInstance/PageInstanceExport.xaml.vb) 的 `StartExport` | 复用文件选择、平台文件识别和 `.mrpack` 输出；增加小队导出模式，约束导出范围并写入小队元数据 |
| [ModModpack.vb](../../Plain%20Craft%20Launcher%202/Modules/Minecraft/ModModpack.vb) 的 `ModpackInstall` / `InstallPackModrinth` | 新成员安装及环境版本变化时新建实例；参考其中的包解析与下载实现 |
| [ModNet.vb](../../Plain%20Craft%20Launcher%202/Modules/Base/ModNet.vb) 的 `NetFile` / `LoaderDownload` | 下载指定版本的文件到暂存区，接入已有进度和重试 |
| [ModLoader.vb](../../Plain%20Craft%20Launcher%202/Modules/Base/ModLoader.vb) | 用 `LoaderTask` / `LoaderCombo` 编排扫描、准备与应用，并显示在任务列表中 |
| [ModMinecraft.vb](../../Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb) 的 `McInstance` / `McInstanceLock` | 获取真实实例目录、环境信息和操作锁 |
| [ModWatcher.vb](../../Plain%20Craft%20Launcher%202/Modules/Minecraft/ModWatcher.vb) 的 `McWatcherList` | 检查启动器已跟踪的目标游戏进程是否仍在运行 |
| [LocalResourceFile.vb](../../Plain%20Craft%20Launcher%202/Modules/Resource/LocalResourceFile.vb) | 复用本地资源信息、显示名称和禁用状态识别 |
| [McInstallManifest.cs](../../PCLCS/Minecraft/McInstallManifest.cs) | 读取已有安装组件与受管理文件信息，识别安装器维护的组件文件 |
| [CryptographyUtils.cs](../../MeloongCore/Shared/Utils/CryptographyUtils.cs) / [FileUtils.cs](../../MeloongCore/Shared/Utils/FileUtils.cs) | 文件哈希、文件检查与基础文件操作 |

现有 `InstallPackModrinth` 会复制 overrides 并组织游戏安装，`McInstallCommit` 会替换已有版本文件。因此，**更新已有实例必须走新增的差异应用流程**，不要直接把现有“新建安装”函数指向旧实例目录。

`McInstallManifest` 记录的是安装器管理的信息，不能用作“上次小队发布了什么”的完整基线。小队状态单独保存。

## 4. 包格式与小队身份

### 继续使用 `.mrpack`

现有导出已经生成 `modrinth.index.json`，包含精确下载地址、文件哈希和游戏/加载器依赖。沿用这一格式：客户端目标由文件清单与覆盖目录合成，`client-overrides` 覆盖通用 `overrides`，忽略服务端覆盖目录；可选文件让成员选择。格式依据：[Modrinth 官方规范](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack)。

小队包增加一个由本项目定义的根目录文件 `pcl-group.json`：

```json
{
  "schemaVersion": 1,
  "packId": "d04fbff2-70b7-474d-9333-ec75e760a77a"
}
```

`packId` 是随机生成并持续复用的 UUID。显示名称和版本名继续读取标准索引中的 `name`、`versionId`。不要靠包名、文件名或版本名判断是否属于同一小队，也不要因为版本名相同就跳过内容比较。

```text
weekend-1.2.mrpack
├─ modrinth.index.json
├─ pcl-group.json            本项目的小队身份扩展，不复制到游戏目录
└─ overrides/
   └─ config/
      └─ shared-example.toml
```

这个附加文件是本功能的约定，不属于 Modrinth 标准。第一版只接受带该标识的包进行小队同步；普通 `.mrpack` 仍按现有方式导入。维护者的标识存入其本地 `publisher.json`，重复导出沿用。复制实例给另一小队时，通过“新建小队”生成新标识；成员接手同一小队的维护时，可明确选择沿用已绑定的标识。

### 文件范围

同步白名单限定为 `mods/*.jar`、`config/**`、`defaultconfigs/**`、`kubejs/**`、`scripts/**`。除普通 Mod 外，其余文件由维护者勾选；小队导出模式复用导出能力，但应用自己的文件选择规则。

`saves/`、`screenshots/`、`logs/`、`crash-reports/`、`options.txt`、`optionsof.txt`、`servers.dat`、`PCL/`、账号信息、Java 路径和启动器设置均不参与同步。`resourcepacks/`、`shaderpacks/` 可放到后续版本。导入同步包时同样执行范围检查，不能只依赖导出端筛选；发现范围外载荷就给出文件名并停止本次同步。

已由安装器登记为组件的文件，通过 `McInstallManifest.ManagedFiles` 中非空 `Component` 标记识别。第一版可显示其差异，但涉及这类文件的增删改时走“重新导入新实例”，避免小队同步破坏安装组件记录。可把对此类文件的原地更新作为后续增强。

## 5. 本地状态怎么存

状态目录固定为 `Instance.PathVersion/PCL/PackSync/`，并且不能由包内路径指定。

```text
PCL/PackSync/
├─ publisher.json          维护者的小队标识和导出选择
├─ state.json              成员绑定、上次团队基线和可选项选择
├─ staging/<操作ID>/       经过校验的本次新文件
└─ transactions/<操作ID>/
   ├─ journal.json         计划、前后哈希与处理状态
   ├─ previous-state.json  操作前的本地状态；首次绑定记录“原本不存在”
   └─ backup/              本次被替换、移出或删除的原文件
```

`state.json` 至少保存以下字段：

| 字段 | 用途 |
| --- | --- |
| `schemaVersion`、`packId` | 状态格式和绑定身份 |
| `releaseId`、`archiveSha512` | 本次处理的版本名及包文件哈希；版本名不用于推断文件一致性 |
| `dependencies` | 该团队版本的 Minecraft 与加载器要求 |
| `baselineFiles` | 上次处理后，各必需文件与已选可选文件的目标路径和 SHA-512 |
| `optionalSelections` | 成员对包内可选文件的选择 |
| `lastTransactionId` | 最近一次成功操作的恢复记录 |

基线记录**团队要求的内容**。成员选择保留自己修改过的配置时，不能把本地修改后的哈希当成团队哈希，否则下次无法判断偏离。界面上的一致性状态通过实际扫描计算。

首次绑定以空基线开始，完整应用或处理完选择后才写状态。后续 `packId` 不一致时停止，提供新建实例入口。没有 `.mrpack` 原文件也应能撤销上次操作。

## 6. 差异计算：同时比较三份信息

以规范化的相对路径作为键，用内容哈希判断变化：

- **B：** 上次团队基线。
- **L：** 当前本地文件。
- **T：** 本次包要求的目标文件。

“不存在”也是一种明确状态。按表从上到下判断，每个路径只产生一个主要动作。

| 条件 | 展示 | 默认处理 |
| --- | --- | --- |
| `L = T`，包括两边都不存在 | 无变化 | 不操作 |
| 无 B，有 T，无 L | 缺少团队文件 | 新增 |
| 无 B，有 T，有 L，且 `L ≠ T` | 首次接入遇到本地文件 | 冲突，等待选择 |
| 有 B、有 T，且 `L = B`、`L ≠ T` | 团队更新了文件 | 替换并备份 |
| 有 B、有 T，且 `L ≠ B`、`L ≠ T` | 本地也修改或删除过 | 冲突，默认保留本地，等待成员确认 |
| 有 B、无 T，且 `L = B` | 团队移除了文件 | 移到备份 |
| 有 B、无 T，有 L，且 `L ≠ B` | 团队移除，但本地有修改 | 冲突，等待选择 |
| 无 B、无 T，有 L | 本地附加文件 | 保留；额外 Mod 可由成员明确选择移到备份 |

采用以上规则后，正常更新中 Mod 改名会表现为“移出旧路径 + 新增新路径”，两步属于同一操作。

首次接入时，没有 B 的旧版 JAR 会归为本地附加项。页面必须把它列出来，要求成员确认保留或移出；不能根据相似文件名直接推断为旧版本并删除。目标 Mod 在本地处于禁用状态时，也要明确显示，复用 `LocalResourceFile` 的状态判断。

可选文件的选择先确定，再生成 T。由“选中”改为“不选”时，之前管理的文件按照表中的移除规则处理；未选的额外本地文件仍然保留。保留本地冲突或附加项后，相应差异要出现在结果中。

## 7. 应用更新的流程

```mermaid
flowchart LR
    A[选择小队包] --> B[校验身份、环境与文件范围]
    B --> C[扫描本地并展示差异]
    C --> D[确认动作并锁定实例]
    D --> E[暂存全部新文件并校验]
    E --> F[复查本地文件]
    F --> G[备份并应用文件变更]
    G --> H[核对结果并保存状态]
    G -->|失败| R[按记录恢复原文件]
```

1. **只读预览。** 读取包并计算包哈希，检查小队身份、环境、隔离目录和文件范围。合并包中的有效客户端文件，生成确定的目标清单。
2. **下载准备。** 成员确认后进入工作任务，取得实例操作锁。只把需要新增/替换的内容下载或解压到 staging，沿用清单中的精确 URL，使用 SHA-512 校验；覆盖文件的哈希从包内实际内容计算。所有文件准备完成前，不修改目标文件。
3. **再次确认现场。** 检查包哈希、选定实例、操作计划和本地文件是否仍与预览一致。发现预览后变化就返回差异页；确认期间被用户新建的目标文件不能被直接覆盖。
4. **记录和备份。** 先持久化操作清单与旧状态，再备份所有将被替换或移出的文件；新增文件也记录“原本不存在”。备份完整后才开始修改。
5. **应用和核对。** 按记录应用变更，核对目标文件哈希与应移出的路径。失败时使用当前事务恢复，保持旧基线；成功后通过临时文件替换方式保存新 `state.json`，最后标记事务完成。
6. **刷新页面。** 重读实例和 Mod 列表，刷新资源缓存及同步结果。保留本次恢复点；新的操作完全成功后，才清理更早的成功恢复点。

取消在下载/准备阶段立即结束；进入文件修改阶段后，转为完成恢复再退出。进度文案分别显示“准备文件”“应用更新”“恢复原文件”，避免界面停在没有说明的忙碌状态。

### 撤销与中断

撤销先核对文件仍等于上次操作完成时的内容，再删除本次新增文件、恢复原文件和旧状态。文件被游戏或成员再次修改时，停止自动覆盖，保留两个版本并显示相关路径。

第一版仅支持撤销最近一次成功操作。撤销后恢复旧基线及可选项，并清空 `lastTransactionId`，避免指向已经清理的更早备份。恢复遇到文件占用或内容再次变化时，保留事务并显示“恢复未完成”，不要清理其材料或报告成功。

每个事务保留各路径的变更前/后哈希、原始是否存在及步骤状态。启动时遇到未完成事务，先展示恢复入口；恢复时结合实际哈希判定已完成到哪一步，不能只依赖最后写入的进度标记。撤销和中断恢复也使用相同的实例锁与游戏运行检查。

这部分只实现一个本地文件操作记录和最近一次撤销，不扩展为通用版本管理系统。

## 8. 代码组织与接入清单

新增文件建议如下。路径表示计划，当前尚不存在。

| 计划文件 | 职责 |
| --- | --- |
| `PCLCS/PackSync/PackSyncModels.cs` | 包信息、基线、差异项、动作、冲突选择及事务记录模型 |
| `PCLCS/PackSync/PackSyncReader.cs` | 解析标准索引和小队元数据、检查路径、合成客户端目标清单 |
| `PCLCS/PackSync/PackSyncPlanner.cs` | 扫描本地、计算 B/L/T 差异、构造确认后的操作计划 |
| `PCLCS/PackSync/PackSyncStore.cs` | 状态、暂存、备份、应用与恢复；体积变大后再拆分 |
| `Plain Craft Launcher 2/Modules/Minecraft/ModPackSync.vb` | VB 与 C# 的连接层，组织 Loader、下载、实例锁和界面刷新 |
| `Plain Craft Launcher 2/Pages/PageInstance/PageInstanceSync.xaml` 及 `.xaml.vb` | 同步页面、冲突选择与进度 |

C# 层接收明确的目录、环境信息和普通数据对象，不直接引用 VB 的 `McInstance`、`LoaderBase` 或页面。`PCLCS` 是被 VB 工程引用的库，反向引用会形成循环。使用现有 `net48`、Newtonsoft.Json 和公共文件工具即可。

### 接入现有页面

- [PageInstanceLeft.xaml](../../Plain%20Craft%20Launcher%202/Pages/PageInstance/PageInstanceLeft.xaml) 增加“小队同步”项。
- [FormMain.xaml.vb](../../Plain%20Craft%20Launcher%202/FormMain.xaml.vb) 的 `PageSubType` 增加显式值 `InstanceSync = 6`，保留现有实例子页面 `0–5` 的值及所有 `PageType` 值。
- [PageInstanceLeft.xaml.vb](../../Plain%20Craft%20Launcher%202/Pages/PageInstance/PageInstanceLeft.xaml.vb) 补齐 `PageCheck`、`PageGet` 和刷新分支；[ModMain.vb](../../Plain%20Craft%20Launcher%202/Modules/ModMain.vb) 增加页面字段。
- [VB 工程文件](../../Plain%20Craft%20Launcher%202/Plain%20Craft%20Launcher%202.vbproj) 显式登记新 `.vb`、XAML 及对应关系。C# SDK 工程会按现有规则包含普通新增 `.cs` 文件。
- 在 [导出页面](../../Plain%20Craft%20Launcher%202/Pages/PageInstance/PageInstanceExport.xaml.vb) 的 `StartExport` 路径增加小队选项；若提取公共方法，标明它是新接口，不从 C# 调用页面的私有方法。

### 需要处理的几个现有约束

| 约束 | 实现决定 |
| --- | --- |
| `McInstanceLock.TryAcquire` 依赖 `IWorker` 生命周期释放 | 参考 `McModificationStart` 的 `RedoableWorker` 用法，确保异常/取消也结束 Worker；不能只设置“忙碌”标志 |
| 现有 Mod 更新维护 `UpdatingInstanceModFolders` | 同步与 Mod 更新、删除、启用/禁用等修改入口要共享互斥检查，避免同时写同一目录；不要只禁用同步页按钮 |
| 游戏进程可能仍在使用文件 | 检查目标实例的已跟踪进程尚未退出就停止；页面要求成员关闭其他方式启动的该实例游戏 |
| 包路径最终用于本地写入 | 复用路径与解压检查，拒绝越界、绝对路径、重解析点及同一来源层中规范化后重复路径；预览和执行都校验 |
| 同名路径可能来自多个来源层 | 目标清单明确按 `files → overrides → client-overrides` 形成结果；跨层覆盖使用后层内容，只准备最终生效的文件 |
| 本地状态可能缺失或损坏 | 显示未绑定/需要重新核对，以空基线预览，禁止据此猜测并批量移除本地文件 |
| 状态格式版本或游戏/加载器依赖不受支持 | 明确报出不支持项并停止原地修改；无关的扩展说明字段可忽略 |

## 9. 建议按这个顺序开发

| 阶段 | 交付内容 | 完成标准 |
| --- | --- | --- |
| 第一步：导出与只读差异 | 小队导出、稳定 `packId`、包读取、页面、B/L/T 计划 | 用两个小队包能看清新增、更新、移除及本地修改，尚不写游戏文件 |
| 第二步：应用与恢复 | 下载暂存、操作互斥、备份、提交、状态保存、最近一次撤销 | 更新后文件与已确认动作一致，取消/失败能回到操作前 |
| 第三步：接入日常流程 | 首次导入后的核对绑定、可选项记忆、缓存刷新、错误与进度提示 | 维护者发文件，成员按页面提示即可完成更新 |

第一版用人工选择本地包即可。之后确有需要，再加固定下载链接的“检查更新”、资源包同步、组件文件原地更新或更长的历史记录。

### 验证保持简单

按小团体使用场景，不单独规划测试框架、大规模兼容矩阵或测试工程。开发时完成正常编译，并用临时实例手工走三件事：

1. 导出一个小队包，首次导入/绑定，再应用包含新增、替换和移除的第二版。
2. 保留一个个人 Mod，手工改一份共享配置，确认页面能正确处理选择且存档、个人设置不受影响。
3. 在准备阶段取消一次，再验证完成同步后的撤销；恢复按钮应能找回旧文件和旧状态。

构建入口沿用 [现有 CI](../../.github/workflows/build.yml)：

```powershell
dotnet restore .\PCLCS\PCLCS.csproj
msbuild ".\Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj" -p:Configuration=Debug
```

本次只新增开发指南，未实现或执行上述功能。项目其他入口见 [代码索引](../../CODE_INDEX.md)。
