using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Contracts;

/// <summary>Only the immutable preflight evidence needed by the session lifecycle.</summary>
internal sealed record SessionEnvironment(bool HasIssues, bool HasBlockingIssues, string Summary)
{
    internal static SessionEnvironment Empty { get; } = new(false, false, string.Empty);
}

/// <summary>Windows effects used by the lifecycle owner; implementations own background native work.</summary>
internal interface IChildSessionPlatform
{
    bool IsChildSessionsEnabled();
    uint? TryGetChildSessionId();
    bool IsSessionActive(uint sessionId);
    void EnableChildSessions();
    void DisableChildSessions();
    (string State, string StartType) QueryTermService();
    Task<bool> TryStartTermServiceIfStoppedAsync(CancellationToken cancellationToken);
    Task<SessionEnvironment> CollectConnectionIssuesAsync();
    Task<uint?> RequestLogoffAsync(uint sessionId);
    Task LaunchAsync(uint sessionId, string path, string arguments, bool elevated, CancellationToken cancellationToken);
    bool IsLeastPrivilegeEnforced();
    void RecordConnectionFailure(int error, int? extendedError, string message);
    void ClearConnectionFailure();
}
