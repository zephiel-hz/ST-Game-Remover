namespace SteamPluginManager.Services.Steam
{
    public static class SteamtoolsVersionHelper
    {
        public static bool IsLess(string? installedVersion, string? latestVersion)
        {
            string installed = Normalize(installedVersion);
            string latest = Normalize(latestVersion);
            if (installed.Length == 0 || latest.Length == 0)
                return false;

            var installedParts = installed.Split('.');
            var latestParts = latest.Split('.');
            int componentCount = Math.Max(installedParts.Length, latestParts.Length);

            for (int i = 0; i < componentCount; i++)
            {
                int installedPart = i < installedParts.Length &&
                    int.TryParse(installedParts[i], out int parsedInstalled)
                    ? parsedInstalled
                    : 0;
                int latestPart = i < latestParts.Length &&
                    int.TryParse(latestParts[i], out int parsedLatest)
                    ? parsedLatest
                    : 0;

                if (installedPart != latestPart)
                    return installedPart < latestPart;
            }

            return false;
        }

        private static string Normalize(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return string.Empty;

            version = version.Trim();
            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                version = version.Substring(1);

            int suffixIndex = version.IndexOfAny(new[] { '+', '-' });
            if (suffixIndex >= 0)
                version = version.Substring(0, suffixIndex);

            return version.Trim();
        }
    }
}
