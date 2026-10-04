using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPluginManager.Services.ResourcePipeline
{
    public class CloudflareR2StorageService : IStorageService
    {
        public string ProviderName => "Cloudflare R2";

        public async Task<string> UploadFileAsync(
            string localFilePath, 
            string remoteKey, 
            string contentType = "application/zip", 
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default)
        {
            var config = ServiceConfiguration.Current.CloudflareR2;
            if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.Bucket))
                throw new InvalidOperationException("Cloudflare R2 endpoint or bucket is not configured in ServiceConfiguration.");

            var fileInfo = new FileInfo(localFilePath);
            if (!fileInfo.Exists)
                throw new FileNotFoundException($"File to upload was not found: {localFilePath}");

            var service = "s3";
            var region = "auto";
            var host = new Uri(config.Endpoint).Host;
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ");
            var dateStamp = now.ToString("yyyyMMdd");
            var canonicalUri = $"/{config.Bucket}/{remoteKey}";
            var canonicalQueryString = "";

            var canonicalHeaders = $"content-type:{contentType}\nhost:{host}\nx-amz-content-sha256:UNSIGNED-PAYLOAD\nx-amz-date:{amzDate}\n";
            var signedHeaders = "content-type;host;x-amz-content-sha256;x-amz-date";
            var payloadHash = "UNSIGNED-PAYLOAD";

            var canonicalRequest = $"PUT\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var algorithm = "AWS4-HMAC-SHA256";
            var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
            var signingKey = GetSignatureKey(config.SecretKey, dateStamp, region, service);
            var signature = ToHex(HmacSHA256(signingKey, stringToSign));
            var authorizationHeader = $"{algorithm} Credential={config.AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

            var url = $"{config.Endpoint}/{config.Bucket}/{remoteKey}";
            var client = SharedHttpClient.StorageInstance;

            using var fileStream = File.OpenRead(localFilePath);
            var totalBytes = fileStream.Length;
            
            // Custom stream to report progress
            using var progressStream = new ProgressReportingStream(fileStream, totalBytes, sentBytes =>
            {
                if (progress != null && totalBytes > 0)
                {
                    int pct = (int)((sentBytes * 100L) / totalBytes);
                    progress.Report(new ResourceWorkflowProgress(
                        WorkflowStage.Uploading,
                        pct,
                        $"Uploading to R2 ({sentBytes / (1024 * 1024)}MB / {totalBytes / (1024 * 1024)}MB)...",
                        sentBytes,
                        totalBytes));
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
                throw new HttpRequestException($"Cloudflare R2 Upload failed with status {response.StatusCode}: {errorBody}");
            }

            return $"{config.Endpoint}/{config.Bucket}/{remoteKey}";
        }

        public async Task<bool> FileExistsAsync(string remoteKey, CancellationToken cancellationToken = default)
        {
            try
            {
                var config = ServiceConfiguration.Current.CloudflareR2;
                if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.Bucket))
                    return false;

                var service = "s3";
                var region = "auto";
                var host = new Uri(config.Endpoint).Host;
                var now = DateTime.UtcNow;
                var amzDate = now.ToString("yyyyMMddTHHmmssZ");
                var dateStamp = now.ToString("yyyyMMdd");
                var canonicalUri = $"/{config.Bucket}/{remoteKey}";
                var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:UNSIGNED-PAYLOAD\nx-amz-date:{amzDate}\n";
                var signedHeaders = "host;x-amz-content-sha256;x-amz-date";

                var canonicalRequest = $"HEAD\n{canonicalUri}\n\n{canonicalHeaders}\n{signedHeaders}\nUNSIGNED-PAYLOAD";
                var algorithm = "AWS4-HMAC-SHA256";
                var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
                var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{ToHex(HashSHA256(canonicalRequest))}";
                var signingKey = GetSignatureKey(config.SecretKey, dateStamp, region, service);
                var signature = ToHex(HmacSHA256(signingKey, stringToSign));
                var authorizationHeader = $"{algorithm} Credential={config.AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

                var url = $"{config.Endpoint}/{config.Bucket}/{remoteKey}";
                var client = SharedHttpClient.StorageInstance;

                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                req.Headers.Add("x-amz-date", amzDate);
                req.Headers.Add("x-amz-content-sha256", "UNSIGNED-PAYLOAD");
                req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);

                using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

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
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
        {
            var kDate = HmacSHA256(Encoding.UTF8.GetBytes("AWS4" + key), dateStamp);
            var kRegion = HmacSHA256(kDate, regionName);
            var kService = HmacSHA256(kRegion, serviceName);
            return HmacSHA256(kService, "aws4_request");
        }

        private sealed class ProgressReportingStream : Stream
        {
            private readonly Stream _innerStream;
            private readonly long _totalLength;
            private readonly Action<long> _onProgress;
            private long _bytesReadTotal;

            public ProgressReportingStream(Stream innerStream, long totalLength, Action<long> onProgress)
            {
                _innerStream = innerStream;
                _totalLength = totalLength;
                _onProgress = onProgress;
            }

            public override bool CanRead => _innerStream.CanRead;
            public override bool CanSeek => _innerStream.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _totalLength;
            public override long Position { get => _innerStream.Position; set => _innerStream.Position = value; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = _innerStream.Read(buffer, offset, count);
                if (read > 0)
                {
                    _bytesReadTotal += read;
                    _onProgress(_bytesReadTotal);
                }
                return read;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int read = await _innerStream.ReadAsync(buffer, cancellationToken);
                if (read > 0)
                {
                    _bytesReadTotal += read;
                    _onProgress(_bytesReadTotal);
                }
                return read;
            }

            public override void Flush() => _innerStream.Flush();
            public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
            public override void SetLength(long value) => _innerStream.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
