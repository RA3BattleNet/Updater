#include "manifest.h"
#include <pugixml.hpp>
#include <nlohmann/json.hpp>
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <cstdlib>

namespace fs = std::filesystem;

static std::wstring Utf8ToWide(const std::string& utf8)
{
    if (utf8.empty()) return {};
    int len = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (len <= 0) return {};
    std::wstring result(static_cast<size_t>(len), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), result.data(), len);
    return result;
}

static std::string WideToUtf8(const std::wstring& wide)
{
    if (wide.empty()) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()),
                                  nullptr, 0, nullptr, nullptr);
    if (len <= 0) return {};
    std::string result(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()),
                        result.data(), len, nullptr, nullptr);
    return result;
}

static std::wstring ChildText(pugi::xml_node parent, const char* name)
{
    auto child = parent.child(name);
    if (!child) return {};
    return Utf8ToWide(child.text().as_string());
}

static int ChildInt(pugi::xml_node parent, const char* name, int def = 0)
{
    auto child = parent.child(name);
    if (!child) return def;
    return child.text().as_int(def);
}

// ============================================================
//  XML — pugixml
// ============================================================
bool UpdateManifest::LoadFromXml(const std::wstring& path)
{
    Clear();

    pugi::xml_document doc;
    auto loadResult = doc.load_file(path.c_str());
    if (!loadResult) return false;

    auto meta = doc.child("Metadata");
    if (!meta) return false;

    version = Utf8ToWide(meta.attribute("Version").as_string());
    if (version.empty()) return false;

    auto tags = meta.child("Tags");
    if (tags)
    {
        uuid    = ChildText(tags, "UUID");
        genTime = static_cast<int64_t>(ChildInt(tags, "GenTime"));
        commit  = ChildText(tags, "Commit");
    }

    auto manifestNode = meta.child("Manifest");
    if (!manifestNode) return false;

    for (auto fileNode : manifestNode.children("File"))
    {
        ManifestFileEntry entry;
        entry.uuid        = ChildText(fileNode, "UUID");
        entry.fileName    = ChildText(fileNode, "FileName");
        entry.md5         = ChildText(fileNode, "MD5");
        entry.path        = ChildText(fileNode, "Path");
        entry.fileVersion = ChildText(fileNode, "Version");
        entry.type        = static_cast<FileType>(ChildInt(fileNode, "Type"));
        entry.mode        = StrToFileMode(ChildText(fileNode, "Mode"));
        entry.kindOf      = ChildText(fileNode, "KindOf");

        if (entry.uuid.empty()) continue;
        files.push_back(std::move(entry));
    }

    return !files.empty();
}

bool UpdateManifest::SaveToXml(const std::wstring& path) const
{
    pugi::xml_document doc;

    auto meta = doc.append_child("Metadata");
    meta.append_attribute("Version") = WideToUtf8(version).c_str();

    auto tags = meta.append_child("Tags");
    tags.append_child("UUID").text()    = WideToUtf8(uuid).c_str();
    tags.append_child("GenTime").text() = std::to_string(genTime).c_str();
    tags.append_child("Commit").text()  = WideToUtf8(commit).c_str();

    meta.append_child("Includes");

    auto manifestNode = meta.append_child("Manifest");
    for (auto& f : files)
    {
        auto fileNode = manifestNode.append_child("File");
        auto pathStr = f.path.empty() ? L"\\" : f.path;
        fileNode.append_child("UUID").text()      = WideToUtf8(f.uuid).c_str();
        fileNode.append_child("FileName").text()  = WideToUtf8(f.fileName).c_str();
        fileNode.append_child("MD5").text()       = WideToUtf8(f.md5).c_str();
        fileNode.append_child("Path").text()      = WideToUtf8(pathStr).c_str();
        auto ver = f.fileVersion.empty() ? L"1.0.0" : f.fileVersion;
        fileNode.append_child("Version").text()   = WideToUtf8(ver).c_str();
        fileNode.append_child("Type").text()      = WideToUtf8(FileTypeToStr(f.type)).c_str();
        fileNode.append_child("Mode").text()      = WideToUtf8(FileModeToStr(f.mode)).c_str();
        auto kof = f.kindOf.empty() ? L"NULL" : f.kindOf;
        fileNode.append_child("KindOf").text()    = WideToUtf8(kof).c_str();
    }

    fs::create_directories(fs::path(path).parent_path());
    return doc.save_file(path.c_str(), "  ", pugi::format_default, pugi::encoding_utf8);
}

// ============================================================
//  JSON — nlohmann
// ============================================================
bool UpdateManifest::LoadFromJson(const std::wstring& path)
{
    Clear();

    std::ifstream ifs(path);
    if (!ifs) return false;

    nlohmann::json j;
    try { j = nlohmann::json::parse(ifs); }
    catch (...) { return false; }

    version = Utf8ToWide(j.value("version", ""));
    uuid    = Utf8ToWide(j.value("uuid", ""));
    if (uuid.empty()) return false;

    genTime = j.value("genTime", static_cast<int64_t>(0));
    commit  = Utf8ToWide(j.value("commit", ""));

    auto& jfiles = j["files"];
    for (auto& jf : jfiles)
    {
        ManifestFileEntry entry;
        entry.uuid        = Utf8ToWide(jf.value("uuid", ""));
        entry.fileName    = Utf8ToWide(jf.value("fileName", ""));
        entry.md5         = Utf8ToWide(jf.value("md5", ""));
        entry.path        = Utf8ToWide(jf.value("path", ""));
        entry.fileVersion = Utf8ToWide(jf.value("fileVersion", ""));
        entry.type        = static_cast<FileType>(jf.value("type", 0));
        entry.mode        = static_cast<FileMode>(jf.value("mode", 0));
        entry.kindOf      = Utf8ToWide(jf.value("kindOf", ""));

        if (entry.uuid.empty()) continue;
        files.push_back(std::move(entry));
    }
    return !files.empty();
}

// ============================================================
//  PatchIndex — nlohmann
// ============================================================
bool PatchIndex::LoadFromFile(const std::wstring& path)
{
    m_patches.clear();

    std::ifstream ifs(path);
    if (!ifs) return false;

    nlohmann::json j;
    try { j = nlohmann::json::parse(ifs); }
    catch (...) { return false; }

    if (!j.is_array()) return false;

    for (auto& item : j)
    {
        PatchInfo info;
        info.uuid      = Utf8ToWide(item.value("UUID", ""));
        info.patchName = Utf8ToWide(item.value("PatchName", ""));
        info.oldMd5    = Utf8ToWide(item.value("OldMD5", ""));
        info.newMd5    = Utf8ToWide(item.value("NewMD5", ""));

        if (!info.uuid.empty() && !info.patchName.empty())
            m_patches.push_back(std::move(info));
    }
    return !m_patches.empty();
}

std::wstring PatchIndex::FindPatch(const std::wstring& uuid,
                                    const std::wstring& oldMd5,
                                    const std::wstring& newMd5) const
{
    for (auto& p : m_patches)
    {
        if (p.uuid == uuid && p.oldMd5 == oldMd5 && p.newMd5 == newMd5)
            return p.patchName;
    }
    return {};
}
