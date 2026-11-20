using System;
using System.Collections.Generic;

namespace DesktopTwin.Services;

/// <summary>Tracks confirmed diagnostic resolutions independently of window rendering.</summary>
internal sealed class DiagnosticIssueHistory
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);

    internal IReadOnlyCollection<string> ResolvedIds => _resolved;
    internal string GetTitle(string id) => _titles.TryGetValue(id, out var title) ? title : id;

    /// <summary>Missing integrity evidence must revoke an earlier resolution, never imply success.</summary>
    internal void Update(IReadOnlyList<ChildSessionEnvironmentCheck.Issue> issues, bool integrityVerified)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            if (issue.IssueSeverity == ChildSessionEnvironmentCheck.Severity.Inactive) continue;
            current.Add(issue.Id);
            _seen.Add(issue.Id);
            _titles[issue.Id] = issue.Title;
            _resolved.Remove(issue.Id);
        }
        foreach (var id in _seen)
        {
            if (current.Contains(id)) continue;
            if (!integrityVerified && string.Equals(id, "termsrv-dll-tampered", StringComparison.Ordinal))
                _resolved.Remove(id);
            else
                _resolved.Add(id);
        }
    }
}
