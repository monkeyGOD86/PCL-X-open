# 基岩版资源包布局验证工具

本工具用于验证游戏是否识别实验性安装布局，**不代表附加包安装功能已经受支持**。测试包由工具生成，不含游戏文件；它只包含一个清单和少量测试文本。仅验证当前 Windows 用户通过 Microsoft Store/Xbox 注册的游戏。`plan` 为只读预检查，`install` 和 `rollback` 会修改明确选定的数据根，并需要在控制台输入 `YES`。

## 准备和只读检查

在仓库根目录运行：

```powershell
dotnet run --project .\tools\bedrock-probe\PackProbe\PackProbe.csproj -c Release -- list
dotnet run --project .\tools\bedrock-probe\PackProbe\PackProbe.csproj -c Release -- fixture
```

`list` 显示已注册包族与候选数据根；候选目录不等于当前游戏账户已确认使用该目录。`fixture` 在系统临时目录创建独立测试包并打印路径和 UUID，不写游戏数据。对用户确认的目标运行以下预检查（将尖括号内容替换为 `list` 和 `fixture` 的输出）：

```powershell
dotnet run --project .\tools\bedrock-probe\PackProbe\PackProbe.csproj -c Release -- plan '<包族>' '<目标数据根>' '<测试包目录>'
```

`plan` 检查候选目录、所提供安装的全部发现目录中的 UUID 冲突及目标 `resource_packs` 是否存在。未知包、不可读取目录或冲突会阻止测试。它无法发现所有可能的数据目录，也无法确认游戏授权、同步状态或游戏能否识别包。预检查不会在游戏目录创建事务或包。

## 经确认后的验证及恢复

只有在用户明确确认游戏安装、目标数据根、关闭游戏并等待同步完成后，才运行：

```powershell
dotnet run --project .\tools\bedrock-probe\PackProbe\PackProbe.csproj -c Release -- install '<包族>' '<目标数据根>' '<测试包目录>'
```

工具再次显示目标、UUID、指纹和检查范围；输入 `YES` 后才创建事务和新包目录。它会检查常见游戏进程，但不能证明其他进程或云同步已停止。成功只表示文件已移动；随后由用户手动启动游戏，检查资源包列表是否识别 **PCL Compatibility Test**。保留工具打印的事务目录。测试不需要启用资源包或修改任何世界。

完成观察后再次关闭游戏、等待同步，并运行：

```powershell
dotnet run --project .\tools\bedrock-probe\PackProbe\PackProbe.csproj -c Release -- rollback '<包族>' '<目标数据根>' '<事务目录>'
```

恢复前再次输入 `YES`。恢复将未修改的测试包移回事务目录，不覆盖原文件也不删除事务或测试包。若游戏或其他程序修改了包，自动恢复会拒绝移动，必须保留事务并人工检查。中断、失败或磁盘问题可能留下暂存与事务记录；不要手动删除不明内容。当前日志和暂存位于选定游戏数据根，尚需评估迁移到独立目录的方案。
