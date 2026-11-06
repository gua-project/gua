"""Independent Linux x86_64 syscall observer for OWNED test games only.

No product hook: stop at the real loader openat/mmap of its sealed memfd.
"""
import ctypes
import hashlib
import os
from pathlib import Path
import signal
import subprocess
import struct

def is_gua_image(image):
    # Inspect an actual defined dynamic export, not a filename or a diagnostic
    # string. Renamed/path-loaded legitimate SDKs count; the game helper's dlsym
    # string does not make the helper itself a Gua SDK load.
    if image[:6]!=b'\x7fELF\x02\x01' or len(image)<64:return False
    try:
        offset=struct.unpack_from('<Q',image,40)[0]
        size,count=struct.unpack_from('<HH',image,58)
        if size!=64 or not 0<count<8192 or offset+size*count>len(image):return False
        sections=[struct.unpack_from('<IIQQQQIIQQ',image,offset+i*size) for i in range(count)]
        for section in sections:
            if section[1]!=11:continue # SHT_DYNSYM
            if section[6]>=count or section[9]!=24 or section[5]%24:return False
            strings=sections[section[6]]
            if strings[4]+strings[5]>len(image) or section[4]+section[5]>len(image):return False
            names=image[strings[4]:strings[4]+strings[5]]
            for position in range(section[4],section[4]+section[5],24):
                name,info,_other,index,_value,_size=struct.unpack_from('<IBBHQQ',image,position)
                if index==0 or info>>4 not in (1,2) or info&15 not in (2,10):continue
                end=names.find(b'\0',name)
                if end>=name and names[name:end]==b'gua_godot_library_init':return True
    except (struct.error,IndexError):return False
    return False

class Registers(ctypes.Structure):
    _fields_=[(name,ctypes.c_ulonglong) for name in 'r15 r14 r13 r12 rbp rbx r11 r10 r9 r8 rax rcx rdx rsi rdi orig_rax rip cs eflags rsp ss fs_base gs_base ds es fs gs'.split()]

libc=ctypes.CDLL(None,use_errno=True)
libc.ptrace.argtypes=[ctypes.c_ulong,ctypes.c_ulong,ctypes.c_void_p,ctypes.c_void_p]
libc.ptrace.restype=ctypes.c_long

def ptrace(request,pid,address=0,data=0):
    ctypes.set_errno(0)
    result=libc.ptrace(request,pid,ctypes.c_void_p(address),ctypes.c_void_p(data))
    error=ctypes.get_errno()
    if result==-1 and error:raise OSError(error,os.strerror(error))
    return result

def string_at(pid,address):
    value=bytearray()
    for offset in range(0,4096,8):
        word=ptrace(2,pid,address+offset) & ((1<<64)-1) # PEEKDATA
        chunk=word.to_bytes(8,'little')
        if 0 in chunk:
            value.extend(chunk[:chunk.index(0)]);return value.decode('utf-8','strict')
        value.extend(chunk)
    raise ValueError('Unbounded loader pathname.')

def observe(executable,log,env,before_sdk_load=None,timeout=45,original_loader_path=None):
    executable=Path(executable).resolve()
    def traced_child():ptrace(0,0) # TRACEME; exec produces the first stop.
    def expired(_signal,_frame):raise TimeoutError('Owned Linux game observer timed out.')
    old=signal.signal(signal.SIGALRM,expired)
    signal.setitimer(signal.ITIMER_REAL,timeout)
    child=None;native={};inspected={};pending_map=None;barriers=0;entry=True;original_opens=0;deliver=0
    try:
        with Path(log).open('w') as output:
            child=subprocess.Popen([str(executable),'--headless'],cwd=executable.parent,env=env,stdout=output,stderr=subprocess.STDOUT,preexec_fn=traced_child)
            _,status=os.waitpid(child.pid,0)
            if not os.WIFSTOPPED(status):raise RuntimeError('No independent exec stop.')
            ptrace(0x4200,child.pid,0,0x1|0x100000) # SETOPTIONS: TRACESYSGOOD|EXITKILL
            while True:
                ptrace(24,child.pid,0,deliver);deliver=0 # SYSCALL
                _,status=os.waitpid(child.pid,0)
                if os.WIFEXITED(status) or os.WIFSIGNALED(status):
                    child.returncode=os.waitstatus_to_exitcode(status);break
                stopped=os.WSTOPSIG(status)
                if stopped!=signal.SIGTRAP|0x80:
                    if stopped not in (signal.SIGTRAP,signal.SIGSTOP):
                        deliver=stopped # Never suppress a real child crash.
                    continue
                if entry:
                    pending_map=None
                    registers=Registers();ptrace(12,child.pid,0,ctypes.addressof(registers))
                    if registers.orig_rax==257: # openat, BEFORE loader opens SDK.
                        path=string_at(child.pid,registers.rsi)
                        if path.startswith('/proc/self/fd/'):
                            fd=Path(f'/proc/{child.pid}/fd')/path.rsplit('/',1)[1]
                            try:target=os.readlink(fd)
                            except FileNotFoundError:target=''
                            if target.startswith('/memfd:gua-game-verified'):
                                barriers+=1
                                if before_sdk_load is not None:
                                    if native:raise AssertionError('SDK already mapped before check/load barrier.')
                                    before_sdk_load(child.pid,fd)
                        elif original_loader_path is not None and (Path(path) if Path(path).is_absolute() else executable.parent/path).resolve()==Path(original_loader_path).resolve():
                            original_opens+=1
                            # Disposable old-path mutant only: first open copies
                            # the source, second open is the unprotected loader.
                            if original_opens==2:
                                barriers+=1
                                if before_sdk_load is not None:before_sdk_load(child.pid,None)
                    if registers.orig_rax==9: # mmap: observe actual SDK image.
                        fd=Path(f'/proc/{child.pid}/fd/{registers.r8}')
                        try:target=os.readlink(fd)
                        except FileNotFoundError:target=''
                        if target:
                            stat=fd.stat();key=(stat.st_dev,stat.st_ino)
                            if key not in inspected:
                                try:image=fd.read_bytes()
                                except (OSError,ValueError):image=b''
                                inspected[key]=hashlib.sha256(image).hexdigest() if is_gua_image(image) else None
                            if inspected[key] is not None:pending_map=(key,inspected[key])
                elif pending_map is not None:
                    registers=Registers();ptrace(12,child.pid,0,ctypes.addressof(registers))
                    if ctypes.c_longlong(registers.rax).value>=0:
                        native[pending_map[0]]=pending_map[1]
                    pending_map=None
                entry=not entry
        return {'exitCode':child.returncode,'sdkMappings':len(native),'sdkHashes':list(native.values()),'loaderBarriers':barriers}
    finally:
        signal.setitimer(signal.ITIMER_REAL,0);signal.signal(signal.SIGALRM,old)
        if child is not None and child.returncode is None:
            # Only this owned GAME is traced. No Editor is ever signalled.
            try:os.kill(child.pid,signal.SIGKILL)
            except ProcessLookupError:pass
            try:_,status=os.waitpid(child.pid,0);child.returncode=os.waitstatus_to_exitcode(status)
            except ChildProcessError:pass
