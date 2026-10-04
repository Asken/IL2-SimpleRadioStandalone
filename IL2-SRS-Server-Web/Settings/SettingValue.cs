using System.Globalization;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Settings
{
    public enum SettingSource
    {
        Default,
        Database,

        /// <summary>Set by environment, command line or srs-server.json; cannot be changed from the UI or API.</summary>
        Override
    }

    /// <summary>An effective setting value. Mirrors the StringValue/BoolValue/IntValue shape of SharpConfig settings.</summary>
    public sealed class SettingValue
    {
        public SettingValue(string stringValue, SettingSource source)
        {
            StringValue = stringValue ?? string.Empty;
            Source = source;
            BoolValue = TryParseBool(StringValue, out var flag) && flag;
            IntValue = int.TryParse(StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0;
        }

        public string StringValue { get; }
        public bool BoolValue { get; }
        public int IntValue { get; }
        public SettingSource Source { get; }
        public bool IsLocked => Source == SettingSource.Override;

        public static bool TryParseBool(string value, out bool result)
        {
            value = (value ?? string.Empty).Trim();
            if (bool.TryParse(value, out result))
            {
                return true;
            }

            switch (value.ToLowerInvariant())
            {
                case "1":
                case "yes":
                case "on":
                    result = true;
                    return true;
                case "0":
                case "no":
                case "off":
                    result = false;
                    return true;
                default:
                    result = false;
                    return false;
            }
        }
    }
}
