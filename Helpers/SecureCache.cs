using System;
using System.Security.Cryptography;

namespace SteamPluginManager.Helpers
{
    public static class SecureCache
    {
        // Protect data using Windows DPAPI tied to current user
        public static byte[] Protect(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        }

        public static byte[] Unprotect(byte[] encryptedData)
        {
            if (encryptedData == null) throw new ArgumentNullException(nameof(encryptedData));
            return ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
        }
    }
}
