using System.Runtime.InteropServices;

namespace ProxyDiscord.Infrastructure.WinDivert;

public enum WinDivertFilterLayer
{
    Network,
    Socket,
}

public static class WinDivertFilterValidator
{
    public static bool IsValid(string filter, WinDivertFilterLayer layer, out string error)
    {
        var nativeLayer = layer == WinDivertFilterLayer.Socket ? WinDivertLayer.Socket : WinDivertLayer.Network;

        var compiled = WinDivertNative.WinDivertHelperCompileFilter(
            filter, nativeLayer, IntPtr.Zero, 0, out var errorPointer, out var errorPosition);

        if (compiled)
        {
            error = string.Empty;
            return true;
        }

        var message = errorPointer == IntPtr.Zero
            ? "erro desconhecido"
            : Marshal.PtrToStringAnsi(errorPointer) ?? "erro desconhecido";

        error = $"{message} (posição {errorPosition})";
        return false;
    }
}
