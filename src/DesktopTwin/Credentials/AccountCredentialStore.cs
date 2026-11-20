using System;
using System.Collections.Generic;
using DesktopTwin.Models;

namespace DesktopTwin.Credentials;

/// <summary>
/// Account-scoped credential policy. Platform encryption and persistence are injected.
/// UI-thread writes replace the table; background readers may finish using an older snapshot.
/// </summary>
internal sealed class AccountCredentialStore(
    AutomationSettings settings,
    Func<string> currentUser,
    Func<string, string?> protect,
    Func<string, string?> unprotect,
    Action changed)
{
    internal string UserName => string.IsNullOrWhiteSpace(settings.AutoLoginUserName)
        ? currentUser() : settings.AutoLoginUserName.Trim();
    internal string AccountKey => UserName.ToLowerInvariant();

    internal IReadOnlyList<string> StoredAccountNames
    {
        get
        {
            var names = new List<string>();
            foreach (var key in settings.AutoLoginPasswords.Keys)
                if (!string.IsNullOrWhiteSpace(key)) names.Add(key);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }

    internal bool HasStoredPasswordFor(string userName) => FindKey(settings.AutoLoginPasswords, userName) is not null;

    internal bool SavePassword(string plainText)
    {
        var cipher = protect(plainText);
        if (string.IsNullOrWhiteSpace(cipher)) return false;
        var next = CopyPasswords();
        next.Remove(UserName);
        next[UserName] = cipher;
        settings.AutoLoginPasswords = next;
        changed();
        return true;
    }

    internal void ClearPassword()
    {
        var next = CopyPasswords();
        if (!next.Remove(UserName)) return;
        settings.AutoLoginPasswords = next;
        changed();
    }

    internal string? ReadPassword()
    {
        var snapshot = settings.AutoLoginPasswords;
        var key = FindKey(snapshot, UserName);
        if (key is null || string.IsNullOrWhiteSpace(snapshot[key])) return null;
        return unprotect(snapshot[key]);
    }

    private Dictionary<string, string> CopyPasswords()
    {
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.AutoLoginPasswords) next[pair.Key] = pair.Value;
        return next;
    }

    private static string? FindKey(Dictionary<string, string> snapshot, string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        foreach (var key in snapshot.Keys)
            if (string.Equals(key, userName.Trim(), StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }
}
