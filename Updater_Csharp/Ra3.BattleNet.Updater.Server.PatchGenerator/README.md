# Ra3.BattleNet.Updater.Server.PatchGenerator

服务端发布流水线的壳：从一个新版本清单 + 若干基线清单，生成**纯静态**的服务端文件树
。业务逻辑在 `Ra3.BattleNet.Updater.Server.PatchGenerator` 库侧
（`PatchGenerator` 类），本壳只解析参数、调库、打印摘要。

## 用法

```
PatchGenerator --manifest <本版xml> --manifest-root <本版目录> --output <输出目录>
               [--baseline <基线xml> --baseline-root <基线目录>]... [选项]

  --manifest / --manifest-root   新版本的清单与其文件根目录
  --baseline / --baseline-root   基线版本清单与其根目录；可重复多组（保留窗口 N 个）
  --output                       输出目录（files/ 与 patches/ 会在此建立）
  --min-size <字节>              生成补丁前的尺寸预过滤（默认 0 = 不过滤；权威规则是尺寸后判断）
  --tools <目录>                 外部工具目录（默认程序目录）
  --prune                        删除不属于当前基线集合的补丁文件
  --no-verify                    跳过「补丁可用性」校验（默认会校验）
  --compress-files               额外生成 files/{md5}.bin.gz 预压缩旁挂（默认不生成，见下）
```

输出形态（内容寻址，URL 可从 `{BaseUrl}` 直接推导，客户端不需要任何索引文件）：

```
{output}/
├── files/{md5}.bin              完整文件（跨版本天然去重）
└── patches/{oldHash}_{newHash}.bin   补丁（内容对寻址，old 在前 new 在后）
```

## 补丁生成参数（**别改错，差 3 倍**）

实际调用的是 `hdiffz`，参数由 `HdiffTool.BuildDiffArgs` 决定：

- **`-m` 还是 `-s`**：`-s` 是"按流匹配（省内存、快）"，**不是压缩**，而且明显比 `-m` 大。
  `-m` 才是最小补丁，但要 `新文件大小 + 旧文件大小 × 5` 的内存。
  所以规则是：**内存放得下用 `-m`，放不下才退回 `-s`**（大文件如 ISO 会自然走 `-s`）。
- **`-c-lzma`**：`-c-...` 的默认值是 **uncompress**，不传就是未压缩补丁。
  实测 lzma 比 zstd **又小又快**（抽样最大 6 对 / 70 MiB 目标：21.5% vs 23.5%、8.2s vs 12.9s），
  所以固定用 `-c-lzma`；调级别没用（`-c-lzma` 与 `-c-lzma-9` 输出完全一样）。

实测（v4→v5 的真实内容对）：`-s` 69.7% → `-m -c-lzma` **21.5%**，约 **3.2 倍**。

## `--compress-files`：`files/{md5}.bin.gz` 预压缩旁挂（**默认关闭**）

- **客户端目前不消费它。** 客户端只请求 `files/{md5}.bin`；那条"优先取 `.gz` 并自己解压"的分支
  已按决策**删除**（将来要用时按同样的协议重新实现：客户端优先取 `.gz`、自己解压）。
- 因此打开这个开关**不会**让任何客户端省一个字节，只会带来：
  - 发布期 CPU（给每个文件压一遍）；
  - 约 **+46% 存储**（实测 1144.8 MiB → 526.3 MiB 的旁挂）；
  - 以及与客户端无关的额外文件（`files/{md5}.bin.gz`）。
- 保留它是为了"以后可能要"，以及**边缘/主机自己支持 `Content-Encoding` 的场景**：
  那种情况下主机可以直接发这些旁挂，客户端本来就是透明解压、零代码。
  但按当前决策，**这条能力不纳入设计假设**（默认认为主机没有）。
- 压不动的文件**不写**：小文件 gzip 反而更大（实测 9 字节 → 29 字节），已压过的二进制也压不动。
  所以"有的文件有旁挂、有的没有"是正常状态。

## 其它注意

- 补丁命名严格是 `patches/{oldHash}_{newHash}.bin`（old 在前）。顺序反了不会报错，
  只会"永远不命中补丁、永远完整下载"，所以规范与测试各钉了一次。
- **补丁不小于目标文件就弃用**（生成后比较大小），客户端随后自然 404 → 完整下载。
- 生成后必须**校验补丁真的能把旧文件还原成新文件**（默认开启，`--no-verify` 才跳过）。
- 外部工具随包提供 **win-x64 / win-x86 / linux-x64**（`HdiffTool.ShippedRids`）：x64 是主场，`linux-x64`
  给 Linux 发布机，`win-x86` 给 32 位宿主（官方 v5.1.3 没有 windows32 资产，随包的是 **v4.8.0**）。
  其他平台请自行放入 `hdiffpatch_bin/<rid>/`，找不到时工具会明确报错（客户端会回落完整下载，不会静默出错）。

相关：`XmlGenerator/README.md`（清单生成与"猜改名"）、仓库 `README.md`（总览与文档地图）。
---

> 返回总览：[仓库入口 `README.md`](../../README.md) · 接入用法：[`Updater_Csharp/README.md`](../README.md)（客户端库 + 宿主集成）