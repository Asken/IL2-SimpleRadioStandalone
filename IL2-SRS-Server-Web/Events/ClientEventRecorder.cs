using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using Ciribob.IL2.SimpleRadio.Standalone.Common;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Admin;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Network;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Settings;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Events
{
    /// <summary>
    /// Turns the network layer's client notifications into typed events: connections, refused connections,
    /// name and coalition changes, and radio retuning. Radio changes are reported once a radio has settled,
    /// so turning the dial through several channels produces one event.
    /// </summary>
    public sealed class ClientEventRecorder : IDisposable
    {
        public static readonly TimeSpan RadioSettleTime = TimeSpan.FromSeconds(2);

        private readonly ServerEvents _events;
        private readonly ServerSettingsStore _settings;
        private readonly ConcurrentDictionary<string, string> _clientAddresses = new();
        private readonly Dictionary<(string ClientGuid, int Radio), PendingRadioChange> _pendingRadios = new();
        private readonly object _pendingLock = new();
        private readonly Timer _timer;

        public ClientEventRecorder(ServerEvents events, ServerSettingsStore settings)
        {
            _events = events;
            _settings = settings;
            events.ClientConnected += OnClientConnected;
            events.ClientDisconnected += OnClientDisconnected;
            events.ConnectionRejected += OnConnectionRejected;
            events.ClientUpdated += OnClientUpdated;
            _timer = new Timer(_ => FlushRadioChanges(DateTime.UtcNow), null, TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500));
        }

        public void Dispose()
        {
            _timer.Dispose();
            _events.ClientConnected -= OnClientConnected;
            _events.ClientDisconnected -= OnClientDisconnected;
            _events.ConnectionRejected -= OnConnectionRejected;
            _events.ClientUpdated -= OnClientUpdated;
        }

        private void OnClientConnected(SRClient client, string ipAddress)
        {
            _clientAddresses[client.ClientGuid] = ipAddress;
            _events.Publish(SrsEventTypes.ClientConnected, "system",
                $"{ServerAdminService.Describe(client)} connected from {ipAddress}",
                EventPayload.Client(client, ipAddress));
        }

        private void OnClientDisconnected(SRClient client)
        {
            _clientAddresses.TryRemove(client.ClientGuid, out var ipAddress);
            lock (_pendingLock)
            {
                _pendingRadios.Remove((client.ClientGuid, 1));
                _pendingRadios.Remove((client.ClientGuid, 2));
            }

            _events.Publish(SrsEventTypes.ClientDisconnected, "system",
                $"{ServerAdminService.Describe(client)} disconnected", EventPayload.Client(client, ipAddress));
        }

        private void OnConnectionRejected(string ipAddress, string reason)
        {
            _events.Publish(SrsEventTypes.ClientRejected, "system", $"Refused connection from {ipAddress}: {reason}",
                EventPayload.Remote(ipAddress).With("reason", reason));
        }

        private void OnClientUpdated(SRClient client, string previousName, int previousCoalition,
            PlayerGameState previousGameState)
        {
            var ipAddress = _clientAddresses.TryGetValue(client.ClientGuid, out var address) ? address : null;

            if (!string.IsNullOrEmpty(previousName) && !string.Equals(previousName, client.Name, StringComparison.Ordinal))
            {
                _events.Publish(SrsEventTypes.ClientNameChanged, "system",
                    $"{previousName} is now {ServerAdminService.Describe(client)}",
                    EventPayload.Client(client, ipAddress).With("previousName", previousName));
            }

            if (previousCoalition != client.Coalition)
            {
                var previousCoalitionName = ClientAdminPresentation.GetCoalitionName(previousCoalition);
                _events.Publish(SrsEventTypes.ClientCoalitionChanged, "system",
                    $"{client.Name} moved from {previousCoalitionName} to " +
                    ClientAdminPresentation.GetCoalitionName(client.Coalition),
                    EventPayload.Client(client, ipAddress)
                        .With("previousCoalition", previousCoalition)
                        .With("previousCoalitionName", previousCoalitionName));
            }

            var current = client.GameState;
            if (previousGameState == null || current == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            for (var index = 1; index <= 2; index++)
            {
                var before = RadioTuning.From(previousGameState, index);
                var after = RadioTuning.From(current, index);
                if (before.SameAs(after))
                {
                    continue;
                }

                lock (_pendingLock)
                {
                    var key = (client.ClientGuid, index);
                    if (_pendingRadios.TryGetValue(key, out var pending))
                    {
                        pending.Current = after;
                        pending.SettlesAtUtc = now + RadioSettleTime;
                    }
                    else
                    {
                        _pendingRadios[key] = new PendingRadioChange
                        {
                            Client = client,
                            Radio = index,
                            Previous = before,
                            Current = after,
                            SettlesAtUtc = now + RadioSettleTime
                        };
                    }
                }
            }
        }

        /// <summary>Publishes radio changes that have not changed again for <see cref="RadioSettleTime"/>.</summary>
        internal void FlushRadioChanges(DateTime utcNow)
        {
            List<PendingRadioChange> settled = null;
            lock (_pendingLock)
            {
                foreach (var pair in _pendingRadios)
                {
                    if (pair.Value.SettlesAtUtc <= utcNow)
                    {
                        (settled ??= new List<PendingRadioChange>()).Add(pair.Value);
                    }
                }

                if (settled == null)
                {
                    return;
                }

                foreach (var change in settled)
                {
                    _pendingRadios.Remove((change.Client.ClientGuid, change.Radio));
                }
            }

            foreach (var change in settled)
            {
                // Tuned away and back again while settling: nothing to report.
                if (!change.Previous.SameAs(change.Current))
                {
                    PublishRadioChange(change);
                }
            }
        }

        private void PublishRadioChange(PendingRadioChange change)
        {
            var client = change.Client;
            var ipAddress = _clientAddresses.TryGetValue(client.ClientGuid, out var address) ? address : null;
            var current = change.Current;
            var previous = change.Previous;
            // The name the pilot sees: their coalition's own name, else the shared one.
            var channelName = current.Channel > 0
                ? _settings.GetChannelNameSet().Resolve(current.Channel, client.Coalition)
                : null;

            var payload = EventPayload.Client(client, ipAddress).With("radio", new JsonObject
            {
                ["index"] = change.Radio,
                ["enabled"] = current.Enabled,
                ["channel"] = current.Channel > 0 ? current.Channel : null,
                ["channelName"] = channelName,
                ["frequencyMhz"] = current.Enabled ? current.FrequencyMhz : null,
                ["modulation"] = current.Enabled ? current.Modulation.ToString() : null,
                ["previousChannel"] = previous.Channel > 0 ? previous.Channel : null,
                ["previousFrequencyMhz"] = previous.Enabled ? previous.FrequencyMhz : null
            });

            string message;
            if (!current.Enabled)
            {
                message = $"{ServerAdminService.Describe(client)} turned off radio {change.Radio}";
            }
            else
            {
                var tuning = current.Channel > 0
                    ? $"channel {current.Channel}{(channelName != null ? $" \"{channelName}\"" : string.Empty)} ({FormatMhz(current.FrequencyMhz)})"
                    : FormatMhz(current.FrequencyMhz);
                message = $"{ServerAdminService.Describe(client)} tuned radio {change.Radio} to {tuning}";
            }

            _events.Publish(SrsEventTypes.RadioFrequencyChanged, "system", message, payload);
        }

        private static string FormatMhz(double mhz)
        {
            return mhz.ToString("0.000", CultureInfo.InvariantCulture) + " MHz";
        }

        private sealed class PendingRadioChange
        {
            public SRClient Client { get; init; }
            public int Radio { get; init; }
            public RadioTuning Previous { get; init; }
            public RadioTuning Current { get; set; }
            public DateTime SettlesAtUtc { get; set; }
        }

        private readonly record struct RadioTuning(bool Enabled, double Frequency, RadioInformation.Modulation Modulation,
            int Channel)
        {
            public double FrequencyMhz => Math.Round(Frequency / 1_000_000d, 3);

            public static RadioTuning From(PlayerGameState state, int index)
            {
                var radio = state.radios != null && state.radios.Length > index ? state.radios[index] : null;
                if (radio == null || radio.modulation == RadioInformation.Modulation.DISABLED ||
                    radio.modulation == RadioInformation.Modulation.INTERCOM)
                {
                    return default;
                }

                return new RadioTuning(true, radio.freq, radio.modulation, radio.Channel);
            }

            public bool SameAs(RadioTuning other)
            {
                if (!Enabled || !other.Enabled)
                {
                    return Enabled == other.Enabled;
                }

                return Modulation == other.Modulation && PlayerGameState.FreqCloseEnough(Frequency, other.Frequency);
            }
        }
    }
}
