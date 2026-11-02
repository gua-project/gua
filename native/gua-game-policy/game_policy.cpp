// Provider-built template. The GAME build embeds its own policy resource and
// gives the resulting module a game-specific name. It is not a Gua SDK DLL.
#include <windows.h>
#include <bcrypt.h>
#include <array>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <span>
#include <string>
#include <vector>
#include <mutex>
#include <algorithm>

extern "C" IMAGE_DOS_HEADER __ImageBase;
namespace {
constexpr uint32_t magic = 0x50415547; // GUAP, little endian.
struct Entry { uint32_t role; std::filesystem::path path; std::array<unsigned char, 32> hash; };
std::mutex mutex;
std::vector<HANDLE> leases;
std::vector<HMODULE> modules;
uint32_t status = 0, mode = 0;
bool plugin_preloaded = false;
#if GAME_POLICY_EDITOR
constexpr bool editor = true;
#else
constexpr bool editor = false;
#endif

struct Cursor {
    std::span<const unsigned char> bytes;
    uint32_t u32() {
        if(bytes.size() < 4) throw 80u;
        uint32_t value = static_cast<uint32_t>(bytes[0]) | (static_cast<uint32_t>(bytes[1]) << 8) |
            (static_cast<uint32_t>(bytes[2]) << 16) | (static_cast<uint32_t>(bytes[3]) << 24);
        bytes = bytes.subspan(4); return value;
    }
    std::wstring text() {
        auto length = u32();
        if(length > 32768 || length > bytes.size()) throw 80u;
        if(length == 0) return {};
        int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes.data()), length, nullptr, 0);
        if(size <= 0) throw 80u;
        std::wstring value(static_cast<size_t>(size), L'\0');
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(bytes.data()), length, value.data(), size);
        bytes = bytes.subspan(length);
        if(value.find(L'\0') != std::wstring::npos) throw 80u;
        return value;
    }
};

std::filesystem::path self_path() {
    std::wstring file(32768, L'\0');
    DWORD size = GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), file.data(), static_cast<DWORD>(file.size()));
    if(size == 0 || size >= file.size()) throw 81u;
    file.resize(size); return file;
}
void reject_links(const std::filesystem::path& file) {
    for(auto path=file; !path.empty(); path=path.parent_path()) {
        DWORD attributes = GetFileAttributesW(path.c_str());
        if(attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) throw 81u;
        if(path == path.parent_path()) break;
    }
}
std::array<unsigned char, 32> hash_file(HANDLE file) {
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{};
    if(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) throw 82u;
    if(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0); throw 82u;
    }
    std::array<unsigned char, 65536> buffer{};
    std::array<unsigned char, 32> digest{};
    bool success = true;
    for(;;) {
        DWORD read = 0;
        if(!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) {success=false; break;}
        if(read == 0) break;
        if(BCryptHashData(hash, buffer.data(), read, 0) < 0) {success=false; break;}
    }
    if(success && BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0) success=false;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
    if(!success) throw 82u;
    return digest;
}

