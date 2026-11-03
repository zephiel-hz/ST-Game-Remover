using System;
using System.IO;
using System.Security.Cryptography;

namespace SteamPluginManager.Services.Security
{
    public static class BackupCryptoService
    {
        public const int SaltSize = 16;

        public static string GetBackupPassword()
        {
            var password = Environment.GetEnvironmentVariable("SPM_BACKUP_PASSWORD");
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Environment variable 'SPM_BACKUP_PASSWORD' is not set.");
            }

            return password;
        }

        public static void EncryptStream(Stream inStream, string outputFile)
        {
            byte[] salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.GenerateIV();
                var key = new Rfc2898DeriveBytes(GetBackupPassword(), salt, 10000, HashAlgorithmName.SHA256).GetBytes(aes.KeySize / 8);
                aes.Key = key;

                using (var outStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    outStream.Write(salt, 0, salt.Length);
                    outStream.Write(aes.IV, 0, aes.IV.Length);
                    using (var cryptoStream = new CryptoStream(outStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        if (inStream.CanSeek)
                        {
                            inStream.Position = 0;
                        }
                        inStream.CopyTo(cryptoStream);
                    }
                }
            }
        }

        public static MemoryStream DecryptStream(string inputFile)
        {
            var memoryStream = new MemoryStream();
            using (var inStream = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] salt = new byte[SaltSize];
                inStream.ReadExactly(salt, 0, salt.Length);

                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    byte[] iv = new byte[aes.IV.Length];
                    inStream.ReadExactly(iv, 0, iv.Length);
                    aes.IV = iv;

                    var key = new Rfc2898DeriveBytes(GetBackupPassword(), salt, 10000, HashAlgorithmName.SHA256).GetBytes(aes.KeySize / 8);
                    aes.Key = key;

                    using (var cryptoStream = new CryptoStream(inStream, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    {
                        cryptoStream.CopyTo(memoryStream);
                    }
                }
            }
            memoryStream.Position = 0;
            return memoryStream;
        }
    }
}
