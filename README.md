# RA3BattleNet Updater

文件级增量更新系统：**服务端只产出静态文件树**（可整体交给 CDN），客户端按内容推导地址，
只下载变化的部分；拿不到增量时干净回落完整下载。

设计需求与规范见 **[AGENT.md](./AGENT.md)**（本地文档，不入库）。
本文档描述**当前实现**。

---

## 目录结构

```
Updater_Csharp/
├── Ra3.BattleNet.Updater.Share/                    协议模型、哈希、外部工具封装
├── Ra3.BattleNet.Updater.Core/                     更新引擎（无 UI、无产品耦合）
├── Ra3.BattleNet.Updater.Server/                   清单生成 + 生成期自检 + 补丁生成
├── Ra3.BattleNet.Updater.Client/                   离线补丁包链路的客户端逻辑（历史资产）
├── Ra3.BattleNet.Updater.XmlGenerator/             壳：生成 manifest.xml（含自检报告）
├── Ra3.BattleNet.Updater.Server.PatchGenerator/    壳：生成 patches/ 与 files/
├── Ra3.BattleNet.Updater.Client.Update/            壳：独立进程更新入口
├── Ra3.BattleNet.Updater.Server.CLI/               壳：离线补丁包（一次性 A→B 对比）
├── Ra3.BattleNet.Updater.Client.CLI/               壳：应用离线补丁包
├── Ra3.BattleNet.Updater.Tests/                    单元 + 端到端测试
└── simple_test_web_server/                         本地测试用静态服务器
```

约定：**核心逻辑 = 无后缀的库项目**，**可执行壳 = 带后缀、按角色命名**。

---

## 协议

| 资源 | 地址 |
|---|---|
| 清单 | `{BaseUrl}/manifest.xml` |
| 完整文件 | `{BaseUrl}/files/{md5}` |
| 补丁 | `{BaseUrl}/patches/{oldHash}_{newHash}.hdiff` |

- **没有索引文件**。客户端用「本地文件的内容哈希」与「远端清单里的目标哈希」直接推出地址。
- 三者的 URL 都是**内容寻址、不可变**的，因此可以长缓存、可续传、可无脑重试。
- 清单本身的内容就是版本身份：与本地保存的那一份比对即可判断是否需要更新。

客户端逐文件决策：

```
目标路径已有且哈希一致            → skip
按 UUID 定位到前身、内容一致      → move（改写/移动，0 下载）
按 UUID 定位到前身、内容不同      → GET patches/{old}_{new}.hdiff
                                    200 → 打补丁 → 校验；404 或失败 → 回落完整下载
本地没有可用前身                  → GET files/{md5}
```

> 路径与内容**同时**变化的文件，生成器无法自动判定为同一文件：
> 需要在生成的清单上把新条目的 `<UUID>` 改成旧条目的。
> `XmlGenerator` 的自检报告会直接给出这条操作指令。

---

## 本地跑一遍

```powershell
# 1) 生成两版清单（第二版以第一版为基线，继承 UUID）
dotnet run --project Updater_Csharp/Ra3.BattleNet.Updater.XmlGenerator -- `
  --target-dir <旧版本目录> --new-xmloutputpath old.xml
dotnet run --project Updater_Csharp/Ra3.BattleNet.Updater.XmlGenerator -- `
  --target-dir <新版本目录> --old-xmlpath old.xml --old-root <旧版本目录> --new-xmloutputpath new.xml

# 2) 生成服务端静态树（files/ 与 patches/）
dotnet run --project Updater_Csharp/Ra3.BattleNet.Updater.Server.PatchGenerator -- `
  --manifest new.xml --manifest-root <新版本目录> `
  --baseline old.xml --baseline-root <旧版本目录> --output <服务端目录>
copy new.xml <服务端目录>\manifest.xml

# 3) 起本地静态服务器
python Updater_Csharp/simple_test_web_server/file_server.py --port 23456 --dir <服务端目录>

# 4) 跑一次更新（把 old 的内容拷到 client 目录，并放入 old.xml 作为本地清单）
dotnet run --project Updater_Csharp/Ra3.BattleNet.Updater.Client.Update -- `
  --root <client目录> --manifest-url http://127.0.0.1:23456/manifest.xml
```

退出码：`0` 已最新或已更新 ｜ `1` 需要完整包 / 失败 ｜ `2` 参数或配置错误。
加 `--json` 只输出一行结构化结果。

---

## 测试

```powershell
# 单元 + 端到端（含本地 HTTP 服务器）
dotnet run --project Updater_Csharp/Ra3.BattleNet.Updater.Tests -c Release

# 真实历史版本端到端（重活，默认跳过）
$env:UPDATER_E2E_TREES = "<三个历史版本解包目录的父目录>"
$env:UPDATER_TEST_TMP   = "<空间充足的临时盘>"
dotnet run --project Updater_Csharp/Ra3.BattleNet.Updater.Tests -c Release RealVersions
```

> 该测试项目自带一个反射跑器（`Program.cs`）。原因：受限环境下 VSTest 的 testhost
> 会因 `Process.GetProcessHandle` 被拒而崩溃。测试本身是标准 xUnit `[Fact]`，
> 正常机器上 `dotnet test Ra3.BattleNet.sln` 照常可用。

---

## 部署要求

- `manifest.xml` 用短缓存 + 条件请求（`ETag` / `If-None-Match`，命中即 304 空响应）。
- `files/`、`patches/` 是内容寻址的不可变资源，用长缓存。
- 开启 gzip / Brotli（清单 1.3 MB → 约 185 KB）。
- 完整文件建议预压缩传输；**补丁本身已压缩，不要再压**。
- 外部工具 `hdiffz` / `hpatchz` 必须随产物部署（Share 项目会把 `hdiffpatch_bin` 复制到输出目录）。
