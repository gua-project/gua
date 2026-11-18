// Compiled into the GAME (and declared SDK-consuming game modules), never Gua.
#include <windows.h>
#include <delayimp.h>
#include <intrin.h>
#include "gua_game_policy_config.h"

namespace {
INIT_ONCE once = INIT_ONCE_STATIC_INIT;
volatile LONG runs = 0;
HMODULE core{}, runtime{};
[[noreturn]] void refuse(unsigned code) { TerminateProcess(GetCurrentProcess(),code); __fastfail(7); }
bool sdk_name(const char* name) {return name && (_stricmp(name,"gua.dll")==0 || _stricmp(name,"gua_runtime.dll")==0);}
BOOL CALLBACK initialize(PINIT_ONCE,void*,void**) {
    InterlockedIncrement(&runs);
    wchar_t file[32768];
    DWORD size=GetModuleFileNameW(nullptr,file,32768);
    if(!size || size>=32768) refuse(81);
    wchar_t* end=file+size;
    while(end>file && end[-1]!=L'\\' && end[-1]!=L'/') --end;
    const wchar_t* name=GUA_GAME_POLICY_LIBRARY;
    if((end-file)+lstrlenW(name)>=32768) refuse(81);
    lstrcpyW(end,name);
    // This uniquely named module and its resource are GAME code/data. There is
    // no runtime SDK manifest lookup or SDK permission query here.
#ifdef GUA_GAME_DEPENDENT_MODULE
    // A DLL global constructor/DllMain runs under the loader lock. Never load
    // or verify there. The EXE must have completed the bootstrap first.
    HMODULE checker=GetModuleHandleW(GUA_GAME_POLICY_LIBRARY);
    if(!checker) refuse(86);
#else
    HMODULE checker=LoadLibraryExW(file,nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
#endif
    if(!checker) refuse(81);
    using Query=unsigned(__cdecl*)();
    auto status=reinterpret_cast<Query>(GetProcAddress(checker,"GamePolicyStatus"));
    auto mode=reinterpret_cast<Query>(GetProcAddress(checker,"GamePolicyMode"));
    if(!status || !mode) refuse(80);
#ifdef GUA_GAME_DEPENDENT_MODULE
    if(mode()!=GUA_GAME_POLICY_MODE) refuse(86);
#else
    if(status()!=1 || mode()!=GUA_GAME_POLICY_MODE) refuse(84);
#endif
    core=GetModuleHandleW(L"gua.dll");runtime=GetModuleHandleW(L"gua_runtime.dll");
    if(!core || !runtime) refuse(82);
    return TRUE;
}
void ensure() {if(!InitOnceExecuteOnce(&once,initialize,nullptr,nullptr)) refuse(82);}
}

#ifdef GUA_GAME_NOTIFY_FALLBACK
extern FARPROC WINAPI GUA_GAME_NOTIFY_FALLBACK(unsigned,PDelayLoadInfo);
#endif
#ifdef GUA_GAME_FAILURE_FALLBACK
extern FARPROC WINAPI GUA_GAME_FAILURE_FALLBACK(unsigned,PDelayLoadInfo);
#endif
static FARPROC WINAPI notify(unsigned event,PDelayLoadInfo info) {
    if(sdk_name(info->szDll)) {
        ensure();
        if(event==dliNotePreLoadLibrary) return reinterpret_cast<FARPROC>(_stricmp(info->szDll,"gua.dll")==0?core:runtime);
        return nullptr;
    }
#ifdef GUA_GAME_NOTIFY_FALLBACK
    return GUA_GAME_NOTIFY_FALLBACK(event,info);
#else
    return nullptr; // Unrelated delayed imports keep default loader behavior.
#endif
}
static FARPROC WINAPI failure(unsigned event,PDelayLoadInfo info) {
    if(sdk_name(info->szDll)) refuse(85);
#ifdef GUA_GAME_FAILURE_FALLBACK
    return GUA_GAME_FAILURE_FALLBACK(event,info);
#else
    return nullptr;
#endif
}
extern "C" const PfnDliHook __pfnDliNotifyHook2=notify;
extern "C" const PfnDliHook __pfnDliFailureHook2=failure;
// Before ordinary user global constructors. The delay hook also guards SDK
// calls made by earlier constructors; no CRT entry point is replaced.
#pragma warning(push)
#pragma warning(disable:4073)
#ifndef GUA_GAME_DEPENDENT_MODULE
#pragma init_seg(lib)
struct GameStartup {GameStartup(){ensure();}};
static GameStartup startup;
#endif
#pragma warning(pop)
extern "C" unsigned gua_game_guard_initializations() {ensure();return static_cast<unsigned>(runs);}
