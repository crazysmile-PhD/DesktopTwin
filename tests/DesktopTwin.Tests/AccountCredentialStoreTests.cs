using DesktopTwin.Credentials;
using DesktopTwin.Models;

namespace DesktopTwin.Tests;

public sealed class AccountCredentialStoreTests
{
    [Fact]
    public void AccountOperationsAreCaseInsensitiveAndIsolated()
    {
        var settings = new AutomationSettings { AutoLoginUserName = " Alice " };
        settings.AutoLoginPasswords["alice"] = "cipher:old";
        settings.AutoLoginPasswords["Bob"] = "cipher:bob";
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
            var settings = new AutomationSettings { AutoLoginUserName = "Alice" };
            settings.AutoLoginPasswords["Alice"] = "cipher:existing";
            var snapshot = settings.AutoLoginPasswords;
            var store = new AccountCredentialStore(settings, () => "fallback", _ => result,
                _ => throw new InvalidOperationException("must not decrypt during save"),
                () => Assert.Fail("failed protection must not schedule persistence"));
            Assert.False(store.SavePassword("new"));
            Assert.Same(snapshot, settings.AutoLoginPasswords);
            Assert.Equal("cipher:existing", snapshot["Alice"]);
        }
    }

    [Fact]
    public void PublishedTablesDoNotMutateEarlierReaderSnapshots()
    {
        var settings = new AutomationSettings { AutoLoginUserName = "Alice" };
        settings.AutoLoginPasswords = new Dictionary<string, string>
        {
            ["Alice"] = "cipher:old", ["ALICE"] = "cipher:duplicate", ["Bob"] = "cipher:bob"
        };
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
        var settings = new AutomationSettings { AutoLoginUserName = "Alice" };
        var decrypts = 0;
        var store = new AccountCredentialStore(settings, () => "fallback", value => "cipher:" + value,
            value => { decrypts++; return value == "cipher:valid" ? "plain" : null; }, () => { });
        Assert.Null(store.ReadPassword());
        settings.AutoLoginPasswords["Alice"] = " ";
        Assert.Null(store.ReadPassword());
        Assert.Equal(0, decrypts);
        settings.AutoLoginPasswords["Alice"] = "cipher:valid";
        Assert.Equal("plain", store.ReadPassword());
        settings.AutoLoginPasswords["Alice"] = "corrupted";
        Assert.Null(store.ReadPassword());
        Assert.Equal(2, decrypts);
        Assert.DoesNotContain("plain", settings.AutoLoginPasswords.Values);
    }

    [Fact]
    public void EmptyConfiguredUserUsesCurrentIdentityAndNamesAreSorted()
    {
        var settings = new AutomationSettings { AutoLoginUserName = " " };
        settings.AutoLoginPasswords["zoe"] = "cipher:z";
        settings.AutoLoginPasswords["ALICE"] = "cipher:a";
        settings.AutoLoginPasswords[" "] = "ignored";
        var current = "FirstUser";
        var store = new AccountCredentialStore(settings, () => current, _ => null, _ => null, () => { });
        Assert.Equal("FirstUser", store.UserName);
        current = "SecondUser";
        Assert.Equal("seconduser", store.AccountKey);
        Assert.Equal(new[] { "ALICE", "zoe" }, store.StoredAccountNames);
    }

    private static AccountCredentialStore Create(AutomationSettings settings, Action changed) =>
        new(settings, () => "fallback", value => "cipher:" + value,
            value => value["cipher:".Length..], changed);
}
