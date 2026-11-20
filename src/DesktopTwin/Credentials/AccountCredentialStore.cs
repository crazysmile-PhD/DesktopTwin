using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DesktopTwin.Models;

namespace DesktopTwin.Credentials;

/// <summary>
/// Account-scoped credential policy. Platform encryption and persistence are injected.
/// The injected update serializes writers; background readers finish using an immutable snapshot.
/// </summary>
internal sealed class AccountCredentialStore(
    Func<AutomationSettings> readSettings,
    Func<string> currentUser,
    Func<string, string?> protect,
    Func<string, string?> unprotect,
    Action<Func<AutomationSettings, AutomationSettings>> update)
{
    internal string UserName => GetUserName(readSettings());
    private string GetUserName(AutomationSettings settings) => string.IsNullOrWhiteSpace(settings.AutoLoginUserName)
        ? currentUser() : settings.AutoLoginUserName.Trim();
    internal string AccountKey => UserName.ToLowerInvariant();

    internal IReadOnlyList<string> StoredAccountNames
    {
        get
        {
            var names = new List<string>();
            foreach (var key in readSettings().AutoLoginPasswords.Keys)
                if (!string.IsNullOrWhiteSpace(key)) names.Add(key);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }

    internal bool HasStoredPasswordFor(string userName) => FindKey(readSettings().AutoLoginPasswords, userName) is not null;

    internal bool SavePassword(string plainText)
    {
        var cipher = protect(plainText);
        if (string.IsNullOrWhiteSpace(cipher)) return false;
        var user = UserName;
        update(settings => settings with { AutoLoginPasswords = CopyPasswords(settings).Remove(user).Add(user, cipher) });
        return true;
    }

    internal void ClearPassword()
    {
        var user = UserName;
        if (!HasStoredPasswordFor(user)) return;
        update(settings => settings with { AutoLoginPasswords = CopyPasswords(settings).Remove(user) });
    }

    internal string? ReadPassword()
    {
        var settings = readSettings();
        var snapshot = settings.AutoLoginPasswords;
        var key = FindKey(snapshot, GetUserName(settings));
        if (key is null || string.IsNullOrWhiteSpace(snapshot[key])) return null;
        return unprotect(snapshot[key]);
    }

    private static ImmutableDictionary<string, string> CopyPasswords(AutomationSettings settings)
    {
        var next = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.AutoLoginPasswords) next[pair.Key] = pair.Value;
        return next.ToImmutable();
    }

    private static string? FindKey(ImmutableDictionary<string, string> snapshot, string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        foreach (var key in snapshot.Keys)
            if (string.Equals(key, userName.Trim(), StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }
}
