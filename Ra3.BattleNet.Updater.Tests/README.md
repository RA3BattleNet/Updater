# Ra3.BattleNet.Updater.Tests —— 测试

## 怎么跑

```powershell
# 测试临时目录：**必须是 NTFS**（落地语义依赖共享模式 / 只读属性 / 长路径）
$env:UPDATER_TEST_TMP = 'H:\TEST\upd-tests'

dotnet run -c Release                                        # 全套（本机实测约 40 秒 / 157 条）
dotnet run -c Release Staged                                 # 只跑名字含 "Staged" 的
dotnet run -c Release StagedApplierTests.Apply_LandsTheStaged # 精确到方法
```

> 上面那个 40 秒是**不带**两个重活时的实测（2026-10-01）。开了 `UPDATER_SIM_OUT` 跑版本矩阵是分钟级
> （真 HTTP + 真 hdiffz + 上百 MB 的树），别拿这个数字去估它。

- 筛选参数是**子串匹配**（`Type.Method` 的子串），不是通配符。
- **两个可选的重活**（默认跳过，需要环境变量）：
  - 真实历史版本端到端：`UPDATER_E2E_TREES=<三个历史版本解包目录的父目录>`
  - 版本矩阵模拟（22 个场景 = 5 版本链上的 S01–S22）：`UPDATER_SIM_OUT=<输出目录>`，并需要该目录下已有
    `server-summary.json`（由 `_sim/deploy.ps1` 产出）；结果与日志落在该目录的 `logs/<场景>/`

## 为什么自带一个反射跑器（而不是 xUnit runner）

沙箱/受限环境里 VSTest 的 testhost 起不来，所以 `Program.cs` 自己反射发现并执行测试。
由此带来两条**限制**（写新测试时注意）：

- **只发现 `[Fact]`**，`[Theory]` 不会被执行 —— 表驱动用例请写成一个 `[Fact]` 里循环断言。
- 每个测试是独立的 `[Fact]`，没有 fixture / 生命周期钩子；需要夹具就自己写辅助方法。

## 里面有什么

| 文件 | 覆盖什么 |
|---|---|
| `TestHttpServer.cs` | 测试用的静态服务器：**支持 ETag / If-None-Match / Range / gzip** —— 304 与续传靠它 |
| `TestSupport.cs` | `TempDir`（尊重 `UPDATER_TEST_TMP`）、`TestCacheDir`、建树/哈希等夹具 |
| `UpdatePlannerTests` / `PipelineTests` / `ResilienceTests` | 计划与主流程、失败与重试 |
| `ManifestFormatTests` / `ManifestGenerationTests` / `PatchGenerationTests` | 清单往返、UUID 继承与自动关联、补丁生成 |
| `EndToEndTests` / `FaultInjectionTests` / `LargeFileTests` / `LongPathTests` | 端到端、故障注入、>2 GB、超长路径 |
| `StagedApplyTests` / `StagedUpdateTests` / `StagedApplierTests` | 暂存模式：状态判定纯函数、阶段一、落地（含"中断后收敛""备份回滚""等宿主退出"**"零网络""不写 ETag"**） |
| `BaselineAdoptionTests` | 没有可信基线时按磁盘哈希核对（含"保险丝看到的是真实工作量"的正反两条） |
| `ManifestFreshnessTests` | 缓存比树新 / 命中 304 也要自愈；`--verify-unchanged` 真的会逐文件核对 |
| `PathSafetyTests` | 路径信任边界（逃逸向量表） |
| `WireAccountingTests` / `LogFormatTests` | 网线字节口径、日志格式契约 |
| `VersionMatrixSimulation.cs` | 真实 5 版本的 22 个场景（重活，见上） |

## 约定

- **断言必须真的会失败**（不要写只会打印 PASS 的检查）。
- 涉及落地语义的测试**必须跑在 NTFS 上**；非 NTFS 会给出相反结论。
- 动完代码后：**干净 clone 构建** + 全套（见 [`../USAGE.md`](../USAGE.md) §6.1）。