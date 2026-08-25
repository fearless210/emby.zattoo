using System;
using Emby.Zattoo.Exceptions;
using MediaBrowser.Controller.Security;

namespace Emby.Zattoo.Plugin.Configuration
{
    /// <summary>Encrypts the stored password and prevents browser round-trips.</summary>
    internal sealed class ZattooPasswordStore
    {
        internal const string DisplayMask = "**********";
        private const string EncryptedPrefix = "emby-encrypted:v1:";
        private readonly IEncryptionManager encryptionManager;

        public ZattooPasswordStore(IEncryptionManager encryptionManager)
        {
            this.encryptionManager = encryptionManager
                ?? throw new ArgumentNullException(nameof(encryptionManager));
        }

        public string GetDisplayValue(string storedValue)
        {
            return string.IsNullOrEmpty(storedValue) ? string.Empty : DisplayMask;
        }

        public string ProtectSubmittedValue(string submittedValue, string storedValue)
        {
            if (!string.IsNullOrEmpty(storedValue)
                && string.Equals(submittedValue, DisplayMask, StringComparison.Ordinal))
            {
                return storedValue;
            }

            if (string.IsNullOrEmpty(submittedValue))
            {
                return string.Empty;
            }

            return EncryptedPrefix + encryptionManager.EncryptString(submittedValue);
        }

        public string Unprotect(string storedValue)
        {
            if (string.IsNullOrEmpty(storedValue))
            {
                return string.Empty;
            }

            if (!storedValue.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                throw new ZattooAuthenticationException(
                    "The stored Zattoo password has an unsupported format. Re-enter it in the plugin settings.");
            }

            try
            {
                return encryptionManager.DecryptString(
                    storedValue.Substring(EncryptedPrefix.Length));
            }
            catch (Exception)
            {
                throw new ZattooAuthenticationException(
                    "The stored Zattoo password could not be decrypted. Re-enter it in the plugin settings.");
            }
        }
    }
}
