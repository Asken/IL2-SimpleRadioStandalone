using System;
using System.Globalization;

namespace Ciribob.IL2.SimpleRadio.Standalone.Common.Setting
{
    public static class ChannelNameSettings
    {
        public const string SectionName = "Channel Names";
        public const string SyncedSettingPrefix = "CHANNEL_NAME_";
        public const int MaximumChannel = 25;
        public const int MaximumNameLength = 32;

        // Coalition numbers as used by PlayerGameState and SRClient. Shared names apply to every coalition.
        public const int SharedCoalition = 0;
        public const int AlliesCoalition = 1;
        public const int AxisCoalition = 2;

        // Per-coalition names are synced as CHANNEL_NAME_ALLIES_<n> / CHANNEL_NAME_AXIS_<n>. They start with
        // SyncedSettingPrefix, but the shared parser rejects them (the suffix is not a number), so clients
        // without per-coalition support ignore them and keep showing the shared names.
        private const string AlliesSyncedSettingPrefix = SyncedSettingPrefix + "ALLIES_";
        private const string AxisSyncedSettingPrefix = SyncedSettingPrefix + "AXIS_";

        public static readonly int[] Coalitions = { AlliesCoalition, AxisCoalition };

        public static string GetSyncedSettingName(int channel)
        {
            return SyncedSettingPrefix + channel.ToString(CultureInfo.InvariantCulture);
        }

        public static string GetSyncedSettingName(int channel, int coalition)
        {
            return GetSyncedSettingPrefix(coalition) + channel.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Parses shared and per-coalition setting names; coalition is SharedCoalition for shared names.</summary>
        public static bool TryParseSyncedSettingName(string settingName, out int coalition, out int channel)
        {
            foreach (var candidate in Coalitions)
            {
                if (TryParseChannel(settingName, GetSyncedSettingPrefix(candidate), out channel))
                {
                    coalition = candidate;
                    return true;
                }
            }

            coalition = SharedCoalition;
            return TryParseSyncedSettingName(settingName, out channel);
        }

        public static bool IsCoalition(int coalition)
        {
            return coalition == AlliesCoalition || coalition == AxisCoalition;
        }

        /// <summary>"Allies" or "Axis"; "Shared" for any other value.</summary>
        public static string GetCoalitionLabel(int coalition)
        {
            switch (coalition)
            {
                case AlliesCoalition:
                    return "Allies";
                case AxisCoalition:
                    return "Axis";
                default:
                    return "Shared";
            }
        }

        private static string GetSyncedSettingPrefix(int coalition)
        {
            switch (coalition)
            {
                case AlliesCoalition:
                    return AlliesSyncedSettingPrefix;
                case AxisCoalition:
                    return AxisSyncedSettingPrefix;
                default:
                    return SyncedSettingPrefix;
            }
        }

        private static bool TryParseChannel(string settingName, string prefix, out int channel)
        {
            channel = 0;
            return !string.IsNullOrWhiteSpace(settingName) &&
                   settingName.StartsWith(prefix, StringComparison.Ordinal) &&
                   int.TryParse(settingName.Substring(prefix.Length), NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out channel) &&
                   channel >= 1 && channel <= MaximumChannel;
        }

        public static bool TryParseSyncedSettingName(string settingName, out int channel)
        {
            return TryParseChannel(settingName, SyncedSettingPrefix, out channel);
        }

        public static string NormalizeName(string name)
        {
            var normalized = (name ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            return normalized.Length > MaximumNameLength
                ? normalized.Substring(0, MaximumNameLength).TrimEnd()
                : normalized;
        }

    }
}
