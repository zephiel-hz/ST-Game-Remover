using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SteamPluginManager.Services.API;

namespace SteamPluginManager.Services.Security
{
    public static class DeviceIdentityService
    {
        private static readonly SemaphoreSlim ResolutionLock = new(1, 1);
        private static string? _resolvedTpmHash;

        private sealed class ResolveRequest
        {
            [JsonPropertyName("tpmHash")]
            public string TpmHash { get; init; } = string.Empty;

            [JsonPropertyName("currentDeviceId")]
            public string CurrentDeviceId { get; init; } = string.Empty;
        }

        private sealed class ResolveResponse
        {
            [JsonPropertyName("deviceId")]
            public string? DeviceId { get; init; }

            [JsonPropertyName("resolved")]
            public bool Resolved { get; init; }
        }

        public static async Task ResolveAndMigrateAsync()
        {
            string? tpmHash = await Task.Run(DeviceIdentifier.GetTpmIdentityHash);
            if (string.IsNullOrWhiteSpace(tpmHash) ||
                string.Equals(_resolvedTpmHash, tpmHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await ResolutionLock.WaitAsync();
            try
            {
                if (string.Equals(_resolvedTpmHash, tpmHash, StringComparison.OrdinalIgnoreCase))
                    return;

                string currentDeviceId = DeviceIdentifier.GetDeviceId();
                var payload = new ResolveRequest
                {
                    TpmHash = tpmHash,
                    CurrentDeviceId = currentDeviceId
                };
                string url = $"{SupabaseConfig.SupabaseUrl.TrimEnd('/')}/functions/v1/device-identity";
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("apikey", SupabaseConfig.AnonKey);

                using var response = await SharedHttpClient.Instance.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    SteamPluginManager.Logger.Log(
                        $"[DeviceIdentityService] TPM identity resolution failed with HTTP {(int)response.StatusCode}; retaining the current device ID.");
                    return;
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ResolveResponse>(
                    responseBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (result == null)
                {
                    SteamPluginManager.Logger.Log(
                        "[DeviceIdentityService] Supabase returned an invalid TPM identity response.");
                    return;
                }

                if (!result.Resolved)
                {
                    SteamPluginManager.Logger.Log(
                        "[DeviceIdentityService] Supabase did not link the TPM identity; verify the current Device ID is bound to an active token or is blocked.");
                    return;
                }

                if (!DeviceIdentifier.IsValidDeviceId(result.DeviceId))
                {
                    SteamPluginManager.Logger.Log(
                        "[DeviceIdentityService] Supabase resolved the TPM identity to an invalid Device ID.");
                    return;
                }

                DeviceIdentifier.SetResolvedDeviceId(result.DeviceId!, tpmHash);
                _resolvedTpmHash = tpmHash;
            }
            catch (Exception ex)
            {
                SteamPluginManager.Logger.Log(
                    $"[DeviceIdentityService] TPM identity resolution error; retaining the current device ID: {ex.Message}");
            }
            finally
            {
                ResolutionLock.Release();
            }
        }
    }
}
