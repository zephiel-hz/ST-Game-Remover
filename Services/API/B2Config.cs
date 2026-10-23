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
    /// <summary>
    /// S3-compatible client untuk Backblaze B2 — digunakan khusus untuk OnlineFix storage.
    /// GameBypass tetap menggunakan R2Config.
    /// </summary>
    public static class B2Config
    {
        public static string B2Endpoint => ServiceConfiguration.Current.BackblazeB2.Endpoint;
        public static string B2AccessKey => ServiceConfiguration.Current.BackblazeB2.AccessKey;
        public static string B2SecretKey => ServiceConfiguration.Current.BackblazeB2.SecretKey;
        public static string B2Bucket => ServiceConfiguration.Current.BackblazeB2.Bucket;
        private static string B2Region => ServiceConfiguration.Current.BackblazeB2.Region;

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

        // List file di bucket B2 (private bucket, pakai signature)
        public static async Task<List<B2FileInfo>> ListFilesAsync(string folderPrefix = "onlinefix/")
        {
            var files = new List<B2FileInfo>();
            var service = "s3";
            var region = !string.IsNullOrWhiteSpace(B2Region) ? B2Region : "us-east-005";
            var host = new Uri(B2Endpoint).Host;
            var client = SharedHttpClient.StorageInstance;

            string? continuationToken = null;
            bool isTruncated = false;

            do
            {
                var now = DateTime.UtcNow;
                var amzDate = now.ToString("yyyyMMddTHHmmssZ");
                var dateStamp = now.ToString("yyyyMMdd");
                var canonicalUri = $"/{B2Bucket}";
                var canonicalQueryString = "list-type=2";
                if (!string.IsNullOrEmpty(continuationToken))
                {
                    canonicalQueryString += $"&continuation-token={WebUtility.UrlEncode(continuationToken)}";
                }

                var payloadHash = ToHex(HashSHA256(""));
                var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
                var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
                var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
                var algorithm = "AWS4-HMAC-SHA256";
                var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
                var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
                var signingKey = GetSignatureKey(B2SecretKey, dateStamp, region, service);
                var signature = ToHex(HmacSHA256(signingKey, stringToSign));
                var authorizationHeader = $"{algorithm} Credential={B2AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
                var url = $"{B2Endpoint}/{B2Bucket}?{canonicalQueryString}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("x-amz-date", amzDate);
                req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
                req.Headers.Add("x-amz-content-sha256", payloadHash);

                var response = await client.SendAsync(req);
                var xml = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"B2 ListFilesAsync error: {response.StatusCode} {xml}");
                    throw new Exception($"B2 ListFilesAsync error: {response.StatusCode} {xml}");
                }

                int pos = 0;
                while ((pos = xml.IndexOf("<Contents>", pos, StringComparison.OrdinalIgnoreCase)) != -1)
                {
                    int keyStart = xml.IndexOf("<Key>", pos, StringComparison.OrdinalIgnoreCase);
                    if (keyStart == -1) break;
                    keyStart += 5;
                    int keyEnd = xml.IndexOf("</Key>", keyStart, StringComparison.OrdinalIgnoreCase);
                    if (keyEnd == -1) break;
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

                    DateTime? lastModified = null;
                    int modStart = xml.IndexOf("<LastModified>", keyEnd, StringComparison.OrdinalIgnoreCase);
                    if (modStart != -1)
                    {
                        modStart += 14;
                        int modEnd = xml.IndexOf("</LastModified>", modStart, StringComparison.OrdinalIgnoreCase);
                        if (modEnd != -1)
                        {
                            string modText = xml.Substring(modStart, modEnd - modStart);
                            if (DateTime.TryParse(modText, out var parsedDt))
                            {
                                lastModified = parsedDt;
                            }
                        }
                    }

                    if (key.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)
                        && key != folderPrefix
                        && !key.EndsWith("/"))
                    {
                        files.Add(new B2FileInfo
                        {
                            Key = key,
                            Url = $"{B2Endpoint}/{B2Bucket}/{key}",
                            Size = size,
                            LastModified = lastModified
                        });
                    }

                    pos = keyEnd;
                }

                isTruncated = xml.Contains("<IsTruncated>true</IsTruncated>", StringComparison.OrdinalIgnoreCase);
                if (isTruncated)
                {
                    int tokenStart = xml.IndexOf("<NextContinuationToken>", StringComparison.OrdinalIgnoreCase);
                    if (tokenStart != -1)
                    {
                        tokenStart += 23;
                        int tokenEnd = xml.IndexOf("</NextContinuationToken>", tokenStart, StringComparison.OrdinalIgnoreCase);
                        if (tokenEnd != -1)
                        {
                            continuationToken = xml.Substring(tokenStart, tokenEnd - tokenStart);
                        }
                        else isTruncated = false;
                    }
                    else isTruncated = false;
                }
            } while (isTruncated);

            return files;
        }

        // Upload file ke B2 dengan streaming dan progress reporting
        public static async Task<string> UploadFileAsync(
            string localFilePath,
            string remoteKey,
            string contentType = "application/zip",
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var fileInfo = new FileInfo(localFilePath);
            if (!fileInfo.Exists)
                throw new FileNotFoundException($"File to upload was not found: {localFilePath}");

            var service = "s3";
            var region = !string.IsNullOrWhiteSpace(B2Region) ? B2Region : "us-east-005";
            var host = new Uri(B2Endpoint).Host;
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = now.ToString("yyyyMMdd");
            var canonicalUri = $"/{B2Bucket}/{remoteKey}";
            var canonicalQueryString = "";

            var canonicalHeaders = $"content-type:{contentType}\nhost:{host}\nx-amz-content-sha256:UNSIGNED-PAYLOAD\nx-amz-date:{amzDate}\n";
            var signedHeaders = "content-type;host;x-amz-content-sha256;x-amz-date";
            var payloadHash = "UNSIGNED-PAYLOAD";

            var canonicalRequest = $"PUT\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var algorithm = "AWS4-HMAC-SHA256";
            var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
            var signingKey = GetSignatureKey(B2SecretKey, dateStamp, region, service);
            var signature = ToHex(HmacSHA256(signingKey, stringToSign));
            var authorizationHeader = $"{algorithm} Credential={B2AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

            var url = $"{B2Endpoint}/{B2Bucket}/{remoteKey}";
            var client = SharedHttpClient.StorageInstance;

            using var fileStream = File.OpenRead(localFilePath);
            var totalBytes = fileStream.Length;

            using var progressStream = new StorageProgressStream(fileStream, totalBytes, sentBytes =>
            {
                if (progress != null && totalBytes > 0)
                {
                    int pct = (int)((sentBytes * 100L) / totalBytes);
                    progress.Report(pct);
                }
            });

            using var content = new StreamContent(progressStream, 81920);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

            using var req = new HttpRequestMessage(HttpMethod.Put, url);
            req.Content = content;
            req.Headers.Add("x-amz-date", amzDate);
            req.Headers.Add("x-amz-content-sha256", "UNSIGNED-PAYLOAD");
            req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);

            using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"B2 Upload failed with status {response.StatusCode}: {errorBody}");
            }

            return $"{B2Endpoint}/{B2Bucket}/{remoteKey}";
        }

        // Download streaming file dari B2 langsung ke path lokal di disk
        public static async Task DownloadFileToPathAsync(
            string key,
            string destinationFilePath,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var service = "s3";
            var region = !string.IsNullOrWhiteSpace(B2Region) ? B2Region : "us-east-005";
            var host = new Uri(B2Endpoint).Host;
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = now.ToString("yyyyMMdd");
            var canonicalUri = $"/{B2Bucket}/{key}";
            var canonicalQueryString = "";
            var payloadHash = ToHex(HashSHA256(""));
            var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
            var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
            var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var algorithm = "AWS4-HMAC-SHA256";
            var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
            var signingKey = GetSignatureKey(B2SecretKey, dateStamp, region, service);
            var signature = ToHex(HmacSHA256(signingKey, stringToSign));
            var authorizationHeader = $"{algorithm} Credential={B2AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
            var url = $"{B2Endpoint}/{B2Bucket}/{key}";
            var client = SharedHttpClient.StorageInstance;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-amz-date", amzDate);
            req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
            req.Headers.Add("x-amz-content-sha256", payloadHash);

            var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"B2 Download error: {response.StatusCode} {err}");
            }

            var contentLength = response.Content.Headers.ContentLength ?? -1L;
            var tempPath = destinationFilePath + ".tmp";
            string? dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                    totalRead += read;
                    if (contentLength > 0 && progress != null)
                    {
                        int percent = (int)((totalRead * 100L) / contentLength);
                        progress.Report(percent);
                    }
                }
            }

            if (File.Exists(destinationFilePath))
            {
                File.Delete(destinationFilePath);
            }
            File.Move(tempPath, destinationFilePath);
            progress?.Report(100);
        }

        // Download file dari B2
        public static async Task<byte[]> DownloadFileAsync(string key, IProgress<int>? progress = null, CancellationToken cancellationToken = default, Func<Task>? pauseCheck = null)
        {
            var service = "s3";
            var region = !string.IsNullOrWhiteSpace(B2Region) ? B2Region : "us-east-005";
            var host = new Uri(B2Endpoint).Host;
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = now.ToString("yyyyMMdd");
            var canonicalUri = $"/{B2Bucket}/{key}";
            var canonicalQueryString = "";
            var payloadHash = ToHex(HashSHA256(""));
            var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
            var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
            var canonicalRequest = $"GET\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var algorithm = "AWS4-HMAC-SHA256";
            var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
            var signingKey = GetSignatureKey(B2SecretKey, dateStamp, region, service);
            var signature = ToHex(HmacSHA256(signingKey, stringToSign));
            var authorizationHeader = $"{algorithm} Credential={B2AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
            var url = $"{B2Endpoint}/{B2Bucket}/{key}";
            var client = SharedHttpClient.StorageInstance;
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-amz-date", amzDate);
            req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
            req.Headers.Add("x-amz-content-sha256", payloadHash);

            // Stream the response so we can report progress
            var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"B2 DownloadFileAsync error: {response.StatusCode} {err}");
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
                progress.Report(100);
            }
            else if (progress != null)
            {
                progress.Report(100);
            }

            return ms.ToArray();
        }

        // Upload bytes ke B2 (mis. foto profil pfp/{userId}.jpg)
        public static async Task<string> UploadBytesAsync(string key, byte[] data, string contentType = "image/jpeg", CancellationToken cancellationToken = default)
        {
            var service = "s3";
            var region = !string.IsNullOrWhiteSpace(B2Region) ? B2Region : "us-east-005";
            var host = new Uri(B2Endpoint).Host;
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = now.ToString("yyyyMMdd");
            var canonicalUri = $"/{B2Bucket}/{key}";
            var canonicalQueryString = "";
            var payloadHash = "UNSIGNED-PAYLOAD";
            var canonicalHeaders = $"content-type:{contentType}\nhost:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
            var signedHeaders = "content-type;host;x-amz-content-sha256;x-amz-date";
            var canonicalRequest = $"PUT\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var algorithm = "AWS4-HMAC-SHA256";
            var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
            var signingKey = GetSignatureKey(B2SecretKey, dateStamp, region, service);
            var signature = ToHex(HmacSHA256(signingKey, stringToSign));
            var authorizationHeader = $"{algorithm} Credential={B2AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
            var url = $"{B2Endpoint}/{B2Bucket}/{key}";
            var client = SharedHttpClient.StorageInstance;

            using var content = new ByteArrayContent(data);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

            using var req = new HttpRequestMessage(HttpMethod.Put, url);
            req.Content = content;
            req.Headers.Add("x-amz-date", amzDate);
            req.Headers.Add("x-amz-content-sha256", payloadHash);
            req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);

            var response = await client.SendAsync(req, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"B2 UploadBytesAsync failed: {response.StatusCode} - {errorBody}");
            }

            return $"{B2Endpoint}/{B2Bucket}/{key}";
        }

        // Hapus file dari B2
        public static async Task<bool> DeleteFileAsync(string key, CancellationToken cancellationToken = default)
        {
            try
            {
                var service = "s3";
                var region = !string.IsNullOrWhiteSpace(B2Region) ? B2Region : "us-east-005";
                var host = new Uri(B2Endpoint).Host;
                var now = DateTime.UtcNow;
                var amzDate = now.ToString("yyyyMMddTHHmmssZ");
                var dateStamp = now.ToString("yyyyMMdd");
                var canonicalUri = $"/{B2Bucket}/{key}";
                var canonicalQueryString = "";
                var payloadHash = ToHex(HashSHA256(""));
                var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
                var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
                var canonicalRequest = $"DELETE\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
                var algorithm = "AWS4-HMAC-SHA256";
                var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
                var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
                var signingKey = GetSignatureKey(B2SecretKey, dateStamp, region, service);
                var signature = ToHex(HmacSHA256(signingKey, stringToSign));
                var authorizationHeader = $"{algorithm} Credential={B2AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
                var url = $"{B2Endpoint}/{B2Bucket}/{key}";
                var client = SharedHttpClient.StorageInstance;

                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                req.Headers.Add("x-amz-date", amzDate);
                req.Headers.Add("x-amz-content-sha256", payloadHash);
                req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);

                var response = await client.SendAsync(req, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"B2 DeleteFileAsync error: {ex.Message}");
                return false;
            }
        }

        public class B2FileInfo
        {
            public string Key { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public long Size { get; set; } = -1;
            public DateTime? LastModified { get; set; }
        }
    }
}

