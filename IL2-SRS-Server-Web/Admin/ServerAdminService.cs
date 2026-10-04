using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Api;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Audit;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Settings;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Admin
{
    /// <summary>
    /// Everything the admin UI and the API read or change. Every change is recorded in the event log
    /// with the actor ("admin" for the UI, "api:&lt;key name&gt;" for API keys).
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
                Audit.Write(AuditCategory.Server, actor, $"Started the server on port {_serverState.Port}");
                return null;
            }
            catch (Exception ex)
            {
                Audit.Write(AuditCategory.Server, actor, "Failed to start the server: " + ex.Message);
                return ex.Message;
            }
        }

        public void Stop(string actor)
        {
            if (!_serverState.IsRunning)
            {
                return;
            }

            _serverState.Stop();
            Audit.Write(AuditCategory.Server, actor, "Stopped the server");
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
            if (client == null || !_serverState.KickClient(clientGuid))
            {
                return false;
            }

            Audit.Write(AuditCategory.Admin, actor, $"Kicked {Describe(client)}");
            return true;
        }

        public BanRecord BanClient(string clientGuid, string reason, TimeSpan? duration, string actor)
        {
            var client = FindClient(clientGuid);
            if (client == null)
            {
                return null;
            }

            var ban = _serverState.BanClient(clientGuid, reason, actor, ExpiresAt(duration));
            if (ban != null)
            {
                Audit.Write(AuditCategory.Admin, actor, $"Banned {Describe(client)} at {ban.IpAddress}{DescribeBan(ban)}");
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

            Audit.Write(AuditCategory.Admin, actor, $"{(muted ? "Muted" : "Unmuted")} {Describe(client)}");
            return true;
        }

        // Bans

        public IReadOnlyList<BanRecord> GetBans() => _bans.List();

        public int ActiveBanCount => _bans.ActiveCount;

        /// <summary>Bans an IP address directly; returns an error message or null.</summary>
        public string BanAddress(string ipAddress, string playerName, string reason, TimeSpan? duration, string actor,
            out BanRecord ban)
        {
            ban = null;
            if (!IPAddress.TryParse((ipAddress ?? string.Empty).Trim(), out var address))
            {
                return "Enter a valid IPv4 or IPv6 address.";
            }

            ban = _bans.Add(address, playerName, reason, actor, ExpiresAt(duration));
            var disconnected = _serverState.DisconnectAddress(address);
            Audit.Write(AuditCategory.Admin, actor,
                $"Banned {ban.IpAddress}{DescribeBan(ban)}" +
                (disconnected > 0 ? $", disconnected {disconnected} client(s)" : string.Empty));
            return null;
        }

        public bool Unban(long banId, string actor)
        {
            var ban = _bans.Remove(banId);
            if (ban == null)
            {
                return false;
            }

            Audit.Write(AuditCategory.Admin, actor,
                $"Removed ban on {ban.IpAddress}" + (ban.PlayerName != null ? $" ({ban.PlayerName})" : string.Empty));
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

            Audit.Write(AuditCategory.Admin, actor, definition.Secret
                ? $"Changed {key}"
                : $"Changed {key} from \"{previous}\" to \"{current}\"" +
                  (definition.RequiresRestart && _serverState.IsRunning ? " (applies after a server restart)" : string.Empty));

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

        public void SetChannelNames(IDictionary<int, string> channelNames, string actor)
        {
            _settings.SetChannelNames(channelNames);
            var named = _settings.GetChannelNames().Count;
            Audit.Write(AuditCategory.Admin, actor, $"Updated channel names ({named} named channel(s))");
            Events.PublishSettingsChanged();
        }

        // API keys (admin UI only; keys cannot manage keys)

        public IReadOnlyList<ApiKeyRecord> GetApiKeys() => _apiKeys.List();

        public string CreateApiKey(string name, string scope, string actor, out ApiKeyRecord record)
        {
            try
            {
                var created = _apiKeys.Create(name, scope);
                record = created.Record;
                Audit.Write(AuditCategory.Admin, actor, $"Created {scope} API key \"{record.Name}\" ({record.Prefix})");
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

            Audit.Write(AuditCategory.Admin, actor, $"Revoked API key \"{key.Name}\" ({key.Prefix})");
            return true;
        }

        private SRClient FindClient(string clientGuid)
        {
            return _serverState.GetClients().FirstOrDefault(c => c.ClientGuid == clientGuid);
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
