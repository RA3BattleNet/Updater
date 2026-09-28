# Ra3.BattleNet.Updater.Server —— 服务端发布逻辑

发布流水线的**库**（无 UI、无产品耦合）。两个可执行壳
（`XmlGenerator` / `Server.PatchGenerator`）只是参数解析与打印，业务逻辑都在这里。

| 文件 | 干什么 |
|---|---|
| `ManifestGenerator.cs` | 扫描目录生成清单：UUID 继承（同名/同内容/路径派生/自动关联）、生成期自检、报告文本 |
| `PatchGenerator.cs` | 生成**纯静态**服务端树：完整文件落 `files/{md5}.bin`，内容对补丁落 `patches/{old}_{new}.bin`（old 在前），并校验"补丁确实能把旧文件还原成新文件" |

## 发布侧的三条口径

1. **输出永远是静态文件树**，不含任何索引文件 —— URL 可由哈希直接推导，客户端不需要索引。
2. **保留窗口 N**：对最近 N 个基线版本生成补丁（当前流水线用 N=3）；窗口外的旧版本自然回落完整下载。
3. **补丁不小于目标文件就弃用**：生成后比较大小，无用的补丁不进库（客户端随后 404 → 完整下载）。

## 用法与参数

两个壳的参数与取舍细节在它们自己的 README 里：
[`../Ra3.BattleNet.Updater.XmlGenerator/README.md`](../Ra3.BattleNet.Updater.XmlGenerator/README.md)、
[`../Ra3.BattleNet.Updater.Server.PatchGenerator/README.md`](../Ra3.BattleNet.Updater.Server.PatchGenerator/README.md)。
流水线的完整命令示例见 [`../USAGE.md`](../USAGE.md) §3。

## 注意

- **链式生成**：第 N 版清单要以第 N-1 版为基线（`--old-xmlpath/--old-root`），否则 UUID 链断裂 →
  补丁全部落空且**不报错**。这是发布侧最容易出的错。
- 补丁命名顺序反了（`{new}_{old}`）同样"永远不命中、永远完整下载"，不会报错 —— 规范与测试各钉了一次。