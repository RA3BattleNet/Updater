# UpdaterCpp

RA3BattleNet / CoronaLauncher 的 C++ 客户端增量更新引擎。

从 C# 参考实现移植。模块化架构：hash → download → patch → engine。

---

## 架构

```
UpdaterCpp.exe
├── main.cpp            CLI 入口（测试/调试驱动）
├── engine.h/cpp        UpdateEngine — 编排器（6 阶段循环）
├── hash.h/cpp          MD5：ComputeFileMd5 + ComputeNormalizedMd5（CRLF→LF 归一化）
├── download.h/cpp      WinHTTP 下载，支持进度回调 + 取消
├── patch.h/cpp         ApplyPatch：hpatchz 子进程（300 秒超时，-s 流式）
├── manifest.h/cpp      Manifest / FileEntry 模型（富字段：genTime、commit、FileType、FileMode）
├── progress_ui.h/cpp   Windows 进度对话框 + 回调桥接
├── language.h          中/英文字符串表
├── UpdaterCpp.vcxproj  VS 2022 工程（v143、x86、vcpkg x86-windows-static）
└── UpdaterCpp.vcxproj.filters
```

### UpdateEngine（6 阶段循环）

| 阶段 | 操作 |
|------|------|
| 1 | 从 `newestManifestUrl` 下载远端 manifest |
| 2 | UUID 比对 — 若客户端 UUID 与最新一致则跳过 |
| 3 | 加载 PatchIndex（`patches.json`） |
| 4 | 对每个文件：根据是否可用执行全量下载或 patch 应用 |
| 5 | 写入后校验 MD5 |
| 6 | 成功时覆盖本地 manifest |

### CRLF→LF 归一化

服务端 `XmlGenerator` 对 `FileType::Text` 类型文件会在计算 MD5 前将 `\r\n` 归一化为 `\n`。
`ComputeNormalizedMd5()` 在客户端同步此行为，确保 Text 文件 MD5 一致。

- `NormalizeCrlf(buf, len)` — 原地双指针 O(n) 扫描，O(1) 额外空间
- `engine.cpp` 当 `fileEntry.type == FileType::Text` 时选用 `ComputeNormalizedMd5`
- Binary 文件（`FileType::Binary` / 默认）仍使用原生 `ComputeFileMd5`

### hpatchz 集成

- 工具：`tools/hpatchz.exe`（HDiffPatch 4.8.0）
- 模式：`-s` 流式（4 MiB 窗口），大文件无内存暴涨
- 超时：**300 秒**（5 分钟），适配数 GB 级别补丁
- 签名：`ApplyPatch(oldFile, patchFile, newFile, cancelToken)`
- 错误处理：捕获 stderr，超时/非零退出时返回描述性错误

---

## 构建

### 前置条件

- Visual Studio 2022（v143 工具集，Windows 10 SDK）
- vcpkg 并安装 `x86-windows-static` triplet
- 通过 vcpkg 安装依赖：
  - `pugixml`
  - `nlohmann-json`
  - `libzip`

### 构建命令

```powershell
# Release
msbuild source\UpdaterCpp.vcxproj /p:Configuration=Release /p:Platform=Win32

# Debug
msbuild source\UpdaterCpp.vcxproj /p:Configuration=Debug /p:Platform=Win32
```

输出：`build\UpdaterCpp.exe`

### 若 vcpkg MSBuild 集成不可用

`.vcxproj` 已内置硬编码的 include/library 路径作为回退。若 vcpkg 根目录不同于 `%USERPROFILE%\.local\opt\vcpkg`，请更新：

```xml
<AdditionalIncludeDirectories>$(USERPROFILE)\.local\opt\vcpkg\installed\x86-windows-static\include</AdditionalIncludeDirectories>
<AdditionalLibraryDirectories>$(USERPROFILE)\.local\opt\vcpkg\installed\x86-windows-static\lib</AdditionalLibraryDirectories>
```

---

## 测试

```powershell
pwsh test\run_test.ps1
```

测试（7/7 通过）：

