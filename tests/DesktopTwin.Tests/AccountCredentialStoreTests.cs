using System.Collections.Immutable;
using DesktopTwin.Credentials;
using DesktopTwin.Models;

namespace DesktopTwin.Tests;

public sealed class AccountCredentialStoreTests
{
    [Fact]
    public void AccountOperationsAreCaseInsensitiveAndIsolated()
    {
        var settings = new State { AutoLoginUserName = " Alice " };
        settings.AutoLoginPasswords = settings.AutoLoginPasswords.SetItem("alice", "cipher:old");
        settings.AutoLoginPasswords = settings.AutoLoginPasswords.SetItem("Bob", "cipher:bob");
        var changes = 0;
        var store = Create(settings, () => changes++);
        Assert.Equal("Alice", store.UserName);
        Assert.Equal("alice", store.AccountKey);
        Assert.True(store.HasStoredPasswordFor(" ALICE "));
        Assert.False(store.HasStoredPasswordFor(" "));
        Assert.False(store.HasStoredPasswordFor("missing"));
        Assert.True(store.SavePassword("new"));
        Assert.Equal(2, settings.AutoLoginPasswords.Count);
        Assert.Equal("cipher:new", settings.AutoLoginPasswords["ALICE"]);
        Assert.Equal("cipher:bob", settings.AutoLoginPasswords["bob"]);
        Assert.Equal(new[] { "Alice", "Bob" }, store.StoredAccountNames);
        Assert.Equal("new", store.ReadPassword());
        store.ClearPassword();
        Assert.False(store.HasStoredPasswordFor("alice"));
        Assert.Single(settings.AutoLoginPasswords);
        Assert.Equal("cipher:bob", settings.AutoLoginPasswords["Bob"]);
        store.ClearPassword();
        Assert.Equal(2, changes);
    }

    [Fact]
    public void EncryptionFailurePreservesExistingSecretAndDoesNotScheduleSave()
    {
        foreach (var result in new string?[] { null, "", " " })
        {
            var settings = new State { AutoLoginUserName = "Alice" };
            settings.AutoLoginPasswords = settings.AutoLoginPasswords.SetItem("Alice", "cipher:existing");
            var snapshot = settings.AutoLoginPasswords;
            var store = new AccountCredentialStore(() => settings.Value, () => "fallback", _ => result,
                _ => throw new InvalidOperationException("must not decrypt during save"),
                _ => Assert.Fail("failed protection must not schedule persistence"));
            Assert.False(store.SavePassword("new"));
            Assert.Same(snapshot, settings.AutoLoginPasswords);
            Assert.Equal("cipher:existing", snapshot["Alice"]);
        }
    }

    [Fact]
    public void PublishedTablesDoNotMutateEarlierReaderSnapshots()
    {
        var settings = new State { AutoLoginUserName = "Alice" };
        settings.AutoLoginPasswords = new Dictionary<string, string>
        {
            ["Alice"] = "cipher:old", ["ALICE"] = "cipher:duplicate", ["Bob"] = "cipher:bob"
        }.ToImmutableDictionary();
        var original = settings.AutoLoginPasswords;
        var store = Create(settings, () => { });
        var reader = original.GetEnumerator();
        Assert.True(reader.MoveNext());
        store.SavePassword("new");
        Assert.True(reader.MoveNext()); // The reader can finish enumerating the earlier table.
        Assert.NotSame(original, settings.AutoLoginPasswords);
        Assert.Equal(3, original.Count);
        Assert.Equal("cipher:old", original["Alice"]);
        Assert.Equal(2, settings.AutoLoginPasswords.Count);
        var saved = settings.AutoLoginPasswords;
        store.ClearPassword();
        Assert.Equal("cipher:new", saved["Alice"]);
        Assert.Single(settings.AutoLoginPasswords);
    }

    [Fact]
    public void ReadsUseCurrentCiphertextAndNeverCachePlaintext()
    {
        var settings = new State { AutoLoginUserName = "Alice" };
        var decrypts = 0;
        var store = new AccountCredentialStore(() => settings.Value, () => "fallback", value => "cipher:" + value,
            value => { decrypts++; return value == "cipher:valid" ? "plain" : null; }, change => settings.Value = change(settings.Value));
        Assert.Null(store.ReadPassword());
        settings.AutoLoginPasswords = settings.AutoLoginPasswords.SetItem("Alice", " ");
        Assert.Null(store.ReadPassword());
        Assert.Equal(0, decrypts);
        settings.AutoLoginPasswords = settings.AutoLoginPasswords.SetItem("Alice", "cipher:valid");
        Assert.Equal("plain", store.ReadPassword());
        settings.AutoLoginPasswords = settings.AutoLoginPasswords.SetItem("Alice", "corrupted");
        Assert.Null(store.ReadPassword());
        Assert.Equal(2, decrypts);
        Assert.DoesNotContain("plain", settings.AutoLoginPasswords.Values);
    }

    [Fact]
    public void EmptyConfiguredUserUsesCurrentIdentityAndNamesAreSorted()
    {
        var settings = new State { AutoLoginUserName = " " };
        settings.AutoLoginPasswords = settings.AutoLoginPasswords.SetItem("zoe", "cipher:z");
        settings.AutoLoginPasswords = settings.AutoLoginPasswords.SetItem("ALICE", "cipher:a");
        settings.AutoLoginPasswords = settings.AutoLoginPasswords.SetItem(" ", "ignored");
        var current = "FirstUser";
        var store = new AccountCredentialStore(() => settings.Value, () => current, _ => null, _ => null, change => settings.Value = change(settings.Value));
        Assert.Equal("FirstUser", store.UserName);
        current = "SecondUser";
        Assert.Equal("seconduser", store.AccountKey);
        Assert.Equal(new[] { "ALICE", "zoe" }, store.StoredAccountNames);
    }

    private static AccountCredentialStore Create(State settings, Action changed) =>
        new(() => settings.Value, () => "fallback", value => "cipher:" + value,
            value => value["cipher:".Length..], change => { settings.Value = change(settings.Value); changed(); });

    private sealed class State
    {
        internal AutomationSettings Value { get; set; } = new();
        public string? AutoLoginUserName { init => Value = Value with { AutoLoginUserName = value }; }
        internal ImmutableDictionary<string, string> AutoLoginPasswords
        {
            get => Value.AutoLoginPasswords;
            set => Value = Value with { AutoLoginPasswords = value };
        }
    }
}
