# RA3BattleNet Updater

> 文件增量更新工具集，包含 C# 和 C++ 两个独立实现。其中 C++ 由 C# 项目迁移

## 项目结构

```
Updater/
├── Updater_Csharp/     C# 增量更新器（服务端 + 客户端）
└── Updater_Cpp/        C++ 增量更新引擎（用于嵌入 CoronaLauncher AppHost）
```

两个项目实现相同的增量更新协议，可独立使用。

### [Updater_Csharp](Updater_Csharp/README.md)

.NET 8 实现，包含完整的服务端和客户端工具链：

- **服务端工具** — XML 清单生成、增量补丁计算、Patch 索引管理
- **客户端工具** — 补丁下载、应用、MD5 校验
- **数据格式** — Manifest XML + HDiffPatch 差异补丁 + patches.json 索引

### [Updater_Cpp](Updater_Cpp/README.md)

C++20 原生实现（嵌入 CoronaLauncher 的 AppHost 启动器）：

- **UpdateEngine** — 6 阶段增量更新循环
- **WinHTTP 下载** + **hpatchz 补丁** + **Windows CryptoAPI MD5**
- 作为 Phase 0 在加载 .NET 运行时之前执行

## 参考

- [RA3BattleNet Metadata](https://github.com/RA3BattleNet/Metadata)
- [HDiffPatch](https://github.com/sisong/HDiffPatch)