| # | 领域 | 说明 |
|---|------|------|
| 1 | 环境 | 测试目录结构已创建 |
| 2 | Hash | `ComputeFileMd5` 对已知输入正确 |
| 3 | Patch | `hpatchz.exe` 路径解析 |
| 4 | Manifest | 富 XML roundtrip（genTime、commit、KindOf、FileType） |
| 5 | PatchIndex | `patches.json` 生成和 `FindPatch` 查找 |
| 6 | Engine | UUID 稳定性、文件更新逻辑（离线验证） |
| 7 | 集成 | CoronaLauncher 嵌入 — 编译冒烟测试 |

---

## 数据格式说明

### Manifest XML（富字段）

```xml
<?xml version="1.0" encoding="utf-8"?>
<Manifest uuid="550e8400-e29b-41d4-a716-446655440000"
          genTime="1700000000" commit="abc123def" version="1.0.0">
  <File fileName="RA2+Launcher.exe" path="RA2+Launcher.exe"
        fileSize="1234567" md5="d41d8cd98f00b204e9800998ecf8427e"
        mode="Auto" type="1" kindOf="GAME;CLIENT;">
    <RemoteFile url="https://example.com/files/..." />
  </File>
  ...
</Manifest>
```

### FileType 枚举

| 值 | 名称 | MD5 行为 |
|----|------|---------|
| 0 | Binary | 原始字节哈希 |
| 1 | Text | 先 CRLF→LF 归一化再哈希 |

### PatchIndex（patches.json）

```json
{
  "files": [
    {
      "fileName": "RA2+Launcher.exe",
      "patches": [
        { "from": "1.0.0", "to": "2.0.0", "patchFile": "patches/RA2+Launcher_1.0.0_2.0.0.hdz" }
      ]
    }
  ]
}
```

---

## CoronaLauncher 嵌入

引擎已嵌入 `CoronaLauncher/source/AppHost/`：

```
AppHost/
├── engine.h/cpp        UpdateEngine — 差异检查 + 文件更新
├── hash.h/cpp          MD5（原始 + 归一化）
├── download.h/cpp      WinHTTP 进度感知下载
├── patch.h/cpp         hpatchz 流式补丁
├── manifest.h/cpp      富 manifest 模型
├── updater.cpp          CheckForUpdates → 调用 UpdateEngine
├── progress_ui.cpp      Windows 进度对话框（SetProgressMessage）
└── AppHost.vcxproj      MSBuild 工程（v145、x86、vcpkg + YY-Thunks）
```

### 集成契约

- `CheckForUpdates` 下载远端 manifest，比对 UUID，调用 `engine.Run()`
- 自更新检查：`file.fileName` 与当前 exe 名称匹配
- 进度：`EngineProgress.status` → `GetLang()` 双语字符串
- 引擎缓存根目录：`{exeDir}/UpdaterCache`
- 自更新临时目录：`{exeDir}/CoronaData/Temp/updates`

---

## 与 C# 版的差异

| 方面 | C#（Updater_Csharp） | C++（UpdaterCpp） |
|------|---------------------|-------------------|
| 运行时 | .NET 8 | 原生（MSVC v143） |
| Manifest XML | XmlDocument | pugixml |
| HTTP | HttpClient | WinHTTP |
| 哈希 | MD5CryptoServiceProvider | WinCrypt BCrypt |
| 补丁 | hdiffz/hpatchz 进程 | hpatchz 子进程 |
| JSON | System.Text.Json | nlohmann-json |
| Zip | System.IO.Compression | libzip |
| 目标 | CLI / GUI | CLI（嵌入 AppHost） |
| CRLF 归一化 | XmlGenerator 服务端 | ComputeNormalizedMd5 客户端 |

---

## CLI

```powershell
UpdaterCpp.exe hash --file <路径>
UpdaterCpp.exe manifest --old <目录> --new <目录> --uuid <uuid>
UpdaterCpp.exe patch --index <索引> --file <名称>
UpdaterCpp.exe engine <旧目录> <新目录> <缓存目录> <下载URL>
UpdaterCpp.exe --relaunch <exe> [--wait <pid>]
```
