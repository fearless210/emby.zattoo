using System.Text;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Plugin.Configuration;
using MediaBrowser.Controller.Security;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooPasswordStoreTests
{
    [Fact]
    public void NewPassword_IsEncryptedAndMaskedBeforeDisplay()
    {
        var store = new ZattooPasswordStore(new FakeEncryptionManager());
        const string password = "not-a-real-password";

        var stored = store.ProtectSubmittedValue(password, string.Empty);

        Assert.DoesNotContain(password, stored, StringComparison.Ordinal);
        Assert.Equal(password, store.Unprotect(stored));
        Assert.Equal(ZattooPasswordStore.DisplayMask, store.GetDisplayValue(stored));
    }

    [Fact]
    public void DisplayMask_PreservesExistingEncryptedValue()
    {
        var store = new ZattooPasswordStore(new FakeEncryptionManager());
        var stored = store.ProtectSubmittedValue("secret", string.Empty);

        var submitted = store.ProtectSubmittedValue(
            ZattooPasswordStore.DisplayMask,
            stored);

        Assert.Equal(stored, submitted);
    }

    [Fact]
    public void EmptySubmission_ClearsPassword()
    {
        var store = new ZattooPasswordStore(new FakeEncryptionManager());
        var stored = store.ProtectSubmittedValue("secret", string.Empty);

        Assert.Equal(string.Empty, store.ProtectSubmittedValue(string.Empty, stored));
    }

    [Fact]
    public void Unprotect_RejectsPlainTextStorage()
    {
        var store = new ZattooPasswordStore(new FakeEncryptionManager());

        Assert.Throws<ZattooAuthenticationException>(
            () => store.Unprotect("plain-text-must-not-be-accepted"));
    }

    private sealed class FakeEncryptionManager : IEncryptionManager
    {
        public string EncryptString(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        public string DecryptString(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
    }
}
