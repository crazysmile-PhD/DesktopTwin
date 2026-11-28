using System;
using System.Drawing;

namespace DesktopTwin.Interop;

/// <summary>Coalesces accepted requests while allowing rejected or unconfirmed display settings to retry.</summary>
internal sealed class RdpDisplayRequestController(Action<RdpDisplaySettings> apply, TimeProvider timeProvider)
{
    internal static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(2);
    private RdpDisplaySettings? _accepted;
    private long _requestedAt;

    /// <summary>Returns whether a native request was sent; a failed call never becomes cached success.</summary>
    internal bool Request(RdpDisplaySettings settings, Size? reportedSize)
    {
        if (_accepted == settings && (reportedSize == settings.DesktopSize
            || timeProvider.GetElapsedTime(_requestedAt) < ConfirmationTimeout)) return false;

        // A failing COM call can leave native state uncertain, including the previous setting.
        _accepted = null;
        apply(settings);
        _accepted = settings;
        _requestedAt = timeProvider.GetTimestamp();
        return true;
    }

    /// <summary>Each native connection owns its own accepted display settings.</summary>
    internal void Reset() => _accepted = null;
}
