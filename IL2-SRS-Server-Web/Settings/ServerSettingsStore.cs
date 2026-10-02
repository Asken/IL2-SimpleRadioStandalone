using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Data;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting;
using Microsoft.Extensions.Configuration;
using NLog;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Settings
{
    /// <summary>
    /// Server settings. Effective value = override (SRS_&lt;KEY&gt; from environment, command line or
    /// srs-server.json) if set, else the value stored in the database, else the default.
    /// Values are cached in memory because the voice path reads some of them for every packet.
    /// </summary>
    public sealed class ServerSettingsStore
    {
        public const string OverridePrefix = "SRS_";

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static ServerSettingsStore instance;

        private readonly SrsDatabase _database;
        private readonly IReadOnlyDictionary<string, string> _overrides;
        private readonly ConcurrentDictionary<string, SettingValue> _values = new ConcurrentDictionary<string, SettingValue>();
        private readonly object _writeLock = new object();
        private volatile Dictionary<int, string> _channelNames;

        public ServerSettingsStore(SrsDatabase database, IReadOnlyDictionary<string, string> overrides)
        {
            _database = database;
            _overrides = ValidateOverrides(overrides ?? new Dictionary<string, string>());
            Reload();
        }

        /// <summary>The store used by the SRS network code; set once at startup.</summary>
        public static ServerSettingsStore Instance
        {
            get => instance ?? throw new InvalidOperationException("Server settings have not been initialised.");
            set => instance = value;
        }

        /// <summary>Reads SRS_&lt;KEY&gt; overrides for every known setting from configuration.</summary>
        public static Dictionary<string, string> ReadOverrides(IConfiguration configuration)
        {
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ServerSettingsKeys key in Enum.GetValues(typeof(ServerSettingsKeys)))
            {
                var value = configuration[OverridePrefix + key];
                if (value != null)
                {
                    overrides[key.ToString()] = value;
                }
            }

            return overrides;
        }

        public SettingValue GetGeneralSetting(ServerSettingsKeys key) => Get(key);

        public SettingValue GetServerSetting(ServerSettingsKeys key) => Get(key);

        public SettingValue Get(ServerSettingsKeys key)
        {
            return _values.TryGetValue(key.ToString(), out var value)
                ? value
                : new SettingValue(string.Empty, SettingSource.Default);
        }

        public int GetServerPort() => Get(ServerSettingsKeys.SERVER_PORT).IntValue;

        /// <summary>Validates and stores a setting. Returns an error message, or null on success.</summary>
        public string Set(ServerSettingsKeys key, string value)
        {
            var definition = SettingDefinition.For(key);
            if (Get(key).IsLocked)
            {
                return $"{key} is set by {OverridePrefix}{key} and cannot be changed here.";
            }

            var error = definition.Normalize(value, out var normalized);
            if (error != null)
            {
                return error;
            }

            lock (_writeLock)
            {
                using var connection = _database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
INSERT INTO settings (key, value, updated_at) VALUES ($key, $value, $updated)
ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;";
                command.Parameters.AddWithValue("$key", key.ToString());
                command.Parameters.AddWithValue("$value", normalized);
                command.Parameters.AddWithValue("$updated", SrsDatabase.FormatTimestamp(DateTime.UtcNow));
                command.ExecuteNonQuery();

                _values[key.ToString()] = new SettingValue(normalized, SettingSource.Database);
            }

            return null;
        }

        /// <summary>Settings sent to clients: everything outside the server-only section (as the WPF server's "General Settings").</summary>
        public Dictionary<string, string> ToDictionary()
        {
            var settings = new Dictionary<string, string>();
            foreach (var entry in _values)
            {
                if (!DefaultServerSettings.ServerSectionSettings.Contains(entry.Key))
                {
                    settings[entry.Key] = entry.Value.StringValue;
                }
            }

            return settings;
        }

        public Dictionary<int, string> GetChannelNames()
        {
            return new Dictionary<int, string>(_channelNames);
        }

        public void SetChannelNames(IDictionary<int, string> channelNames)
        {
            var normalized = Normalize(channelNames);

            lock (_writeLock)
            {
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM channel_names;";
                    delete.ExecuteNonQuery();
                }

                foreach (var channelName in normalized)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO channel_names (channel, name) VALUES ($channel, $name);";
                    insert.Parameters.AddWithValue("$channel", channelName.Key);
                    insert.Parameters.AddWithValue("$name", channelName.Value);
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
                _channelNames = normalized;
            }
        }

        /// <summary>Stores values without validation or override checks; used by the server.cfg import.</summary>
        internal void Import(IDictionary<string, string> settings, IDictionary<int, string> channelNames)
        {
            lock (_writeLock)
            {
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                foreach (var setting in settings)
                {
                    if (!Enum.TryParse(setting.Key, false, out ServerSettingsKeys key))
                    {
                        continue;
                    }

                    var error = SettingDefinition.For(key).Normalize(setting.Value, out var normalized);
                    if (error != null)
                    {
                        Logger.Warn($"Skipping imported setting: {error}");
                        continue;
                    }

                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO settings (key, value, updated_at) VALUES ($key, $value, $updated)
ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;";
                    command.Parameters.AddWithValue("$key", key.ToString());
                    command.Parameters.AddWithValue("$value", normalized);
                    command.Parameters.AddWithValue("$updated", SrsDatabase.FormatTimestamp(DateTime.UtcNow));
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            if (channelNames != null && channelNames.Count > 0)
            {
                SetChannelNames(channelNames);
            }

            Reload();
        }

        private void Reload()
        {
            var stored = new Dictionary<string, string>(StringComparer.Ordinal);
            var channelNames = new Dictionary<int, string>();

            using (var connection = _database.Open())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT key, value FROM settings;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        stored[reader.GetString(0)] = reader.GetString(1);
                    }
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT channel, name FROM channel_names;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        channelNames[reader.GetInt32(0)] = reader.GetString(1);
                    }
                }
            }

            foreach (ServerSettingsKeys key in Enum.GetValues(typeof(ServerSettingsKeys)))
            {
                var name = key.ToString();
                if (_overrides.TryGetValue(name, out var overridden))
                {
                    _values[name] = new SettingValue(overridden, SettingSource.Override);
                }
                else if (stored.TryGetValue(name, out var value))
                {
                    _values[name] = new SettingValue(value, SettingSource.Database);
                }
                else
                {
                    DefaultServerSettings.Defaults.TryGetValue(name, out var defaultValue);
                    _values[name] = new SettingValue(defaultValue ?? string.Empty, SettingSource.Default);
                }
            }

            _channelNames = Normalize(channelNames);
        }

        private static Dictionary<int, string> Normalize(IDictionary<int, string> channelNames)
        {
            var normalized = new Dictionary<int, string>();
            if (channelNames == null)
            {
                return normalized;
            }

            foreach (var channelName in channelNames)
            {
                var name = ChannelNameSettings.NormalizeName(channelName.Value);
                if (channelName.Key >= 1 && channelName.Key <= ChannelNameSettings.MaximumChannel &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    normalized[channelName.Key] = name;
                }
            }

            return normalized;
        }

        private static IReadOnlyDictionary<string, string> ValidateOverrides(IReadOnlyDictionary<string, string> overrides)
        {
            var validated = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in overrides)
            {
                if (!SettingDefinition.TryFind(entry.Key, out var definition))
                {
                    throw new ServerStartupException($"Unknown server setting override {OverridePrefix}{entry.Key}.");
                }

                var error = definition.Normalize(entry.Value, out var normalized);
                if (error != null)
                {
                    throw new ServerStartupException($"Invalid {OverridePrefix}{entry.Key}: {error}");
                }

                validated[entry.Key] = normalized;
                Logger.Info($"Setting {entry.Key} is fixed by {OverridePrefix}{entry.Key}" +
                            (definition.Secret ? string.Empty : $" = {normalized}"));
            }

            return validated;
        }

        public IReadOnlyList<KeyValuePair<SettingDefinition, SettingValue>> GetAll()
        {
            return SettingDefinition.All
                .Select(definition => new KeyValuePair<SettingDefinition, SettingValue>(definition, Get(definition.Key)))
                .ToList();
        }
    }
}
