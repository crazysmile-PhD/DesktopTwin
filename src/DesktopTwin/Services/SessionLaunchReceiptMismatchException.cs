using System;

namespace DesktopTwin.Services;

/// <summary>Creation was reported, but the observed target did not match every receipt field.</summary>
internal sealed class SessionLaunchReceiptMismatchException(string evidence)
    : InvalidOperationException("启动回执与实际目标进程不匹配，未确认启动成功。" + evidence);
