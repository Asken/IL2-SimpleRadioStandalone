using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;
using NLog;
using SharpConfig;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Settings
{
    /// <summary>
    /// When the database is first created, imports server.cfg and banned.txt written by the WPF server,
    /// so an existing server folder can be used as the data directory as-is. The files are left untouched.
    /// </summary>
    public static class LegacyFileImporter
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static readonly string[] SettingSections = { "General Settings", "Server Settings" };

        public static void ImportServerConfig(string cfgFile, ServerSettingsStore settings)
        {
            if (!File.Exists(cfgFile))
            {
                return;
            }

            Configuration configuration;
            try
            {
                configuration = Configuration.LoadFromFile(cfgFile);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Unable to read {cfgFile}; starting with default settings");
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sectionName in SettingSections)
            {
                if (!configuration.Contains(sectionName))
                {
                    continue;
                }

                foreach (Setting setting in configuration[sectionName])
                {
                    values[setting.Name] = setting.StringValue;
                }
            }

            // Very old configs used "port" instead of SERVER_PORT.
            if (!values.ContainsKey(ServerSettingsKeys.SERVER_PORT.ToString()) &&
                configuration.Contains("Server Settings") && configuration["Server Settings"].Contains("port"))
            {
                values[ServerSettingsKeys.SERVER_PORT.ToString()] = configuration["Server Settings"]["port"].StringValue;
            }

            var channelNames = new Dictionary<int, string>();
            if (configuration.Contains(ChannelNameSettings.SectionName))
            {
                foreach (Setting setting in configuration[ChannelNameSettings.SectionName])
                {
                    if (int.TryParse(setting.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
                    {
                        channelNames[channel] = setting.StringValue;
                    }
                }
            }

            settings.Import(values, channelNames);
            Logger.Info($"Imported {values.Count} settings and {channelNames.Count} channel names from {cfgFile}");
        }

        public static void ImportBannedIps(string bannedFile, BanStore bans)
        {
            if (!File.Exists(bannedFile))
            {
                return;
            }

            var count = 0;
            try
            {
                foreach (var line in File.ReadAllLines(bannedFile))
                {
                    if (IPAddress.TryParse(line.Trim(), out var ip))
                    {
                        bans.Add(ip, null, "Imported from banned.txt", "import", null);
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Unable to read {bannedFile}");
            }

            Logger.Info($"Imported {count} banned IP addresses from {bannedFile}");
        }
    }
}
