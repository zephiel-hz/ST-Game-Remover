using System;
using System.Linq;
using System.Reflection;

namespace SteamPluginManager
{
    public static class AppInfo
    {
        private static string? _version;
        private static string? _formattedVersion;
        private static string? _userAgent;

        public static string AppName => "HZ Lua Manager";
        public static string InternalName => "SteamPluginManager";

        /// <summary>
        /// Gets the semantic version string dynamically from assembly metadata (e.g. "3.0.0").
        /// </summary>
        public static string Version
        {
            get
            {
                if (_version != null) return _version;

                try
                {
                    var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

                    var informationalVersionAttribute = assembly
                        .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                        .OfType<AssemblyInformationalVersionAttribute>()
                        .FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(informationalVersionAttribute?.InformationalVersion))
                    {
                        var infoVer = informationalVersionAttribute.InformationalVersion.Trim();
                        var plusIndex = infoVer.IndexOf('+');
                        if (plusIndex >= 0)
                        {
                            infoVer = infoVer.Substring(0, plusIndex);
                        }
                        if (infoVer.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        {
                            infoVer = infoVer.Substring(1);
                        }
                        _version = infoVer;
                        return _version;
                    }

                    var fileVersionAttribute = assembly
                        .GetCustomAttributes(typeof(AssemblyFileVersionAttribute), false)
                        .OfType<AssemblyFileVersionAttribute>()
                        .FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(fileVersionAttribute?.Version))
                    {
                        _version = fileVersionAttribute.Version.Trim();
                        return _version;
                    }

                    var asmVersion = assembly.GetName().Version;
                    if (asmVersion != null)
                    {
                        _version = $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}";
                        return _version;
                    }
                }
                catch
                {
                    // Fallback
                }

                _version = "3.0.2";
                return _version;
            }
        }

        /// <summary>
        /// Gets the formatted version string with prefix 'v' (e.g. "v3.0.0").
        /// </summary>
        public static string FormattedVersion
        {
            get
            {
                if (_formattedVersion != null) return _formattedVersion;
                _formattedVersion = $"v{Version}";
                return _formattedVersion;
            }
        }

        /// <summary>
        /// Gets the standard User-Agent header string (e.g. "SteamPluginManager/3.0.0").
        /// </summary>
        public static string UserAgent
        {
            get
            {
                if (_userAgent != null) return _userAgent;
                _userAgent = $"{InternalName}/{Version}";
                return _userAgent;
            }
        }
    }
}
