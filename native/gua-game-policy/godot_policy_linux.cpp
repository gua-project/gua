// Provider-built GAME template. Expected data is patched by the normal game
// build, outside the exchangeable SDK. No SDK import or automatic bridge start.
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <linux/if_alg.h>
#include <fcntl.h>
#include <unistd.h>
#include <link.h>
#include <dlfcn.h>
#include <array>
#include <string>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <span>
#include <cstdint>
#include <algorithm>

extern "C" {
__attribute__((section(".guapol"),visibility("hidden"),used))
extern const volatile unsigned char GameGodotPolicySlot[65536]={
    0x47,0x55,0x41,0x47,0x44,0x50,0x4f,0x4c,0x49,0x43,0x59,0x53,0x4c,0x4f,0x54,0x31
};
}
namespace {
struct Init { int minimum_level; void* userdata; void (*initialize)(void*,int); void (*deinitialize)(void*,int); };
static int pinned=-1;
static void* module=nullptr;
static std::once_flag initialized;
static bool accepted=false;
struct Cursor {
    std::span<const unsigned char> bytes;
    uint32_t u32() {
        if(bytes.size()<4)throw 1;
        auto value=uint32_t(bytes[0])|(uint32_t(bytes[1])<<8)|(uint32_t(bytes[2])<<16)|(uint32_t(bytes[3])<<24);
        bytes=bytes.subspan(4);return value;
    }
    std::string text() {
        auto size=u32();if(size>32768 || size>bytes.size())throw 1;
        std::string value(reinterpret_cast<const char*>(bytes.data()),size);
        bytes=bytes.subspan(size);if(value.find('\0')!=std::string::npos)throw 1;return value;
    }
};
static std::array<unsigned char,32> expected_hash() {
    std::array<unsigned char,65536> slot;
    for(size_t i=0;i<slot.size();++i)slot[i]=GameGodotPolicySlot[i];
    Cursor length{std::span(slot).subspan(16,4)};auto size=length.u32();
    if(size>slot.size()-20)throw 1;
    Cursor data{std::span(slot).subspan(20,size)};
    if(data.u32()!=0x50415547 || data.u32()!=1 || data.u32()!=4 || data.u32()>2 || !data.text().empty())throw 1;
    auto source=data.text();auto build=data.text();
    if(source.size()!=40 || source.find_first_not_of("0123456789abcdef")!=std::string::npos || build.empty())throw 1;
    if(data.u32()!=1 || data.u32()!=0 || data.text()!="gua.so" || data.bytes.size()!=32)throw 1;
    std::array<unsigned char,32> result;std::copy(data.bytes.begin(),data.bytes.end(),result.begin());return result;
}
static int seen_sdk(dl_phdr_info* entry,size_t,void* found) {
    std::string path=entry->dlpi_name;
    auto name=path.substr(path.find_last_of('/')+1);
    if(name=="gua.so" || name.starts_with("gua_godot.")) *static_cast<bool*>(found)=true;
    // A renamed legitimate SDK retains its public GDE entry point. NOLOAD
    // inspects existing modules without running or loading a candidate.
    if(!path.empty()) {
        void* loaded=dlopen(path.c_str(),RTLD_NOLOAD|RTLD_NOW|RTLD_LOCAL);
        if(loaded) {
            if(dlsym(loaded,"gua_godot_library_init")) *static_cast<bool*>(found)=true;
            dlclose(loaded);
        }
    }
    return 0;
}
static bool digest(int fd,std::array<unsigned char,32>& output) {
    // AF_ALG is a local kernel-crypto interface, not a TCP/Gua listener.
    int transform=socket(AF_ALG,SOCK_SEQPACKET|SOCK_CLOEXEC,0);
    if(transform<0) return false;
    sockaddr_alg address{}; address.salg_family=AF_ALG;
    strcpy(reinterpret_cast<char*>(address.salg_type),"hash");
    strcpy(reinterpret_cast<char*>(address.salg_name),"sha256");
    int operation=-1; bool good=false;
    if(bind(transform,reinterpret_cast<sockaddr*>(&address),sizeof(address))==0) {
        operation=accept4(transform,nullptr,nullptr,SOCK_CLOEXEC);
        if(operation>=0 && lseek(fd,0,SEEK_SET)==0) {
            std::array<unsigned char,65536> bytes; ssize_t count; good=true;
            while((count=read(fd,bytes.data(),bytes.size()))>0) {
                size_t sent=0;
                while(sent<static_cast<size_t>(count)) {
                    auto n=send(operation,bytes.data()+sent,count-sent,MSG_MORE|MSG_NOSIGNAL);
                    if(n<=0) {good=false;break;} sent+=n;
                }
                if(!good)break;
            }
            good=good && count==0 && send(operation,nullptr,0,MSG_NOSIGNAL)==0 && read(operation,output.data(),output.size())==32;
        }
    }
    if(operation>=0)close(operation); close(transform);return good;
}
static bool verify() {
    auto expected=expected_hash();
    bool found=false; dl_iterate_phdr(seen_sdk,&found); if(found)return false;
    Dl_info self{}; if(!dladdr(reinterpret_cast<void*>(&verify),&self))return false;
    std::string own=self.dli_fname; auto path=own.substr(0,own.find_last_of('/')+1)+"gua.so";
    int source=open(path.c_str(),O_RDONLY|O_CLOEXEC|O_NOFOLLOW); if(source<0)return 0;
    struct stat info{}; if(fstat(source,&info) || !S_ISREG(info.st_mode) || info.st_size<=0 || info.st_size>512*1024*1024) {close(source);return 0;}
    pinned=memfd_create("gua-game-verified",MFD_CLOEXEC|MFD_ALLOW_SEALING); if(pinned<0){close(source);return 0;}
    std::array<unsigned char,65536> bytes; ssize_t count; size_t size=0; bool good=true;
    while((count=read(source,bytes.data(),bytes.size()))>0) {
        size+=count; if(size>512*1024*1024){good=false;break;}
        size_t written=0; while(written<static_cast<size_t>(count)) {auto n=write(pinned,bytes.data()+written,count-written);if(n<=0){good=false;break;}written+=n;} if(!good)break;
    }
    close(source);
    constexpr int seals=F_SEAL_WRITE|F_SEAL_GROW|F_SEAL_SHRINK|F_SEAL_SEAL;
    if(!good || count<0 || size!=static_cast<size_t>(info.st_size) || fcntl(pinned,F_ADD_SEALS,seals) || (fcntl(pinned,F_GET_SEALS)&seals)!=seals) return 0;
    std::array<unsigned char,32> hash{};
    if(!digest(pinned,hash) || hash!=expected)return false;
    // Hash only after all seals succeeded: a writer of the original SDK cannot
    // affect the immutable object handed to dlopen. Advisory file locks alone
    // would not provide this guarantee on Linux.
    fprintf(stderr,"GamePolicy: sealed files verified before SDK load.\n");
    std::string fdpath="/proc/self/fd/"+std::to_string(pinned);
    module=dlopen(fdpath.c_str(),RTLD_NOW|RTLD_LOCAL|RTLD_DEEPBIND);
    if(!module)return false;
    return dlsym(module,"gua_godot_library_init")!=nullptr;
}
} // namespace
extern "C" __attribute__((visibility("default"))) unsigned char game_policy_godot_init(void* getproc,void* library,Init* out) {
    if(!getproc || !library || !out)return 0;
    std::call_once(initialized,[]{
        try {accepted=verify();} catch(...) {accepted=false;}
        if(!accepted) {
            if(pinned>=0){close(pinned);pinned=-1;}
            fprintf(stderr,"GamePolicy: integrity verification refused.\n");
        }
    });
    if(!accepted)return 0;
    using Entry=unsigned char(*)(void*,void*,Init*);
    auto entry=reinterpret_cast<Entry>(dlsym(module,"gua_godot_library_init"));
    return entry ? entry(getproc,library,out):0;
}
