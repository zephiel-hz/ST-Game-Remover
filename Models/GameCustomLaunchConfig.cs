using System.IO;
using System.Text.Json.Serialization;

namespace SteamPluginManager.Models
{
    public class GameCustomLaunchConfig
    {
        [JsonPropertyName("app_id")]
        public int AppId { get; set; }

        [JsonPropertyName("is_enabled")]
        public bool IsEnabled { get; set; } = true;

        [JsonPropertyName("executable_path")]
        public string ExecutablePath { get; set; } = string.Empty;

        [JsonPropertyName("relative_path")]
        public string RelativePath { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = string.Empty;

        [JsonPropertyName("working_directory")]
        public string WorkingDirectory { get; set; } = string.Empty;

        [JsonPropertyName("original_icon_path")]
        public string? OriginalIconPath { get; set; }

        [JsonIgnore]
        public string FileName => !string.IsNullOrEmpty(ExecutablePath) ? Path.GetFileName(ExecutablePath) : string.Empty;

        [JsonIgnore]
        public bool IsBatchScript
        {
            get
            {
                if (string.IsNullOrEmpty(ExecutablePath)) return false;
                string ext = Path.GetExtension(ExecutablePath).ToLowerInvariant();
                return ext == ".bat" || ext == ".cmd";
            }
        }
    }
}
