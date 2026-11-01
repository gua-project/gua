using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;

if(args.Length<2) throw new ArgumentException("native path symbol | managed path");
if(args[0]=="native") {
    if(args.Length!=3) throw new ArgumentException("native path symbol");
    nint library=NativeLibrary.Load(Path.GetFullPath(args[1]));
    try {
        var copy=Marshal.GetDelegateForFunctionPointer<CopyJson>(NativeLibrary.GetExport(library,args[2]));
        int size=copy(0,0);
        if(size<=0 || size>65536) throw new InvalidDataException("Invalid build identity size");
        nint data=Marshal.AllocHGlobal(size);
        try {
            if(copy(data,size)!=size) throw new InvalidDataException("Build identity changed");
            string text=Marshal.PtrToStringUTF8(data) ?? throw new InvalidDataException("Missing build identity");
            using var parsed=JsonDocument.Parse(text);
            Console.WriteLine(parsed.RootElement.GetRawText());
        } finally { Marshal.FreeHGlobal(data); }
    } finally { NativeLibrary.Free(library); }
} else if(args[0]=="managed") {
    using var stream=File.OpenRead(args[1]); using var pe=new PEReader(stream);
    var reader=pe.GetMetadataReader();
    var values=new Dictionary<string,object?>();
    values["remoteStartReference"]=reader.MemberReferences.Any(h=>reader.GetString(reader.GetMemberReference(h).Name)=="StartInspectorBridge");
    values["webInstallImport"]=reader.MethodDefinitions.Any(h=>reader.GetString(reader.GetMethodDefinition(h).Name)=="GuaUnityWebInstall");
    foreach(var handle in reader.TypeDefinitions) {
        var type=reader.GetTypeDefinition(handle);
        if(reader.GetString(type.Namespace)!="Gua.Unity") continue;
        if(reader.GetString(type.Name)=="GuaUnityCompiledBuild") {
            foreach(var fieldHandle in type.GetFields()) {
                var field=reader.GetFieldDefinition(fieldHandle); var constant=reader.GetConstant(field.GetDefaultValue());
                values[reader.GetString(field.Name)]=reader.GetBlobReader(constant.Value).ReadBoolean();
            }
        }
        if(reader.GetString(type.Name)=="GuaUnityRuntime")
            values["remoteCommandHandler"]=type.GetMethods().Any(m=>reader.GetString(reader.GetMethodDefinition(m).Name)=="HandleWebRequest");
    }
    if(!values.ContainsKey("RemoteEnabled") || !values.ContainsKey("PlayerOnly")) throw new InvalidDataException("Missing compiled managed variant");
    Console.WriteLine(JsonSerializer.Serialize(values));
} else throw new ArgumentException("Unknown probe mode");

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int CopyJson(nint output,int size);
