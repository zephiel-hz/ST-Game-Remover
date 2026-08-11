using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SteamPluginManager.Services.Parsing
{
    public static class LuaManifestParser
    {
        private static readonly Regex DlcSectionHeaderRegex = new(@"^\s*--\s*DLCS\s+(WITHOUT|WITH)\s+DEDICATED\s+DEPOTS", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex AddAppIdRegex = new(@"addappid\s*\(\s*(\d+)(?:\s*,[^)]*)*\)", RegexOptions.IgnoreCase);
        private static readonly Regex AddAppIdWithNameRegex = new(@"addappid\s*\(\s*(\d+)(?:\s*,[^)]*)*\)\s*(?:--\s*(.+))?", RegexOptions.IgnoreCase);

        public sealed record DlcEntry(int AppId, string Name);

        public static List<int> ExtractDlcAppIds(string luaContent)
        {
            return ExtractDlcEntries(luaContent).Select(entry => entry.AppId).ToList();
        }

        public static List<DlcEntry> ExtractDlcEntries(string luaContent)
        {
            if (string.IsNullOrWhiteSpace(luaContent))
                return new List<DlcEntry>();

            var lines = luaContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var dlcEntries = new Dictionary<int, string>();
            bool inDlcSection = false;

            // If no explicit DLCS header exists, treat the whole file as the DLC section
            bool hasHeader = DlcSectionHeaderRegex.IsMatch(luaContent);
            if (!hasHeader)
                inDlcSection = true;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (DlcSectionHeaderRegex.IsMatch(line))
                {
                    inDlcSection = true;
                    continue;
                }

                if (!inDlcSection)
                    continue;

                if (line.StartsWith("setManifestid", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (line.StartsWith("--", StringComparison.OrdinalIgnoreCase) && !line.Contains("addappid", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!line.Contains("addappid", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = AddAppIdWithNameRegex.Match(line);
                if (!match.Success)
                    continue;

                if (!int.TryParse(match.Groups[1].Value, out var appId))
                    continue;

                var dlcName = string.Empty;
                if (match.Groups.Count > 2 && match.Groups[2].Success)
                    dlcName = TrimDlcName(match.Groups[2].Value);

                if (dlcEntries.TryGetValue(appId, out var existingName))
                {
                    if (string.IsNullOrWhiteSpace(existingName) && !string.IsNullOrWhiteSpace(dlcName))
                        dlcEntries[appId] = dlcName;
                }
                else
                {
                    dlcEntries[appId] = dlcName;
                }
            }

            return dlcEntries
                .OrderBy(pair => pair.Key)
                .Select(pair => new DlcEntry(pair.Key, pair.Value))
                .ToList();
        }

        private static string TrimDlcName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return string.Empty;

            var trimmed = rawName.Trim();
            if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
                (trimmed.StartsWith('(') && trimmed.EndsWith(')')))
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
            }

            return trimmed;
        }
    }
}
