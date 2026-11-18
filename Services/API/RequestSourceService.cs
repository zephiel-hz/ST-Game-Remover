using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SteamPluginManager.Services.Security;

namespace SteamPluginManager.Services.API
{
    public sealed class RequestSourceDownloadResult
    {
        public bool IsDenuvo { get; init; }
        public string Source { get; init; } = string.Empty;
        public bool NotFound { get; init; }
        public List<DownloadedGameFile> Files { get; init; } = new();
    }

    public sealed class RequestSourceCredentialException : Exception
    {
        public string SourceName { get; }

        public RequestSourceCredentialException(string sourceName)
            : base($"{sourceName} credential is invalid or expired.")
        {
            SourceName = sourceName;
        }
    }

    public static class RequestSourceService
    {
        private sealed class MultipartFilePart
        {
            public string FileName { get; set; } = string.Empty;
            public string ContentType { get; set; } = string.Empty;
            public byte[] Bytes { get; set; } = Array.Empty<byte>();
        }

        private sealed class ErrorResponse
        {
            [JsonPropertyName("code")]
            public string Code { get; set; } = string.Empty;

            [JsonPropertyName("source")]
            public string Source { get; set; } = string.Empty;

            [JsonPropertyName("error")]
            public string Error { get; set; } = string.Empty;
        }

