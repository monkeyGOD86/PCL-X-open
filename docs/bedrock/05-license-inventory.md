# 许可与发布前核对

更新：2026-10-27。此记录是工程清点，**不是法律意见，也不构成发布授权**。本次工作没有从 BedrockBoot、BMCBL 或 LeviLauncher 复制代码；Windows 接口和 Minecraft 文件格式按公开文档独立实现。

## 上游项目

- 本检出根目录 `LICENCE` 将《PCL 分发有限许可》全文指向 [外部文档](https://shimo.im/docs/rGrd8pY8xWkt6ryW)，并附带明确自称不是法律文件或协议的《PCL 存储库合理使用指南》。此次尝试读取外部正文时得到动态 HTML，但未取得可核对的正文；不能把它视为已核验的当前条款。
- 本地 Git 历史中的提交 `fd7b722346523d9574678a8a4a02928d31cd1e0c`（2026-07-11）曾在 `LICENCE` 内保存有限许可文本；其列出的分发权利针对原样副本。下一次许可相关提交 `c7ccdd6` 将正文外链。历史文本只能证明当时仓库内容，不能证明外链当前内容或修改版分发权限。
- 当前随仓库附带的合理使用指南把修改仓库列为重度使用的示例，列出名称、署名、公开源码及其他要求；它自身对法律性质作了限制说明。因此不能仅凭仓库公开、指南或历史文本断言当前修改版已取得完整分发许可。正式发布前应获取并保存适用的完整条款，必要时向权利人确认修改及分发授权，并核对实际界面、署名、名称和源码发布要求。

## 本次 Bedrock 代码直接使用的既有依赖

| 组件 | 证据与范围 | 发布前动作 |
|---|---|---|
| MeloongCore | 子模块固定在 `4c60c2962c3b50bc3fe96ed5dd9259b980173580`；该提交的 [LICENSE.txt](https://github.com/Meloong-Git/MeloongCore/blob/4c60c2962c3b50bc3fe96ed5dd9259b980173580/LICENSE.txt) 为 Apache-2.0。 | 核对实际分发文件、许可副本和适用的 NOTICE / 修改声明要求；当前子模块检出未发现单独 NOTICE 文件。 |
| Newtonsoft.Json | 现有 `Resources/Newtonsoft.Json.dll` 的文件版本为 `13.0.3.27908`；对应上游 [13.0.3 许可证](https://github.com/JamesNK/Newtonsoft.Json/blob/13.0.3/LICENSE.md) 为 MIT。版本元数据本身不证明二进制与上游产物逐字一致。 | 确认二进制来源、完整性和发布包中的版权及许可声明。 |
| PolySharp | `PCLCS.csproj` 固定 `1.15.0`，本地 NuGet 元数据标示 MIT、开发依赖及上游提交 `02753db24d9685e54db06739eb63183d86eb5b62`；对应 [许可证](https://github.com/Sergio0694/PolySharp/blob/02753db24d9685e54db06739eb63183d86eb5b62/LICENSE)。 | 核对生成代码与分发物是否需要携带其版权和许可文本；`PrivateAssets=all` 不应被当作许可证豁免。 |
| .NET Framework / Windows API | 使用框架的压缩、加密和文件 API，以及操作系统包枚举和激活接口；未新增第三方基岩版启动或认证库。 | 按实际分发方式核对框架运行时及相关微软条款，不假定系统 API 文档授予 Minecraft 游戏文件的再分发权。 |

此表只覆盖 Bedrock 新代码直接使用的部分，**不是整个启动器及其所有 DLL、JAR、资源、构建依赖的完整物料清单**。发布前还需完成全量清点、生成第三方声明并核对来源。不得因某个第三方组件采用宽松许可证，就推断整个 PCL 修改版可按该许可证分发。
