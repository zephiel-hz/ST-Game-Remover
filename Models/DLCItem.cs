using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SteamPluginManager.Models
{
    public class DLCItem : INotifyPropertyChanged
    {
        [JsonPropertyName("dlc_app_id")]
        public int AppId { get; set; }

        [JsonPropertyName("dlc_name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dlc_fetch_date")]
        public DateTime FetchDate { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
