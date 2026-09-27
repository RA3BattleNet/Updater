# RA3BattleNet Updater

文件级**增量更新**系统（C# / .NET 10）。服务端只产出**静态文件树**（可整体交给 CDN），
客户端按内容寻址、只下载变化的部分；拿不到增量时干净回落完整下载。

## 关键设计

- **内容寻址、无索引**：地址由哈希直接推导 —— `files/{md5}.bin`、`patches/{old}_{new}.bin`。
- **清单即版本身份**：`manifest.xml` 的字节与本地保存的那份比对，一次请求就能判断"要不要更新"（命中 304 时 0 字节）。
- **文件身份 = UUID**：路径变了也认得出是同一个文件，于是改名也能打补丁、甚至 0 下载。
- **失败不致命**：补丁缺失/打不上 → 静默回落完整下载；失败到阈值 → 交回宿主走整包；库不抛异常、不弹 UI。
- **可观测**：每轮写 `update.log`（S/F/R 三种行），结构化结果里给内容字节、payload 与**真正上网的字节（wire）**。

## 仓库结构

| 路径 | 内容 |
|---|---|
| `Updater_Csharp/Ra3.BattleNet.Updater.Core` | 更新引擎（客户端只需引用它） |
| `Updater_Csharp/Ra3.BattleNet.Updater.Server` | 清单生成 + 生成期自检 + 补丁生成 |
| `Updater_Csharp/Ra3.BattleNet.Updater.{XmlGenerator,Server.PatchGenerator}` | 发布流水线的两个壳 |
| `Updater_Csharp/Ra3.BattleNet.Updater.Client.Update` | 独立进程更新入口 |
| `Updater_Csharp/Ra3.BattleNet.Updater.Tests` | 单元 + 端到端测试 |
| `Updater_Csharp/Ra3.BattleNet.Updater.{Share,Client,*.CLI}` | 协议模型 / 离线补丁包链路（历史资产） |

## 文档

- **[Updater_Csharp/README.md](./Updater_Csharp/README.md)** —— 使用说明（怎么引用、怎么调、返回什么、怎么处理、怎么发布）。
- `Updater_Csharp/*/README.md` —— 各子项目参数与取舍细节。
- `AGENT.md` —— 设计规范（甲方要求，**本地文档、不入库**）。

## 快速开始

```powershell
dotnet build Updater_Csharp/Ra3.BattleNet.sln -c Release
dotnet run --project Updater_Csharp/Ra3.BattleNet.Updater.Tests -c Release   # 跑测试
```

完整的两版生成 → 发布 → 客户端更新的示例命令见 `Updater_Csharp/README.md` §5。
