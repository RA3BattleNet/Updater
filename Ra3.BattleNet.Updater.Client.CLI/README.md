# Ra3.BattleNet.Updater.Client.CLI —— 独立进程壳

宿主**不是 C#**、或需要**在宿主退出后落地**时的入口。它只是
`Ra3.BattleNet.Updater.Client` 的命令行包装：解析参数 → 调库 → 打印一行 JSON。

## 用法

```
Client.CLI --root <安装目录> --manifest-url <清单地址> [选项]

  --local-manifest <路径>   本地清单路径（默认 <root>/manifest.xml）
  --cache-dir <目录>        缓存目录（默认 <系统临时目录>/updater-cache/<安装根指纹>）
  --tools-dir <目录>        外部工具目录（默认程序目录）
  --log <路径>              日志路径（默认 <cache-dir>/update.log；未指定 cache-dir 时用用户目录下的 updater-logs）
  --exclude <列表>          不受管顶层目录，逗号分隔
  --fallback <列表>         备用基准地址，逗号分隔
  --concurrency <N>         并发上限（默认 4）
  --threshold-files <N>     待下载文件数阈值（默认 0 = 关闭）
  --threshold-ratio <R>     待下载文件数占比阈值（默认 0 = 关闭）
  --apply-mode <模式>       inplace（默认，就地替换）或 staged（只暂存，宿主退出后由 applier 落地）
  --verify-unchanged        对判定无需更新的文件重新校验哈希（慢）。
                            开了它会**跳过"已最新"的捷径**：即使清单没变也逐文件核对磁盘，
                            坏掉的资源会被重新下载 —— 宿主"修复资源"按钮用这个
  --json                    只输出一行 JSON 结果

暂存更新的落地（宿主退出后跑；库不自己 spawn 进程）：
  --apply                   只做落地：把 UpdaterStage 里已就绪的内容换上去
  --wait-for-pid <PID>      --apply 时先等这个进程退出（宿主把自己的 PID 传进来）
  --wait-for-name <名字>    连同 PID 一起认宿主（PID 会被复用，光凭 PID 认不准）
  --wait-for-start <ticks>  宿主的启动时刻，唯一实例标识（后两个由 BuildApplyCommand 自动填）
  --quiescence-timeout <秒> --apply 时等树静的时限（默认 600）
  --poll-seconds <秒>       --apply 时的轮询间隔（默认 2）

落地成功后可选地把宿主拉起来（宿主一般用 BuildApplyCommand 自动带上）：
  --restart <exe>           要拉起的可执行文件
  --restart-args <原文>     原样传回的参数（原始命令行去掉 exe 那段的**原文**，引号不重新解释）
  --restart-cwd <目录>      宿主的工作目录
  --restart-delay <秒>      拉起前等待（默认 1）
```

## 退出码

`0` 已最新或已更新 ｜ `1` 需要完整包 / 失败 ｜ `2` 参数或配置错误 ｜ `3` **已暂存待落地**（暂存模式，宿主要退出）。

## 输出契约

`--json` 输出**一行 JSON**，字段集由库定义（`UpdateResult.ToJson()`，只增不改）：
[`../USAGE.md`](../USAGE.md) §2.3 的全部字段 + `Applied` / `PendingRestart` + `Ms`。
**壳只要读这行就够了** —— `PendingRestart` 就是"该重启"这个信号，不必靠猜退出码。

## 两个典型用法

```powershell
# ① 就地更新（不需要替换宿主自己的文件）
Client.CLI --root <安装目录> --manifest-url <清单地址> --json

# ② 暂存 + 宿主退出后落地（自更新）
Client.CLI --root <安装目录> --manifest-url <清单地址> --apply-mode staged --json   # 退出码 3
Client.CLI --apply --root <安装目录> --manifest-url <清单地址> --wait-for-pid <宿主PID>
```

宿主集成（谁在什么时候挂 applier、怎么收尾）见 [`../USAGE.md`](../USAGE.md) §2.8。