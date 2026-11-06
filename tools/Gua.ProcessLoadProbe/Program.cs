using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Test-only, game-independent observation from process creation. Do not attach
// to an existing user process or change debugger/security configuration.
if (!OperatingSystem.IsWindows() || IntPtr.Size != 8 || args.Length < 2) throw new ArgumentException("Windows64: <executable> <result.json> [child arguments]");
string executable = Path.GetFullPath(args[0]);
string result = Path.GetFullPath(args[1]);
string? readyBarrier=null, resumeBarrier=null;
string barrierMessage="GamePolicy: locked files verified before SDK load.";
int childStart=2;
if(args.Length>2 && args[2] is "--pause-before-sdk" or "--pause-before-sdk-open") {
    if(args.Length<5)throw new ArgumentException("The test barrier requires ready/resume paths.");
    readyBarrier=Path.GetFullPath(args[3]);resumeBarrier=Path.GetFullPath(args[4]);childStart=5;
    if(args[2]=="--pause-before-sdk-open")barrierMessage="GamePolicy: game directories pinned before SDK open.";
    if(File.Exists(readyBarrier)||File.Exists(resumeBarrier))throw new ArgumentException("Use fresh barrier files.");
}
var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(args.Skip(childStart)).Select(Quote)));
// DEBUG_PROCESS follows this owned child tree: Godot's console EXE is a wrapper
// that starts the actual engine. Observing only the wrapper cannot prove absence.
if (!CreateProcess(executable, command, 0, 0, false, 0x1 | 0x08000000, 0, null, ref startup, out var process))
    throw new Win32Exception(Marshal.GetLastWin32Error());
