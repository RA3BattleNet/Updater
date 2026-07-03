#pragma once
#include <string>
#include <vector>
#include <cstdint>

// 文件类型（对应 C# FileTypeEnum）
enum class FileType : int { Bin = 0, Text = 1 };

// 文件处理模式（对应 C# FileModeEnum）
enum class FileMode : int { Auto = 0, Force = 1, Skip = 2 };

inline std::wstring FileModeToStr(FileMode m)
{
    switch (m) {
    case FileMode::Force: return L"Force";
    case FileMode::Skip:  return L"Skip";
    default:              return L"Auto";
    }
}

inline FileMode StrToFileMode(const std::wstring& s)
{
    if (s == L"Force") return FileMode::Force;
    if (s == L"Skip")  return FileMode::Skip;
    return FileMode::Auto;
}

inline std::wstring FileTypeToStr(FileType t)
{
    return (t == FileType::Text) ? L"Text" : L"Bin";
}

// 单个文件条目（对应 C# ManifestFile）
struct ManifestFileEntry {
    std::wstring uuid;          // 文件唯一标识（32 位小写十六进制）
    std::wstring fileName;      // 文件名（不含路径）
    std::wstring md5;           // MD5 哈希（32位小写十六进制）
    std::wstring path;          // 目录路径（如 "\"、"\CoronaResources\"），不含文件名
    std::wstring fileVersion;   // 文件版本（如 "1.0.0"）
    FileType type;              // 0=Bin, 1=Text
    FileMode mode;              // 0=Auto, 1=Force, 2=Skip
    std::wstring kindOf;        // 文件种类标识（如 "APPLICATION;PROGRAM;"、"NULL"）

    // 获取完整相对路径（目录 + 文件名）
    std::wstring FullPath() const
    {
        if (path.empty() || path == L"." || path == L"\\")
            return fileName;
        std::wstring p = path;
        if (p.back() != L'/' && p.back() != L'\\')
            p += L'/';
        return p + fileName;
    }
};

// 清单中的文件夹项（对应 C# ManifestFolder，当前未使用但保留结构）
struct ManifestFolderEntry {
    std::wstring folderName;
    std::wstring path;
    bool recursion;
    FileType type;
    FileMode mode;
};

// 对应 manifest.xml（兼容现有服务端格式）
struct UpdateManifest {
    std::wstring version;       // Metadata.Version
    std::wstring uuid;          // Tags.UUID — 主版本标识
    int64_t genTime;            // Tags.GenTime — Unix 时间戳（秒）
    std::wstring commit;        // Tags.Commit — 提交信息

    std::vector<ManifestFileEntry> files;
    std::vector<ManifestFolderEntry> folders;  // 保留结构，暂未使用

    // 从 XML 加载（兼容现有服务端）
    bool LoadFromXml(const std::wstring& path);

    // 从 JSON 加载（未来新格式）
    bool LoadFromJson(const std::wstring& path);

    // 保存为 XML
    bool SaveToXml(const std::wstring& path) const;

    bool IsValid() const { return !uuid.empty(); }

    void Clear()
    {
        version.clear();
        uuid.clear();
        genTime = 0;
        commit.clear();
        files.clear();
        folders.clear();
    }
};

// PatchIndex：patches.json 中的单条补丁记录
struct PatchInfo {
    std::wstring uuid;          // 文件 UUID
    std::wstring patchName;     // 补丁文件 GUID（不含扩展名）
    std::wstring oldMd5;        // 源文件 MD5
    std::wstring newMd5;        // 目标文件 MD5
};

// 补丁索引表，用于查找增量补丁
class PatchIndex {
    std::vector<PatchInfo> m_patches;
public:
    bool LoadFromFile(const std::wstring& path);

    // 根据文件 UUID + 源 MD5 + 目标 MD5 查找补丁
    // 返回补丁文件名（如 "a1b2c3d4..."），空字符串表示未找到
    std::wstring FindPatch(const std::wstring& uuid,
                           const std::wstring& oldMd5,
                           const std::wstring& newMd5) const;

    bool IsValid() const { return !m_patches.empty(); }
    void Clear() { m_patches.clear(); }

    size_t Count() const { return m_patches.size(); }
    const PatchInfo& GetPatch(size_t i) const { return m_patches[i]; }
};
