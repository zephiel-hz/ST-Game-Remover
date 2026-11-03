using System;
using System.Security.Cryptography;
using System.Text;

namespace SteamPluginManager.Services.Security
{
    public static class AwsSigV4Crypto
    {
        public static byte[] HmacSHA256(byte[] key, string data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        public static byte[] HashSHA256(string data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
        {
            var kDate = HmacSHA256(Encoding.UTF8.GetBytes("AWS4" + key), dateStamp);
            var kRegion = HmacSHA256(kDate, regionName);
            var kService = HmacSHA256(kRegion, serviceName);
            return HmacSHA256(kService, "aws4_request");
        }
    }
}
