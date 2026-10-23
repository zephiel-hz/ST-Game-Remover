using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamPluginManager.Models
{
    public class TutorialStep
    {
        [JsonPropertyName("step_number")]
        public int StepNumber { get; set; } = 1;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    public class CustomLaunchTutorial
    {
        [JsonPropertyName("app_id")]
        public int AppId { get; set; }

        [JsonPropertyName("game_title")]
        public string GameTitle { get; set; } = string.Empty;

        [JsonPropertyName("applicable_to")]
        public string ApplicableTo { get; set; } = "ALL"; // "ALL", "GAMEBYPASS", "ONLINEFIX"

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("recommended_executable")]
        public string RecommendedExecutable { get; set; } = string.Empty;

        [JsonPropertyName("recommended_arguments")]
        public string RecommendedArguments { get; set; } = string.Empty;

        [JsonPropertyName("steps")]
        public List<TutorialStep> Steps { get; set; } = new();

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
