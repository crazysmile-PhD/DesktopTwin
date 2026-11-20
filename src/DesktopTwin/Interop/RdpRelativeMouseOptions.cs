using System;
using System.Runtime.InteropServices;
using DesktopTwin.Services;

namespace DesktopTwin.Interop;

/// <summary>连接前配置原生相对鼠标；不支持时尽力恢复绝对输入，继续普通连接。</summary>
internal static class RdpRelativeMouseOptions
{
    /// <summary>返回两项设置是否均被接受；不代表服务器已协商或游戏已收到相对输入。</summary>
    internal static bool Apply(bool enabled, Action<bool> allowRelativeMode, Action<bool> setRelativeMode)
    {
        bool TryWrite(string property, Action<bool> write, bool value)
        {
            try
            {
                write(value);
                return true;
            }
            catch (Exception exception) when (exception.GetBaseException() is COMException
                or MissingMemberException or InvalidCastException or ArgumentException)
            {
                AppLog.Warn($"RDP 可选鼠标属性 {property}={value} 不被接受。", exception.GetBaseException());
                return false;
            }
        }

        bool RestoreAbsoluteMode()
        {
            // 同一个控件会重用于设置重连：关闭时必须撤销上一连接的两项设置。
            // 不短路，避免第一项失败后遗漏另一项的清理。
            var modeReset = TryWrite("RelativeMouseMode", setRelativeMode, false);
            var allowReset = TryWrite("AllowRelativeMouseMode", allowRelativeMode, false);
            return modeReset && allowReset;
        }

        if (!enabled)
        {
            return RestoreAbsoluteMode();
        }

        // AllowRelativeMouseMode 自 Windows 11 24H2 提供。先确认该能力，
        // 不在旧系统上单独启用只适用于部分场景的历史 RelativeMouseMode。
        if (TryWrite("AllowRelativeMouseMode", allowRelativeMode, true)
            && TryWrite("RelativeMouseMode", setRelativeMode, true))
        {
            return true;
        }

        var restored = RestoreAbsoluteMode();
        AppLog.Info($"相对鼠标请求未完整接受，恢复绝对输入属性接受={restored}。继续普通 RDP 连接。");
        return false;
    }
}
