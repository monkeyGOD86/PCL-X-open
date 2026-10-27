# Bedrock 版本和实例管理：技术验证记录

更新：2026-10-27。状态：**调研中，未实现历史版本安装、版本切换或独立实例**。本记录不将 Windows 通用能力视为 Minecraft 零售包已验证的能力。P2 不阻塞 P0/P1。

## 当前结论

1. 可安全展示当前用户已注册的正式版或预览版包及其 Windows 包版本；这不是游戏内显示版本，也不证明购买状态。当前机器只实测了正式版的一次系统启动，尚未实测 Preview、两个版本并存或两个独立数据实例。
2. **不能将复制游戏安装文件、修改受保护目录、直接运行 exe 或重签名作为版本切换方案**。合法取得的历史游戏包、系统注册、Gaming Services、授权以及旧世界数据兼容性都未验证；不能根据一般 MSIX 工具存在推断可部署 Minecraft 零售历史版本。
3. 有条件的替代方案是让用户通过官方渠道安装与选择 Retail / Preview，再由启动器检测并分别请求系统启动。微软的 [Minecraft Editor 安装文档](https://learn.microsoft.com/en-us/minecraft/creator/documents/editorinstallation) 描述了官方启动器安装及选择这两个渠道；这**不证明**本项目已验证它们能同时安装、各自启动、并行运行或隔离所有用户数据。
4. 当前没有经过验证的方式，让一个 Minecraft Windows 零售包按启动参数选择任意数据目录。现有 `BedrockDataRootFinder` 的结果是候选位置，不能当作独立实例隔离能力。世界备份/恢复仅是文件级工具，不能替代游戏和云同步对实例的管理。

## 证据与推论

| 问题 | 已查证资料 | 对本项目的判断 |
|---|---|---|
| 包身份与版本 | Windows 的 [包身份文档](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview) 区分包含版本和架构的包全名，以及由名称和发布者构成的包族名；数据和安全通常以包族为范围。 | **推论**：版本号不同并不自动意味着数据或授权隔离。必须逐一验证实际 Minecraft 安装，不能把包全名映射为独立实例。 |
| 更新和降级 | [MSIX 更新文档](https://learn.microsoft.com/en-us/windows/msix/app-package-updates) 说明同包族更新规则；[部署规划](https://learn.microsoft.com/en-us/windows/msix/desktop/managing-your-msix-deployment-targetdevices) 介绍通用 MSIX 更新、降级和保留 app data 的风险。 | 这些是通用 Windows 包能力，**不是** Minecraft GDK 零售包可合法降级或并存的证明。新版本写入的数据不一定能被旧版本读取。 |
| GDK 部署 | [GDK 开发包测试](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/packaging/packaging-testing-pc-install) 面向已有 MSIXVC 开发构建的开发 PC，依赖 Gaming Services。 | 不把开发者 `wdapp` 流程当作普通玩家获取或部署 Minecraft 历史版本的授权来源。零售包许可、签名、来源和游戏上下文另需验证。 |
| GDK 与 UWP 目录 | Minecraft [GDK 迁移文档](https://learn.microsoft.com/en-us/minecraft/creator/documents/gdkpcprojectfolder) 区分迁移后的共享创作者内容目录与旧 UWP 布局。 | 相同包族名称不能单独判断 GDK/UWP；共享内容目录不等于账户世界目录，更不代表独立实例。 |
| 同步与冲突 | GDK [Game Saves 同步流程](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/game-save/game-saves-syncing) 说明同步、离线和冲突处理；[存档操作说明](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/game-save/game-saves-walkthroughs-and-samples) 提醒直接操作本地数据仍受同步逻辑影响。 | 这些是 GDK 的一般机制，Minecraft 使用哪种机制仍待验证。关闭游戏进程不能单独证明同步完成；不得自动搬移或覆盖用户世界。 |

## 需要独立验证的矩阵

任何会写入游戏数据或改变系统包注册的试验均需用户明确选择目标，并先完成单独备份和恢复演练。不得下载来历不明的二进制或绕过授权。

| 试验 | 只读或受控操作 | 完成证据 | 状态 |
|---|---|---|---|
| Retail / Preview 同时存在 | 在用户选择的测试 Windows 账户中，通过官方渠道安装可用渠道；记录包族、包全名、应用入口及版本。 | 两个注册条目真实存在，各自经系统入口启动，授权和版本由游戏确认。 | 待验证；本机只有 Retail 经过启动验证。 |
| 渠道数据隔离 | 使用两个全新的测试世界及独立外部备份，记录两渠道能读取的目录和游戏内显示；结束后按确认过的恢复流程处理。 | 两边各自修改、退出、同步、重启后不会互相覆盖；记录例外和云同步行为。 | 待验证；不能从目录名推断。 |
| 同渠道多个版本 | 先取得官方许可且合法可用的测试版本来源与部署说明，再在专门测试环境验证，不使用用户日常安装。 | 明确的包注册、授权、切换、启动、升级与恢复证据；若渠道不提供合法来源则记录无法验证。 | 待验证；尚无可确认的历史零售包来源。 |
| 并发启动与隔离 | 只有在上面两个测试成功后，验证同账户或其他受支持账户的并发限制。 | 每个进程、实际用户身份、世界读写和同步结果可追踪；不能仅凭窗口数。 | 待验证。 |
| 世界迁移与回退 | 使用可丢弃的测试世界、游戏内版本标识和游戏外独立备份。 | 前后文件与游戏内状态、跨版本读取结果、异常恢复均有记录。 | 待验证；旧版读取新版世界可能失败或修改数据。 |

## 决策门槛

- 若只能验证 Retail 与 Preview 各自启动，界面只应显示两个已注册的**渠道**，不承诺历史版本或实例隔离。
- 只有在至少两个受支持版本或实例的独立管理、切换和数据恢复通过上述测试后，才将 P2 称为支持；失败时保留限制说明和官方启动器的渠道选择替代方案。
- 原生 Mod、注入以及其他扩展属于 P3，需要独立合法性、兼容性与安全评估，不作为 P0/P1/P2 的默认前提。
