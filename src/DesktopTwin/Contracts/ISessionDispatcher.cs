using System;
using System.Threading.Tasks;

namespace DesktopTwin.Contracts;

/// <summary>Serializes session state and native control commands on their owning thread.</summary>
internal interface ISessionDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Func<Task> action);
    Task InvokeAsync(Action action);
    void Post(Action action);
}

/// <summary>Credentials and the bounded dialog watcher borrowed by session orchestration.</summary>
internal interface ISessionAutoLogin
{
    bool IsEnabled { get; }
    (string UserName, string Domain, string Password)? GetCredential();
    void StartWatching();
    void StopWatching();
}