void verify() {
    HMODULE self = reinterpret_cast<HMODULE>(&__ImageBase);
    HRSRC resource = FindResourceW(self, MAKEINTRESOURCEW(179), RT_RCDATA);
    if(!resource) throw 80u;
    DWORD size=SizeofResource(self, resource);
    auto* data=static_cast<const unsigned char*>(LockResource(LoadResource(self, resource)));
    if(!data || size < 24 || size > 1024*1024) throw 80u;
    Cursor cursor{std::span(data, size)};
    if(cursor.u32()!=magic || cursor.u32()!=1) throw 80u;
    uint32_t kind=cursor.u32();
    if(kind < 1 || kind > 3) throw 80u;
    if(editor != (kind==3)) throw 80u;
    mode=cursor.u32(); if(mode>2) throw 80u;
    auto explicit_root=cursor.text();
    auto source=cursor.text(); auto build=cursor.text();
    if(source.size()!=40 || build.empty() || build.size()>512) throw 80u;
    std::filesystem::path root=self_path().parent_path();
    if(kind==1) root=root.parent_path().parent_path(); // Player_Data/Plugins/x86_64 -> Player_Data
    else if(kind==3) { root=explicit_root; if(!root.is_absolute()) throw 80u; }
    else if(!explicit_root.empty()) throw 80u;
    uint32_t count=cursor.u32(); if(count==0 || count>256) throw 80u;
    std::vector<Entry> entries;
    uint32_t native_count=0;
    for(uint32_t i=0;i<count;i++) {
        Entry entry{}; entry.role=cursor.u32(); if(entry.role>1) throw 80u;
        auto relative=std::filesystem::path(cursor.text());
        if(relative.empty() || relative.is_absolute() || relative.has_root_name()) throw 80u;
        for(const auto& part: relative) if(part==L".." || part==L"." || part.empty()) throw 80u;
        if(cursor.bytes.size()<entry.hash.size()) throw 80u;
        std::copy_n(cursor.bytes.begin(), entry.hash.size(), entry.hash.begin());
        cursor.bytes=cursor.bytes.subspan(entry.hash.size());
        entry.path=root/relative;
        for(const auto& previous: entries) if(_wcsicmp(previous.path.c_str(),entry.path.c_str())==0) throw 80u;
        if(entry.role==0) { native_count++; if(GetModuleHandleW(entry.path.filename().c_str())) throw 79u; }
        entries.push_back(std::move(entry));
    }
    if(!cursor.bytes.empty() || native_count==0) throw 80u;
    // Every expected file is locked and hashed before loading any SDK member.
    for(const auto& entry: entries) {
        reject_links(entry.path);
        HANDLE file=CreateFileW(entry.path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if(file==INVALID_HANDLE_VALUE) throw 81u;
        leases.push_back(file);
        if(hash_file(file)!=entry.hash) throw 78u;
    }
    for(const auto& entry: entries) if(entry.role==0) {
        HMODULE library=LoadLibraryExW(entry.path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!library) throw 82u;
        modules.push_back(library);
    }
}

uint32_t initialize() {
    std::lock_guard guard(mutex);
    if(status) return status;
    try { verify(); status=1; }
    catch(uint32_t code) { status=code; }
    catch(...) {status=80;}
    if(status!=1) {
        // Fixed diagnostics only; no paths, values, secrets or recordings.
        OutputDebugStringW(L"GamePolicy: integrity verification refused.");
        // Editor failure is queried by the game-owned Editor code before Play.
        // Never terminate an Editor or discard its unsaved work.
        if(!editor) TerminateProcess(GetCurrentProcess(), status);
    } else OutputDebugStringW(L"GamePolicy: verification succeeded before SDK initialization.");
    return status;
}
}

extern "C" __declspec(dllexport) void __stdcall UnityPluginLoad(void*) { plugin_preloaded=true; initialize(); }
extern "C" __declspec(dllexport) uint32_t __cdecl GamePolicyStatus() { return initialize(); }
extern "C" __declspec(dllexport) uint32_t __cdecl GamePolicyMode() { return status==1 ? mode : 0xffffffffu; }
extern "C" __declspec(dllexport) uint32_t __cdecl GamePolicyTemplateKind() { return editor ? 3u : 0u; }
extern "C" __declspec(dllexport) uint32_t __cdecl GamePolicyWasPreloaded() { return plugin_preloaded ? 1u : 0u; }

// Minimal stable Godot GDExtension initialization ABI; no Godot C++ bindings,
// SDK context, remote bridge, registration or local observation is part of it.
struct GodotInitialization { int minimum_level; void* userdata; void (*initialize)(void*,int); void (*deinitialize)(void*,int); };
extern "C" __declspec(dllexport) unsigned char game_policy_godot_init(void*,void*,GodotInitialization* out) {
    if(!out || initialize()!=1) return 0;
    out->minimum_level=2; out->userdata=nullptr; // SCENE: permits deferred game loading.
    out->initialize=[](void*,int){}; out->deinitialize=[](void*,int){};
    return 1;
}
