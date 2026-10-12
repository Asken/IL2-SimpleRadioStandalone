using System.Collections.Generic;
using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ciribob.IL2.SimpleRadio.Standalone.Common.Tests.Settings
{
    [TestClass]
    public class ChannelNameSetTests
    {
        [TestMethod]
        public void SharedParserIgnoresCoalitionNames()
        {
            // Clients without per-coalition support only have the shared parser, which used the same
            // prefix + int.TryParse check as TryParseChannel; "ALLIES_1" is not a number, so they skip these keys.
            Assert.IsFalse(ChannelNameSettings.TryParseSyncedSettingName("CHANNEL_NAME_ALLIES_1", out _));
            Assert.IsFalse(ChannelNameSettings.TryParseSyncedSettingName("CHANNEL_NAME_AXIS_25", out _));
            Assert.IsTrue(ChannelNameSettings.TryParseSyncedSettingName("CHANNEL_NAME_1", out var channel));
            Assert.AreEqual(1, channel);
        }

        [TestMethod]
        public void CoalitionSettingNamesRoundTrip()
        {
            foreach (var coalition in new[]
                     {
                         ChannelNameSettings.SharedCoalition,
                         ChannelNameSettings.AlliesCoalition,
                         ChannelNameSettings.AxisCoalition
                     })
            {
                var settingName = ChannelNameSettings.GetSyncedSettingName(7, coalition);

                Assert.IsTrue(ChannelNameSettings.TryParseSyncedSettingName(settingName, out var parsedCoalition,
                    out var parsedChannel), settingName);
                Assert.AreEqual(coalition, parsedCoalition, settingName);
                Assert.AreEqual(7, parsedChannel, settingName);
            }

            Assert.AreEqual("CHANNEL_NAME_ALLIES_7", ChannelNameSettings.GetSyncedSettingName(7, ChannelNameSettings.AlliesCoalition));
            Assert.AreEqual("CHANNEL_NAME_AXIS_7", ChannelNameSettings.GetSyncedSettingName(7, ChannelNameSettings.AxisCoalition));
            Assert.AreEqual("CHANNEL_NAME_7", ChannelNameSettings.GetSyncedSettingName(7, ChannelNameSettings.SharedCoalition));
        }

        [TestMethod]
        public void CoalitionParserRejectsOutOfRangeAndMalformedNames()
        {
            Assert.IsFalse(ChannelNameSettings.TryParseSyncedSettingName("CHANNEL_NAME_ALLIES_0", out _, out _));
            Assert.IsFalse(ChannelNameSettings.TryParseSyncedSettingName("CHANNEL_NAME_AXIS_26", out _, out _));
            Assert.IsFalse(ChannelNameSettings.TryParseSyncedSettingName("CHANNEL_NAME_SOVIET_1", out _, out _));
            Assert.IsFalse(ChannelNameSettings.TryParseSyncedSettingName("CHANNEL_LIMIT", out _, out _));
            Assert.IsFalse(ChannelNameSettings.TryParseSyncedSettingName(null, out _, out _));
        }

        [TestMethod]
        public void ResolvePrefersOwnCoalitionThenSharedName()
        {
            var names = new ChannelNameSet(
                new Dictionary<int, string> { [1] = "Command", [2] = "Tower" },
                new Dictionary<int, string> { [1] = "Kenway", [3] = "Dogrose" },
                new Dictionary<int, string> { [1] = "Freya" });

            Assert.AreEqual("Kenway", names.Resolve(1, ChannelNameSettings.AlliesCoalition));
            Assert.AreEqual("Freya", names.Resolve(1, ChannelNameSettings.AxisCoalition));
            Assert.AreEqual("Command", names.Resolve(1, ChannelNameSettings.SharedCoalition));
            Assert.AreEqual("Tower", names.Resolve(2, ChannelNameSettings.AxisCoalition));
            Assert.AreEqual("Dogrose", names.Resolve(3, ChannelNameSettings.AlliesCoalition));
            Assert.IsNull(names.Resolve(3, ChannelNameSettings.AxisCoalition));
            Assert.AreEqual("Command", names.Resolve(1, 3), "Unknown coalitions use the shared names.");
        }

        [TestMethod]
        public void SyncedSettingsRoundTripAndNormalise()
        {
            var original = new ChannelNameSet(
                new Dictionary<int, string> { [1] = "Command", [5] = "  " },
                new Dictionary<int, string> { [1] = " Kenway\r\n", [30] = "Out of range" },
                new Dictionary<int, string> { [2] = "Schlageter" });

            var settings = new Dictionary<string, string> { ["CHANNEL_LIMIT"] = "5" };
            original.AddSyncedSettings(settings);

            Assert.AreEqual("Command", settings["CHANNEL_NAME_1"]);
            Assert.AreEqual("Kenway", settings["CHANNEL_NAME_ALLIES_1"]);
            Assert.AreEqual("Schlageter", settings["CHANNEL_NAME_AXIS_2"]);
            Assert.IsFalse(settings.ContainsKey("CHANNEL_NAME_5"), "Blank names are dropped.");
            Assert.IsFalse(settings.ContainsKey("CHANNEL_NAME_ALLIES_30"), "Out-of-range channels are dropped.");

            var decoded = ChannelNameSet.FromSyncedSettings(settings);
            CollectionAssert.AreEquivalent(original.Get(ChannelNameSettings.SharedCoalition), decoded.Get(ChannelNameSettings.SharedCoalition));
            CollectionAssert.AreEquivalent(original.Get(ChannelNameSettings.AlliesCoalition), decoded.Get(ChannelNameSettings.AlliesCoalition));
            CollectionAssert.AreEquivalent(original.Get(ChannelNameSettings.AxisCoalition), decoded.Get(ChannelNameSettings.AxisCoalition));
        }

        [TestMethod]
        public void WithReplacesOnlyTheGivenSet()
        {
            var names = new ChannelNameSet(
                new Dictionary<int, string> { [1] = "Command" },
                new Dictionary<int, string> { [1] = "Kenway" },
                new Dictionary<int, string> { [1] = "Freya" });

            var updated = names.With(ChannelNameSettings.AxisCoalition, new Dictionary<int, string> { [2] = "Schlageter" });

            Assert.AreEqual("Command", updated.Resolve(1, ChannelNameSettings.SharedCoalition));
            Assert.AreEqual("Kenway", updated.Resolve(1, ChannelNameSettings.AlliesCoalition));
            Assert.AreEqual("Command", updated.Resolve(1, ChannelNameSettings.AxisCoalition), "Axis channel 1 was cleared.");
            Assert.AreEqual("Schlageter", updated.Resolve(2, ChannelNameSettings.AxisCoalition));
            Assert.AreEqual("Freya", names.Resolve(1, ChannelNameSettings.AxisCoalition), "The original set is unchanged.");
        }
    }
}