var guaLoads = new List<string>();
var guaPaths = new List<string>();
var nativeNames = new List<string>();
var timeline = new List<string>();
bool exited = false;
bool processHandleAutoClosed = false, threadHandleAutoClosed = false;
var active = new HashSet<uint> { process.ProcessId };
var processHandles = new Dictionary<uint, nint> { [process.ProcessId] = process.Process };
var exitCodes = new Dictionary<uint, uint>();
var timer = Stopwatch.StartNew();
try
{
    while (!exited)
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Observed child did not terminate within60seconds.");
        if (!WaitForDebugEventEx(out var item, 1000))
        {
            if (Marshal.GetLastWin32Error() == 121) continue;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        uint continuation = 0x10002;
        try
        {
            if (item.Code == 3) {
                active.Add(item.ProcessId);
                processHandles[item.ProcessId] = item.CreatedProcess;
                if(item.ProcessId == process.ProcessId) {
                    processHandleAutoClosed = item.CreatedProcess == process.Process;
                    threadHandleAutoClosed = item.CreatedThread == process.Thread;
                }
            }
            else if (item.Code == 6) // LOAD_DLL_DEBUG_EVENT
            {
                var path = new StringBuilder(32768);
                uint size = item.File != 0 ? GetFinalPathNameByHandle(item.File, path, (uint)path.Capacity, 0) : 0;
                if (size == 0 || size >= path.Capacity)
                    size = GetModuleFileNameEx(processHandles[item.ProcessId], item.ModuleBase, path, (uint)path.Capacity);
                if (size == 0 || size >= path.Capacity)
                    throw new InvalidOperationException("Could not identify a native load event; absence cannot be established.");
                string name = Path.GetFileName(path.ToString());
                nativeNames.Add(name);
                if(name.StartsWith("GameSecurity_",StringComparison.OrdinalIgnoreCase)) timeline.Add("load:"+name);
                if (name.Equals("gua.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("gua_runtime.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("gua_godot.", StringComparison.OrdinalIgnoreCase)) { guaLoads.Add(name); guaPaths.Add(path.ToString()); timeline.Add("load:"+name); }
            }
            else if (item.Code == 8 && item.DebugStringLength is > 0 and < 512) {
                int length=item.DebugStringLength*(item.DebugStringUnicode!=0?2:1);
                var bytes=new byte[length];
                if(ReadProcessMemory(processHandles[item.ProcessId],item.File,bytes,(nuint)bytes.Length,out var read) && read==(nuint)bytes.Length) {
                    string message=(item.DebugStringUnicode!=0?Encoding.Unicode:Encoding.ASCII).GetString(bytes);
                    int terminator=message.IndexOf('\0');
                    if(terminator>=0) message=message[..terminator];
                    if(message is "GameSource: before first policy call." or "GamePolicy: integrity verification refused." or "GamePolicy: verification succeeded before SDK initialization." or "GamePolicy: locked files verified before SDK load." or "GamePolicy: game directories pinned before SDK open.") timeline.Add(message);
                    if(message==barrierMessage && readyBarrier!=null) {
                        // Debug events suspend this owned GAME before any SDK load.
                        // No test timing hook or environment switch exists in the product.
                        File.WriteAllText(readyBarrier,JsonSerializer.Serialize(new{nativeLoads=guaLoads.Count,stage=args[2]=="--pause-before-sdk-open"?"namespace-before-sdk":"locked-before-sdk"}));
                        var barrierTimer=Stopwatch.StartNew();
                        while(!File.Exists(resumeBarrier)) {
                            if(barrierTimer.Elapsed>TimeSpan.FromSeconds(20))throw new TimeoutException("The controller did not release the owned game barrier.");
                            Thread.Sleep(10);
                        }
                    }
                }
            }
            else if (item.Code == 5) // EXIT_PROCESS_DEBUG_EVENT
            {
                exitCodes.Add(item.ProcessId, item.ExitCode);
                active.Remove(item.ProcessId);
                processHandles.Remove(item.ProcessId);
                exited = active.Count == 0;
                if(exited) {
                    Directory.CreateDirectory(Path.GetDirectoryName(result)!);
                    File.WriteAllText(result, JsonSerializer.Serialize(new { childExitCode = exitCodes[process.ProcessId], processExitCodes = exitCodes.Values, guaNativeLoads = guaLoads, guaNativePaths = guaPaths, nativeModuleNames = nativeNames, timeline }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            else if (item.Code == 1 && item.ExceptionCode != 0x80000003)
                continuation = 0x80010001; // Let the application/runtime handle its exception.
        }
        finally
        {
            if (item.Code is 3 or 6 && item.File != 0) CloseHandle(item.File);
            // Debug-event process/thread handles are closed by ContinueDebugEvent
            // on EXIT_PROCESS/EXIT_THREAD, per the Win32 contract.
            if (!ContinueDebugEvent(item.ProcessId, item.ThreadId, continuation)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
finally
{
    if (!exited) { TerminateProcess(process.Process, 80); WaitForSingleObject(process.Process, 5000); }
    if (!threadHandleAutoClosed || !exited) CloseHandle(process.Thread);
    if (!processHandleAutoClosed || !exited) CloseHandle(process.Process);
}

static string Quote(string value)
{
    var text = new StringBuilder("\"");
    int slashes = 0;
    foreach (char c in value)
    {
        if (c == '\\') { slashes++; continue; }
        text.Append('\\', slashes * (c == '"' ? 2 : 1) + (c == '"' ? 1 : 0));
        text.Append(c); slashes = 0;
    }
    return text.Append('\\', slashes * 2).Append('"').ToString();
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct StartupInfo { public int Size; public nint Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags; public ushort Show, ReservedSize; public nint Reserved2, Input, Output, Error; }
[StructLayout(LayoutKind.Sequential)]
struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
[StructLayout(LayoutKind.Explicit, Size = 176)]
struct DebugEvent
{
    [FieldOffset(0)] public uint Code;
    [FieldOffset(4)] public uint ProcessId;
    [FieldOffset(8)] public uint ThreadId;
    [FieldOffset(16)] public nint File;
    [FieldOffset(16)] public uint ExitCode;
    [FieldOffset(16)] public uint ExceptionCode;
    [FieldOffset(24)] public nint ModuleBase;
    [FieldOffset(24)] public nint CreatedProcess;
    [FieldOffset(32)] public nint CreatedThread;
    [FieldOffset(24)] public ushort DebugStringUnicode;
    [FieldOffset(26)] public ushort DebugStringLength;
}

partial class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WaitForDebugEventEx(out DebugEvent item, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadProcessMemory(nint process,nint address,byte[] bytes,nuint size,out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ContinueDebugEvent(uint process, uint thread, uint status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetFinalPathNameByHandle(nint file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "K32GetModuleFileNameExW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetModuleFileNameEx(nint process, nint module, StringBuilder path, uint length);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint process, uint milliseconds);
}
