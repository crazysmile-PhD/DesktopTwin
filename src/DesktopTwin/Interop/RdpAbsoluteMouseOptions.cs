using System;
using System.Runtime.InteropServices;
using DesktopTwin.Services;

namespace DesktopTwin.Interop;

/// <summary>The session receiver owns relative motion; native RDP always uses absolute coordinates.</summary>
internal static class RdpAbsoluteMouseOptions
{
    /// <summary>Attempts both resets even when one optional property is unsupported.</summary>
    internal static bool Reset(Action<bool> allowRelativeMode, Action<bool> setRelativeMode)
    {
        var modeReset = TryReset("RelativeMouseMode", setRelativeMode);
        var allowReset = TryReset("AllowRelativeMouseMode", allowRelativeMode);
        return modeReset && allowReset;
    }

    private static bool TryReset(string property, Action<bool> write)
    {
        try { write(false); return true; }
        catch (Exception error) when (error.GetBaseException() is COMException
            or MissingMemberException or InvalidCastException or ArgumentException)
        {
            AppLog.Warn($"RDP 可选鼠标属性 {property}=false 不被接受。", error.GetBaseException());
            return false;
        }
    }
}