        private const string FunctionName = "request-game-assets";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static async Task<RequestSourceDownloadResult> DownloadAsync(
            int appId,
            string gameName,
            Action<string>? statusCallback = null)
        {
            statusCallback?.Invoke("Checking Denuvo status and downloading from the correct source...");

            var responseBody = JsonSerializer.Serialize(new
            {
                appId,
                gameName,
                deviceId = DeviceIdentifier.GetDeviceId()
            });

            using var request = CreateFunctionRequest(FunctionName, responseBody);
            using var response = await SharedHttpClient.Instance.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                var content = await response.Content.ReadAsStringAsync();
                var error = Deserialize<ErrorResponse>(content);
                if (error?.Code == "hubcap_api_key_invalid")
                    throw new RequestSourceCredentialException("Hubcap API key");
                if (error?.Code == "lua_tools_cookie_invalid")
                    throw new RequestSourceCredentialException("lua.tools cookie");
                if (error?.Code == "lua_tools_cookie_format_invalid")
                    throw new InvalidDataException(
                        error.Error is not null && !string.IsNullOrWhiteSpace(error.Error)
                            ? error.Error
                            : "The saved lua.tools Netscape cookie export has no active matching cookies.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var content = await response.Content.ReadAsStringAsync();
                var error = Deserialize<ErrorResponse>(content);
                if (error?.Code != "game_not_found")
                {
                    throw new HttpRequestException(
                        "Game source service returned an unexpected HTTP 404.");
                }

                return new RequestSourceDownloadResult
                {
                    NotFound = true,
                    Source = error?.Source ?? "request source"
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Game source request failed (HTTP {(int)response.StatusCode}).");
            }

            var contentType = response.Content.Headers.ContentType;
            if (!string.Equals(contentType?.MediaType, "multipart/mixed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Game source returned an unsupported file response.");

            var boundary = contentType?.Parameters
                .FirstOrDefault(parameter => string.Equals(
                    parameter.Name,
                    "boundary",
                    StringComparison.OrdinalIgnoreCase))
                ?.Value?
                .Trim('"');
            if (string.IsNullOrWhiteSpace(boundary))
                throw new InvalidDataException("Game source response is missing its multipart boundary.");

            bool isDenuvo = response.Headers.TryGetValues("X-Game-Denuvo", out var denuvoHeaders) &&
                bool.TryParse(denuvoHeaders.FirstOrDefault(), out bool parsedDenuvo) &&
                parsedDenuvo;
            string source = response.Headers.TryGetValues("X-Game-Source", out var sourceHeaders)
                ? sourceHeaders.FirstOrDefault() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidDataException("Game source response is missing its source name.");

            var files = new List<DownloadedGameFile>();
            foreach (var payload in ParseMultipartFiles(
                await response.Content.ReadAsByteArrayAsync(),
                boundary))
            {
                var fallbackFileName = isDenuvo
                    ? $"{appId}.manifest"
                    : $"{appId}.lua";
                var fileName = Path.GetFileName(payload.FileName);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = fallbackFileName;

                HubcapManifestService.ProcessPayload(
                    payload.Bytes,
                    payload.ContentType,
                    fileName,
                    files);
            }

            files = files
                .Where(file => isDenuvo
                    ? HasExtension(file.FileName, ".lua") || HasExtension(file.FileName, ".manifest")
                    : HasExtension(file.FileName, ".lua"))
                .ToList();

            if (files.Count == 0)
                throw new InvalidDataException("Game source returned no usable Lua or manifest files.");

            return new RequestSourceDownloadResult
            {
                IsDenuvo = isDenuvo,
                Source = source,
                Files = files
            };
        }

        private static List<MultipartFilePart> ParseMultipartFiles(byte[] content, string boundary)
        {
            var delimiter = Encoding.ASCII.GetBytes($"--{boundary}");
            var headerSeparator = new byte[] { 13, 10, 13, 10 };
            var files = new List<MultipartFilePart>();
            int position = FindSequence(content, delimiter, 0);

            while (position >= 0)
            {
                int afterDelimiter = position + delimiter.Length;
                if (afterDelimiter + 1 < content.Length &&
                    content[afterDelimiter] == (byte)'-' &&
                    content[afterDelimiter + 1] == (byte)'-')
                {
                    break;
                }

                if (afterDelimiter + 1 >= content.Length ||
                    content[afterDelimiter] != 13 ||
                    content[afterDelimiter + 1] != 10)
                {
                    throw new InvalidDataException("Game source returned an invalid multipart section.");
                }

                int headersStart = afterDelimiter + 2;
                int headersEnd = FindSequence(content, headerSeparator, headersStart);
                if (headersEnd < 0)
                    throw new InvalidDataException("Game source returned incomplete multipart headers.");

                string headers = Encoding.ASCII.GetString(
                    content,
                    headersStart,
                    headersEnd - headersStart);
                int contentStart = headersEnd + headerSeparator.Length;
                int nextDelimiter = FindSequence(content, delimiter, contentStart);
                if (nextDelimiter < 0)
                    throw new InvalidDataException("Game source returned an incomplete multipart file.");

                int contentEnd = nextDelimiter;
                if (contentEnd >= contentStart + 2 &&
                    content[contentEnd - 2] == 13 &&
                    content[contentEnd - 1] == 10)
                {
                    contentEnd -= 2;
                }

                string fileName = ReadMultipartFileName(headers);
                string partContentType = ReadMultipartHeader(headers, "Content-Type") ??
                    "application/octet-stream";
                var bytes = new byte[contentEnd - contentStart];
                Buffer.BlockCopy(content, contentStart, bytes, 0, bytes.Length);
                files.Add(new MultipartFilePart
                {
                    FileName = fileName,
                    ContentType = partContentType,
                    Bytes = bytes
                });
                position = nextDelimiter;
            }

            if (files.Count == 0)
                throw new InvalidDataException("Game source returned no files.");

            return files;
        }

        private static string ReadMultipartFileName(string headers)
        {
            string? disposition = ReadMultipartHeader(headers, "Content-Disposition");
            if (string.IsNullOrWhiteSpace(disposition))
                return string.Empty;

            var match = Regex.Match(disposition, @"filename=""?([^"";\r\n]+)");
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static string? ReadMultipartHeader(string headers, string headerName)
        {
            foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOf(':');
                if (separator <= 0 ||
                    !string.Equals(line.Substring(0, separator), headerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                return line.Substring(separator + 1).Trim();
            }

            return null;
        }

        private static int FindSequence(byte[] content, byte[] sequence, int startIndex)
        {
            if (sequence.Length == 0 || startIndex < 0 || startIndex > content.Length - sequence.Length)
                return -1;

            for (int i = startIndex; i <= content.Length - sequence.Length; i++)
            {
                int j = 0;
                while (j < sequence.Length && content[i + j] == sequence[j])
                    j++;
                if (j == sequence.Length)
                    return i;
            }

            return -1;
        }

        private static bool HasExtension(string fileName, string extension) =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

        private static T? Deserialize<T>(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return default;

            try
            {
                return JsonSerializer.Deserialize<T>(content, JsonOptions);
            }
            catch (JsonException)
            {
                return default;
            }
        }

        internal static HttpRequestMessage CreateFunctionRequest(string functionName, string body)
        {
            var supabaseUrl = SupabaseConfig.SupabaseUrl?.TrimEnd('/');
            var anonKey = SupabaseConfig.AnonKey;
            if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(anonKey))
            {
                throw new InvalidOperationException(
                    "Supabase URL and anon key are required for request-source functions.");
            }

            var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{supabaseUrl}/functions/v1/{functionName}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("apikey", anonKey);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {anonKey}");
            return request;
        }
    }

    public sealed class RequestSourceCredentials
    {
        public string HubcapApiKey { get; init; } = string.Empty;
        public string LuaToolsCookie { get; init; } = string.Empty;
    }

    public static class RequestSourceCredentialsService
    {
        private sealed class CredentialsResponse
        {
            [JsonPropertyName("hubcapApiKey")]
            public string HubcapApiKey { get; set; } = string.Empty;

            [JsonPropertyName("luaToolsCookie")]
            public string LuaToolsCookie { get; set; } = string.Empty;
        }

        private const string FunctionName = "request-source-credentials";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static async Task<RequestSourceCredentials> LoadAsync(string deviceId)
        {
            var content = await SendAsync(new { action = "load", deviceId });
            var credentials = JsonSerializer.Deserialize<CredentialsResponse>(content, JsonOptions)
                ?? throw new JsonException("Credential service returned an empty response.");

            return new RequestSourceCredentials
            {
                HubcapApiKey = credentials.HubcapApiKey,
                LuaToolsCookie = credentials.LuaToolsCookie
            };
        }

        public static async Task SaveAsync(
            string deviceId,
            string hubcapApiKey,
            string luaToolsCookie)
        {
            await SendAsync(new
            {
                action = "save",
                deviceId,
                hubcapApiKey,
                luaToolsCookie
            });
        }

        private static async Task<string> SendAsync<TBody>(TBody body)
        {
            var json = JsonSerializer.Serialize(body);
            using var request = RequestSourceService.CreateFunctionRequest(FunctionName, json);
            using var response = await SharedHttpClient.Instance.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Request source credential operation failed (HTTP {(int)response.StatusCode}).");
            }

            return content;
        }
    }
}
