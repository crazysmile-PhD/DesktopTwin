using System;

namespace DesktopTwin.Input;

/// <summary>Separates passive cursor discovery from the short authorization needed for captured input.</summary>
internal sealed class RelativeInputActivity(Action<bool> authorize, Action requestStatus, TimeProvider timeProvider)
{
    internal static readonly TimeSpan CaptureInterval = TimeSpan.FromMilliseconds(32);
    internal static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMilliseconds(250);
    private long? _lastDiscovery;
    internal TimeSpan? PollInterval { get; private set; }
    internal bool Authorized { get; private set; }

    /// <summary>Foreground discovery cannot authorize input; leaving the foreground stops both activities.</summary>
    internal void Update(bool foreground, bool captured, bool keysHeld)
    {
        Authorized = foreground && (captured || keysHeld);
        authorize(Authorized);
        PollInterval = foreground ? (Authorized ? CaptureInterval : DiscoveryInterval) : null;
        if (!foreground || Authorized)
        {
            _lastDiscovery = null;
            return;
        }
        if (_lastDiscovery is { } last && timeProvider.GetElapsedTime(last) < DiscoveryInterval) return;
        _lastDiscovery = timeProvider.GetTimestamp();
        requestStatus();
    }
}
