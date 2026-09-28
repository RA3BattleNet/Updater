# 第三方组件与许可

本项目**随包分发**了下列第三方二进制。它们都是 MIT 许可：**不要求通知作者、不要求开源本项目**，
但要求**保留版权声明与许可全文** —— 所以请勿删除 `hdiffpatch_bin/LICENSE-HDiffPatch.txt`。

| 组件 | 许可 | 随包内容 | 上游 |
|---|---|---|---|
| **HDiffPatch**（hdiffz / hpatchz） | MIT，`Copyright (c) 2012-2025 housisong`（v4.8.0 为 `2012-2023`） | `win-x64`、`linux-x64` 为 **v5.1.3**；`win-x86` 为 **v4.8.0** | https://github.com/sisong/HDiffPatch |
| **libdivsufsort** | MIT，`Copyright (c) 2003-2008 Yuta Mori` | HDiffPatch 内嵌（不单独分发） | https://github.com/y-256/libdivsufsort |

许可全文（两个组件各一段，逐字复制自上游）：`Updater_Csharp/Ra3.BattleNet.Updater.Share/hdiffpatch_bin/LICENSE-HDiffPatch.txt`

## 更新二进制时要做什么

1. 从上游 release 取对应平台资产，**不要修改二进制**；
2. 同步更新 `hdiffpatch_bin/LICENSE-HDiffPatch.txt`（把上游 LICENSE 原文替换进去）；
3. 若随包的版本变了，更新本文件与 `LICENSE-HDiffPatch.txt` 头部里的版本号与版权年份；
4. `HdiffTool.ShippedRids` 与 `Share.csproj` 里的 `Content Include` 两份清单必须一致。