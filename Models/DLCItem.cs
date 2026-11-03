using System;
using System.Text.Json.Serialization;

namespace SteamPluginManager.Models
{
    public class DLCItem
    {
        [JsonPropertyName("dlc_app_id")]
        public int AppId { get; set; }

        [JsonIgnore]
        public int DlcAppId => AppId;

        [JsonPropertyName("dlc_name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dlc_fetch_date")]
        public DateTime FetchDate { get; set; }
    }
}
