using System.Collections.Generic;

namespace Ciribob.IL2.SimpleRadio.Standalone.Common.Setting
{
    /// <summary>
    /// Shared channel names plus optional Allied and Axis names that replace them for that coalition.
    /// Immutable, so readers can hold a reference while a new set is swapped in.
    /// </summary>
    public sealed class ChannelNameSet
    {
        public static readonly ChannelNameSet Empty = new ChannelNameSet(null, null, null);

        private readonly Dictionary<int, string> _shared;
        private readonly Dictionary<int, string> _allies;
        private readonly Dictionary<int, string> _axis;

        public ChannelNameSet(IDictionary<int, string> shared, IDictionary<int, string> allies, IDictionary<int, string> axis)
        {
            _shared = Normalize(shared);
            _allies = Normalize(allies);
            _axis = Normalize(axis);
        }

        public bool IsEmpty => _shared.Count == 0 && _allies.Count == 0 && _axis.Count == 0;

        /// <summary>The names stored for one coalition; SharedCoalition (or any other value) gives the shared names.</summary>
        public Dictionary<int, string> Get(int coalition)
        {
            return new Dictionary<int, string>(For(coalition));
        }

        /// <summary>The coalition's own name for the channel, else the shared name, else null.</summary>
        public string Resolve(int channel, int coalition)
        {
            if (For(coalition).TryGetValue(channel, out var name))
            {
                return name;
            }

            return _shared.TryGetValue(channel, out name) ? name : null;
        }

        /// <summary>The names a client in this coalition sees: shared names overlaid with its own coalition's names.</summary>
        public Dictionary<int, string> GetEffective(int coalition)
        {
            var effective = new Dictionary<int, string>(_shared);
            if (ChannelNameSettings.IsCoalition(coalition))
            {
                foreach (var channelName in For(coalition))
                {
                    effective[channelName.Key] = channelName.Value;
                }
            }

            return effective;
        }

        /// <summary>Returns a copy with one coalition's names (or the shared names) replaced.</summary>
        public ChannelNameSet With(int coalition, IDictionary<int, string> names)
        {
            switch (coalition)
            {
                case ChannelNameSettings.AlliesCoalition:
                    return new ChannelNameSet(_shared, names, _axis);
                case ChannelNameSettings.AxisCoalition:
                    return new ChannelNameSet(_shared, _allies, names);
                default:
                    return new ChannelNameSet(names, _allies, _axis);
            }
        }

        /// <summary>Adds the synced settings (CHANNEL_NAME_n, CHANNEL_NAME_ALLIES_n, CHANNEL_NAME_AXIS_n) sent to clients.</summary>
        public void AddSyncedSettings(IDictionary<string, string> settings)
        {
            foreach (var channelName in _shared)
            {
                settings[ChannelNameSettings.GetSyncedSettingName(channelName.Key)] = channelName.Value;
            }

            foreach (var coalition in ChannelNameSettings.Coalitions)
            {
                foreach (var channelName in For(coalition))
                {
                    settings[ChannelNameSettings.GetSyncedSettingName(channelName.Key, coalition)] = channelName.Value;
                }
            }
        }

        /// <summary>Reads every CHANNEL_NAME_* entry from synced settings; other entries are ignored.</summary>
        public static ChannelNameSet FromSyncedSettings(IEnumerable<KeyValuePair<string, string>> settings)
        {
            var shared = new Dictionary<int, string>();
            var allies = new Dictionary<int, string>();
            var axis = new Dictionary<int, string>();

            foreach (var setting in settings)
            {
                if (!ChannelNameSettings.TryParseSyncedSettingName(setting.Key, out var coalition, out var channel))
                {
                    continue;
                }

                switch (coalition)
                {
                    case ChannelNameSettings.AlliesCoalition:
                        allies[channel] = setting.Value;
                        break;
                    case ChannelNameSettings.AxisCoalition:
                        axis[channel] = setting.Value;
                        break;
                    default:
                        shared[channel] = setting.Value;
                        break;
                }
            }

            return new ChannelNameSet(shared, allies, axis);
        }

        private Dictionary<int, string> For(int coalition)
        {
            switch (coalition)
            {
                case ChannelNameSettings.AlliesCoalition:
                    return _allies;
                case ChannelNameSettings.AxisCoalition:
                    return _axis;
                default:
                    return _shared;
            }
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
    }
}
