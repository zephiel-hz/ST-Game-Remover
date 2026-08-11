using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SteamPluginManager
{
    public static class R2Config
    {
        public static string R2Endpoint => ServiceConfiguration.Current.CloudflareR2.Endpoint;
        public static string R2AccessKey => ServiceConfiguration.Current.CloudflareR2.AccessKey;
        public static string R2SecretKey => ServiceConfiguration.Current.CloudflareR2.SecretKey;
        public static string R2Bucket => ServiceConfiguration.Current.CloudflareR2.Bucket;

        // Helper untuk S3 signature v4
        private static byte[] HmacSHA256(byte[] key, string data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }
        private static byte[] HashSHA256(string data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(data));
        }
        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
        {
            var kDate = HmacSHA256(Encoding.UTF8.GetBytes("AWS4" + key), dateStamp);
            var kRegion = HmacSHA256(kDate, regionName);
            var kService = HmacSHA256(kRegion, serviceName);
            var kSigning = HmacSHA256(kService, "aws4_request");
            return kSigning;
        }

        // List file di bucket (private bucket, pakai signature)
        public static async Task<List<R2FileInfo>> ListFilesAsync(string folderPrefix = "gamebypass/")
        {
            var files = new List<R2FileInfo>();
            var service = "s3";
            var region = "auto"; // Cloudflare R2: region 'auto'
            var host = new Uri(R2Endpoint).Host;
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = now.ToString("yyyyMMdd");
            var canonicalUri = $"/{R2Bucket}";
            var canonicalQueryString = "list-type=2";
            var canonicalHeaders = $"host:{host}\nx-amz-date:{amzDate}\n";
            var signedHeaders = "host;x-amz-date";
            var payloadHash = ToHex(HashSHA256(""));
            var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var algorithm = "AWS4-HMAC-SHA256";
            var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
            var signingKey = GetSignatureKey(R2SecretKey, dateStamp, region, service);
            var signature = ToHex(HmacSHA256(signingKey, stringToSign));
            var authorizationHeader = $"{algorithm} Credential={R2AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
            var url = $"{R2Endpoint}/{R2Bucket}?{canonicalQueryString}";
            var client = SharedHttpClient.Instance;
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-amz-date", amzDate);
            req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
            req.Headers.Add("x-amz-content-sha256", payloadHash);
            var response = await client.SendAsync(req);
            var xml = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"R2 ListFilesAsync error: {response.StatusCode} {xml}");
                throw new Exception($"R2 ListFilesAsync error: {response.StatusCode} {xml}");
            }
            int pos = 0;
            while ((pos = xml.IndexOf("<Contents>", pos)) != -1)
            {
                int keyStart = xml.IndexOf("<Key>", pos) + 5;
                int keyEnd = xml.IndexOf("</Key>", keyStart);
                string key = xml.Substring(keyStart, keyEnd - keyStart);

                long size = -1;
                int sizeStart = xml.IndexOf("<Size>", keyEnd, StringComparison.OrdinalIgnoreCase);
                if (sizeStart != -1)
                {
                    sizeStart += 6;
                    int sizeEnd = xml.IndexOf("</Size>", sizeStart, StringComparison.OrdinalIgnoreCase);
                    if (sizeEnd != -1)
                    {
                        var sizeText = xml.Substring(sizeStart, sizeEnd - sizeStart);
                        if (!long.TryParse(sizeText, out size))
                            size = -1;
                    }
                }

                if (key.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)
                    && key != folderPrefix
                    && !key.EndsWith("/"))
                {
                    files.Add(new R2FileInfo
                    {
                        Key = key,
                        Url = $"{R2Endpoint}/{R2Bucket}/{key}",
                        Size = size
                    });
                }

                pos = keyEnd;
            }
            return files;
        }

        // Download file dari R2
        public static async Task<byte[]> DownloadFileAsync(string key, IProgress<int>? progress = null, CancellationToken cancellationToken = default, Func<Task>? pauseCheck = null)
        {
            var service = "s3";
            var region = "auto"; // Cloudflare R2: region 'auto'
            var host = new Uri(R2Endpoint).Host;
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = now.ToString("yyyyMMdd");
            var canonicalUri = $"/{R2Bucket}/{key}";
            var canonicalQueryString = "";
            var canonicalHeaders = $"host:{host}\nx-amz-date:{amzDate}\n";
            var signedHeaders = "host;x-amz-date";
            var payloadHash = ToHex(HashSHA256(""));
            var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var algorithm = "AWS4-HMAC-SHA256";
            var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
            var signingKey = GetSignatureKey(R2SecretKey, dateStamp, region, service);
            var signature = ToHex(HmacSHA256(signingKey, stringToSign));
            var authorizationHeader = $"{algorithm} Credential={R2AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
            var url = $"{R2Endpoint}/{R2Bucket}/{key}";
            var client = SharedHttpClient.Instance;
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-amz-date", amzDate);
            req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
            req.Headers.Add("x-amz-content-sha256", payloadHash);

            // Stream the response so we can report progress
            var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"R2 DownloadFileAsync error: {response.StatusCode} {err}");
            }

            var contentLength = response.Content.Headers.ContentLength ?? -1L;
            using var responseStream = await response.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            bool reportedAny = false;
            while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                // Check if paused and wait if needed
                if (pauseCheck != null)
                {
                    await pauseCheck();
                }

                ms.Write(buffer, 0, read);
                totalRead += read;
                if (contentLength > 0 && progress != null)
                {
                    int percent = (int)((totalRead * 100L) / contentLength);
                    progress.Report(percent);
                    reportedAny = true;
                }
            }
            if (progress != null && !reportedAny)
            {
                // If server didn't send content-length, just report 100 at end
                progress.Report(100);
            }
            else if (progress != null)
            {
                progress.Report(100);
            }

            return ms.ToArray();
        }

        public class R2FileInfo
        {
            public string Key { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public long Size { get; set; } = -1;
        }
    }
}
