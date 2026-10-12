using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Api;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Audit;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Events;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Settings;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Admin
{
    /// <summary>
    /// Everything the admin UI and the API read or change. Every change is published as a typed event
    /// (stored in the event log) with the actor ("admin" for the UI, "api:&lt;key name&gt;" for API keys).
    /// </summary>
    public sealed class ServerAdminService
    {
        private readonly ServerState _serverState;
        private readonly ServerSettingsStore _settings;
        private readonly BanStore _bans;
        private readonly ApiKeyStore _apiKeys;

        public ServerAdminService(ServerState serverState, ServerEvents events, ServerSettingsStore settings,
            BanStore bans, AuditLog audit, ApiKeyStore apiKeys)
        {
            _serverState = serverState;
            _settings = settings;
            _bans = bans;
            _apiKeys = apiKeys;
            Events = events;
            Audit = audit;
        }

        public ServerEvents Events { get; }
        public AuditLog Audit { get; }

        public bool IsRunning => _serverState.IsRunning;
        public int Port => _serverState.IsRunning ? _serverState.Port : _settings.GetServerPort();
        public string Version => ReleaseMetadata.Version;

        // Status and clients

        public IReadOnlyList<SRClient> GetClients() => _serverState.GetClients();

        public string GetClientAddress(string clientGuid) => _serverState.GetClientAddress(clientGuid);

        public ServerHealthSnapshot GetHealth()
        {
            long workingSetBytes;
            try
            {
                using var process = Process.GetCurrentProcess();
                workingSetBytes = process.WorkingSet64;
            }
            catch
            {
                workingSetBytes = 0;
            }

            return ServerHealthSnapshot.Create(_serverState.IsRunning, _serverState.IsTcpListenerRunning,
                _serverState.IsUdpListenerRunning, _serverState.StartedAtUtc, _serverState.GetClients(),
                workingSetBytes, DateTime.UtcNow);
        }

        public DateTime? StartedAtUtc => _serverState.StartedAtUtc;

        /// <summary>Starts the server; returns an error message, or null on success.</summary>
        public string Start(string actor)
        {
            if (_serverState.IsRunning)
            {
                return null;
            }

            try
            {
                _serverState.Start();
                Events.Publish(SrsEventTypes.ServerStarted, actor, $"Started the server on port {_serverState.Port}",
                    ServerPayload());
                return null;
            }
            catch (Exception ex)
            {
                Events.Publish(SrsEventTypes.ServerStartFailed, actor, "Failed to start the server: " + ex.Message,
                    ServerPayload().With("error", ex.Message));
                return ex.Message;
            }
        }

        public void Stop(string actor)
        {
            if (!_serverState.IsRunning)
            {
                return;
            }

            var payload = ServerPayload();
            _serverState.Stop();
            Events.Publish(SrsEventTypes.ServerStopped, actor, "Stopped the server", payload);
        }

        /// <summary>Stops and starts the server so port and startup-only settings take effect.</summary>
        public string Restart(string actor)
        {
            Stop(actor);
            return Start(actor);
        }

        public bool KickClient(string clientGuid, string actor)
        {
            var client = FindClient(clientGuid);
            var payload = client != null ? ClientPayload(client) : null;
            if (client == null || !_serverState.KickClient(clientGuid))
            {
                return false;
            }

            Events.Publish(SrsEventTypes.ClientKicked, actor, $"Kicked {Describe(client)}", payload);
            return true;
        }

        public BanRecord BanClient(string clientGuid, string reason, TimeSpan? duration, string actor)
        {
            var client = FindClient(clientGuid);
            if (client == null)
            {
                return null;
            }

            var payload = ClientPayload(client);
            var ban = _serverState.BanClient(clientGuid, reason, actor, ExpiresAt(duration));
            if (ban != null)
            {
                Events.Publish(SrsEventTypes.ClientBanned, actor,
                    $"Banned {Describe(client)} at {ban.IpAddress}{DescribeBan(ban)}",
                    payload.With(EventPayload.Ban(ban)));
            }

            return ban;
        }

        public bool SetClientMuted(string clientGuid, bool muted, string actor)
        {
            var client = FindClient(clientGuid);
            if (client == null || !_serverState.SetClientMuted(clientGuid, muted))
            {
                return false;
            }

            Events.Publish(muted ? SrsEventTypes.ClientMuted : SrsEventTypes.ClientUnmuted, actor,
                $"{(muted ? "Muted" : "Unmuted")} {Describe(client)}", ClientPayload(client));
            return true;
        }

        // Bans

        public IReadOnlyList<BanRecord> GetBans() => _bans.List();

        public int ActiveBanCount => _bans.ActiveCount;

        /// <summary>Checks an address to ban; returns an error message or null.</summary>
        public static string ValidateIpAddress(string ipAddress)
        {
            var text = (ipAddress ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return "Enter the IP address to ban.";
            }

            return IPAddress.TryParse(text, out _) ? null : "Enter a valid IPv4 or IPv6 address, e.g. 203.0.113.7.";
        }

        /// <summary>Bans an IP address directly; returns an error message or null.</summary>
        public string BanAddress(string ipAddress, string playerName, string reason, TimeSpan? duration, string actor,
            out BanRecord ban)
        {
            ban = null;
            if (ValidateIpAddress(ipAddress) is { } error)
            {
                return error;
            }

            var address = IPAddress.Parse(ipAddress.Trim());

            ban = _bans.Add(address, playerName, reason, actor, ExpiresAt(duration));
            var disconnected = _serverState.DisconnectAddress(address);
            Events.Publish(SrsEventTypes.AddressBanned, actor,
                $"Banned {ban.IpAddress}{DescribeBan(ban)}" +
                (disconnected > 0 ? $", disconnected {disconnected} client(s)" : string.Empty),
                EventPayload.Ban(ban).With("disconnectedClients", disconnected));
            return null;
        }

        public bool Unban(long banId, string actor)
        {
            var ban = _bans.Remove(banId);
            if (ban == null)
            {
                return false;
            }

            Events.Publish(SrsEventTypes.BanRemoved, actor,
                $"Removed ban on {ban.IpAddress}" + (ban.PlayerName != null ? $" ({ban.PlayerName})" : string.Empty),
                EventPayload.Ban(ban));
            return true;
        }

        // Settings

        public IReadOnlyList<KeyValuePair<SettingDefinition, SettingValue>> GetSettings() => _settings.GetAll();

        public SettingValue GetSetting(ServerSettingsKeys key) => _settings.Get(key);

        /// <summary>Changes a setting; returns an error message, or null on success.</summary>
        public string SetSetting(ServerSettingsKeys key, string value, string actor)
        {
            var definition = SettingDefinition.For(key);
            if (definition.Hidden)
            {
                return $"{key} is not used by this server.";
            }

            var previous = _settings.Get(key).StringValue;
            var error = _settings.Set(key, value);
            if (error != null)
            {
                return error;
            }

            var current = _settings.Get(key).StringValue;
            if (current == previous)
            {
                return null;
            }

            Events.Publish(SrsEventTypes.SettingChanged, actor, definition.Secret
                    ? $"Changed {key}"
                    : $"Changed {key} from \"{previous}\" to \"{current}\"" +
                      (definition.RequiresRestart && _serverState.IsRunning ? " (applies after a server restart)" : string.Empty),
                new JsonObject
                {
                    ["setting"] = new JsonObject
                    {
                        ["key"] = key.ToString(),
                        ["label"] = definition.Label,
                        ["previousValue"] = definition.Secret ? null : previous,
                        ["value"] = definition.Secret ? null : current,
                        ["secret"] = definition.Secret,
                        ["requiresRestart"] = definition.RequiresRestart
                    }
                });

            if (key == ServerSettingsKeys.GLOBAL_LOBBY_FREQUENCIES)
            {
                Events.PublishGlobalLobbyFrequenciesChanged(current);
            }

            Events.PublishSettingsChanged();
            return null;
        }

        /// <summary>Sets a toggle by its label meaning (handles inverted settings such as Spectator Audio).</summary>
        public string SetEnabled(SettingDefinition definition, bool enabled, string actor)
        {
            var stored = definition.Inverted ? !enabled : enabled;
            return SetSetting(definition.Key, stored ? "true" : "false", actor);
        }

        public bool IsEnabled(SettingDefinition definition)
        {
            var value = _settings.Get(definition.Key).BoolValue;
            return definition.Inverted ? !value : value;
        }

        public Dictionary<int, string> GetChannelNames() => _settings.GetChannelNames();

        public Dictionary<int, string> GetChannelNames(int coalition) => _settings.GetChannelNames(coalition);

        public ChannelNameSet GetChannelNameSet() => _settings.GetChannelNameSet();

        public void SetChannelNames(IDictionary<int, string> channelNames, string actor)
        {
            SetChannelNames(ChannelNameSettings.SharedCoalition, channelNames, actor);
        }

        /// <summary>Replaces one coalition's names (or the shared names); the other sets are unchanged.</summary>
        public void SetChannelNames(int coalition, IDictionary<int, string> channelNames, string actor)
        {
            _settings.SetChannelNames(coalition, channelNames);
            var named = _settings.GetChannelNames(coalition).Count;
            // Shared names keep the original wording so existing event-log searches still match.
            var message = coalition == ChannelNameSettings.SharedCoalition
                ? $"Updated channel names ({named} named channel(s))"
                : $"Updated {ChannelNameSettings.GetCoalitionLabel(coalition)} channel names ({named} named channel(s))";
            PublishChannelNamesChanged(ChannelNameSettings.GetCoalitionLabel(coalition).ToLowerInvariant(), actor,
                message);
            Events.PublishSettingsChanged();
        }

        /// <summary>Replaces the shared, Allied and Axis names together (the Channel Names page).</summary>
        public void SetChannelNames(ChannelNameSet channelNames, string actor)
        {
            _settings.SetChannelNames(channelNames);
            var saved = _settings.GetChannelNameSet();
            PublishChannelNamesChanged("all", actor,
                $"Updated channel names ({saved.Get(ChannelNameSettings.SharedCoalition).Count} shared, " +
                $"{saved.Get(ChannelNameSettings.AlliesCoalition).Count} Allies, " +
                $"{saved.Get(ChannelNameSettings.AxisCoalition).Count} Axis)");
            Events.PublishSettingsChanged();
        }

        private void PublishChannelNamesChanged(string changed, string actor, string message)
        {
            var saved = _settings.GetChannelNameSet();
            var shared = saved.Get(ChannelNameSettings.SharedCoalition).Count;
            var allies = saved.Get(ChannelNameSettings.AlliesCoalition).Count;
            var axis = saved.Get(ChannelNameSettings.AxisCoalition).Count;
            Events.Publish(SrsEventTypes.ChannelNamesChanged, actor, message, new JsonObject
            {
                ["changed"] = changed,
                ["namedChannels"] = shared + allies + axis,
                ["shared"] = shared,
                ["allies"] = allies,
                ["axis"] = axis
            });
        }

        // API keys (admin UI only; keys cannot manage keys)

        public IReadOnlyList<ApiKeyRecord> GetApiKeys() => _apiKeys.List();

        public string CreateApiKey(string name, string scope, string actor, out ApiKeyRecord record)
        {
            try
            {
                var created = _apiKeys.Create(name, scope);
                record = created.Record;
                Events.Publish(SrsEventTypes.ApiKeyCreated, actor,
                    $"Created {scope} API key \"{record.Name}\" ({record.Prefix})", ApiKeyPayload(record));
                return created.Key;
            }
            catch (ArgumentException ex)
            {
                record = null;
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        public bool RevokeApiKey(long id, string actor)
        {
            var key = _apiKeys.List().FirstOrDefault(k => k.Id == id);
            if (key == null || !_apiKeys.Revoke(id))
            {
                return false;
            }

            Events.Publish(SrsEventTypes.ApiKeyRevoked, actor, $"Revoked API key \"{key.Name}\" ({key.Prefix})",
                ApiKeyPayload(key));
            return true;
        }

        private SRClient FindClient(string clientGuid)
        {
            return _serverState.GetClients().FirstOrDefault(c => c.ClientGuid == clientGuid);
        }

        private JsonObject ClientPayload(SRClient client)
        {
            return EventPayload.Client(client, _serverState.GetClientAddress(client.ClientGuid));
        }

        private JsonObject ServerPayload()
        {
            return EventPayload.Server(Port, _serverState.GetClients().Count);
        }

        private static JsonObject ApiKeyPayload(ApiKeyRecord key)
        {
            return new JsonObject
            {
                ["apiKey"] = new JsonObject { ["name"] = key.Name, ["scope"] = key.Scope, ["prefix"] = key.Prefix }
            };
        }

        private static DateTime? ExpiresAt(TimeSpan? duration)
        {
            return duration.HasValue && duration.Value > TimeSpan.Zero ? DateTime.UtcNow + duration.Value : null;
        }

        internal static string Describe(SRClient client)
        {
            return $"{client.Name} ({ClientAdminPresentation.GetCoalitionName(client.Coalition)})";
        }

        private static string DescribeBan(BanRecord ban)
        {
            return (ban.ExpiresAtUtc.HasValue ? $" until {ban.ExpiresAtUtc.Value:yyyy-MM-dd HH:mm} UTC" : " permanently") +
                   (ban.Reason != null ? $": {ban.Reason}" : string.Empty);
        }
    }
}
