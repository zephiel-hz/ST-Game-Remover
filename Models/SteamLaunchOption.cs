namespace SteamPluginManager.Models
{
    public sealed class SteamLaunchOption
    {
        public string Index { get; set; } = "0";
        public string SourceIndex { get; set; } = string.Empty;
        public string Executable { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = "default";
        public string OperatingSystems { get; set; } = string.Empty;
        public string Architecture { get; set; } = string.Empty;
        public string BetaBranch { get; set; } = string.Empty;
        public string OwnsDlc { get; set; } = string.Empty;

        public SteamLaunchOption Copy() => (SteamLaunchOption)MemberwiseClone();
    }
}
