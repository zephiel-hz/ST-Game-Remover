using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SteamPluginManager.Services.Security;

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
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{AwsSigV4Crypto.ToHex(AwsSigV4Crypto.HashSHA256(canonicalRequest))}";
            var signingKey = AwsSigV4Crypto.GetSignatureKey(config.SecretKey, dateStamp, region, service);
            var signature = AwsSigV4Crypto.ToHex(AwsSigV4Crypto.HmacSHA256(signingKey, stringToSign));
            var authorizationHeader = $"{algorithm} Credential={config.AccessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

            var url = $"{config.Endpoint}/{config.Bucket}/{remoteKey}";
            var client = SharedHttpClient.StorageInstance;

            using var fileStream = File.OpenRead(localFilePath);
            var totalBytes = fileStream.Length;
            
            // Custom stream to report progress
            using var progressStream = new StorageProgressStream(fileStream, totalBytes, sentBytes =>
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
                var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{AwsSigV4Crypto.ToHex(AwsSigV4Crypto.HashSHA256(canonicalRequest))}";
                var signingKey = AwsSigV4Crypto.GetSignatureKey(config.SecretKey, dateStamp, region, service);
                var signature = AwsSigV4Crypto.ToHex(AwsSigV4Crypto.HmacSHA256(signingKey, stringToSign));
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
    }
}
