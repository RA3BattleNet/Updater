# Ra3.BattleNet.Updater.Share —— 共享层

客户端与服务端**都依赖**这一层；它自己**不依赖任何项目**。里面只有四件事：

| 文件 | 干什么 |
|---|---|
| `Models/ManifestModel.cs` | 清单的读写与语义（`ManifestFile` / `ManifestFolder`）。**清单的字节就是版本身份**，所以这里对格式的任何改动都要当成协议改动 |
| `Models/ManifestFileExtensions.cs` | `RelativePath()`：`Path` + `FileName` 拼成相对路径（唯一的拼接口径） |
| `Models/PathSafety.cs` | **路径信任边界**：拒绝绝对路径、盘符、UNC、任何 `..` 段与 `\\?\` 注入；判据看"定义"而不是解析结果 |
| `Models/UpdaterProtocol.cs` | URL/路径口径：`files/{md5}.bin`、`patches/{old}_{new}.bin`（内容对寻址，old 在前） |
| `Utilities/HdiffTool.cs` | 外部工具（`hdiffz`/`hpatchz`）的定位与调用：RID 选择、参数构造、退出码/stderr 处理 |

## 外部工具的定位顺序（改部署方式时看这里）

`工具目录` → `工具目录/<rid>` → `程序目录` → `程序目录/tools[/<rid>]` → `程序目录/hdiffpatch_bin/<rid>` → **PATH（最后兜底）**。

- 随包只覆盖 `HdiffTool.ShippedRids`：`win-x64` / `win-x86`（v4.8.0，官方无 windows32 资产）/ `linux-x64`。
- **两份清单必须一致**：`HdiffTool.ShippedRids` ↔ `Ra3.BattleNet.Updater.Share.csproj` 里的 `Content Include`。
- 找不到工具时报错要能区分"这个平台没打包"与"路径配错"，**不得静默继续**；客户端会因此回落完整下载（安全），发布侧直接失败（正确）。
- 随包二进制是 MIT，**必须附带上游许可声明**：`hdiffpatch_bin/LICENSE-HDiffPatch.txt`（见 [`../THIRD-PARTY.md`](../THIRD-PARTY.md)）。

## 注意

- 同步改客户端/服务端**两侧**的用法时，别在这里引入任何产品耦合（这一层要保持"协议 + 工具"两件事）。
- `PathSafety` 是安全边界，放宽它等于把"远端清单"当成可信输入 —— 别放宽。