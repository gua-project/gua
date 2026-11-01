using System.Runtime.InteropServices;
using System.Text;

namespace Gua.Core;

/// <summary>Immutable native build availability; does not grant session permissions.</summary>
public static class GuaBuildInfo
{
    public static unsafe string GetJson()
    {
        int size = Native.gua_copy_build_info_json(null, 0);
        if (size <= 0 || size > 65536) throw new InvalidOperationException("Invalid Gua build identity.");
        var bytes = new byte[size];
        fixed (byte* buffer = bytes) {
            if (Native.gua_copy_build_info_json(buffer, size) != size)
                throw new InvalidOperationException("Unstable Gua build identity.");
        }
        return Encoding.UTF8.GetString(bytes, 0, size - 1);
    }
}

internal static partial class Native
{
#if GUA_STATIC_LINK
    private const string BuildInfoLibrary = "__Internal";
#else
    private const string BuildInfoLibrary = "gua";
#endif
    [DllImport(BuildInfoLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int gua_copy_build_info_json(byte* output, int size);
}
