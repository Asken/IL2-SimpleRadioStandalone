using Ciribob.IL2.SimpleRadio.Standalone.Common.Setting;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Admin;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Api;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Components.Shared;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Settings;
using Xunit;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Tests
{
    public class FormValidationTests
    {
        [Fact]
        public void FieldErrorsShowOnceTheFieldIsLeftOrTheFormSubmitted()
        {
            var form = new FormState();
            Assert.Null(form.Show("name", "Enter a name."));

            form.Touch("name");
            Assert.Equal("Enter a name.", form.Show("name", "Enter a name."));
            Assert.Null(form.Show("ip", "Enter an IP."));
            Assert.Null(form.Show("name", null));

            form.Submit();
            Assert.Equal("Enter an IP.", form.Show("ip", "Enter an IP."));

            form.Reset();
            Assert.Null(form.Show("name", "Enter a name."));
        }

        [Theory]
        [InlineData(null, "Enter a name for the key.")]
        [InlineData("   ", "Enter a name for the key.")]
        [InlineData("Discord bot", null)]
        public void ChecksApiKeyNames(string name, string expected)
        {
            Assert.Equal(expected, ApiKeyStore.ValidateName(name));
        }

        [Fact]
        public void RejectsApiKeyNamesThatAreTooLong()
        {
            Assert.Equal("The name can be at most 64 characters.", ApiKeyStore.ValidateName(new string('k', 65)));
            Assert.Null(ApiKeyStore.ValidateName(new string('k', 64)));
        }

        [Theory]
        [InlineData("", "Enter the IP address to ban.")]
        [InlineData("not an ip", "Enter a valid IPv4 or IPv6 address, e.g. 203.0.113.7.")]
        [InlineData(" 203.0.113.7 ", null)]
        [InlineData("2001:db8::1", null)]
        public void ChecksBanAddresses(string address, string expected)
        {
            Assert.Equal(expected, ServerAdminService.ValidateIpAddress(address));
        }

        [Fact]
        public void SettingErrorsReadAsInstructionsInTheUiAndNameTheSettingElsewhere()
        {
            var channelLimit = SettingDefinition.For(ServerSettingsKeys.CHANNEL_LIMIT);
            var range = $"a whole number from {channelLimit.Minimum} to {channelLimit.Maximum}";

            Assert.Equal($"Enter {range}.", channelLimit.Validate("lots"));
            Assert.Equal($"CHANNEL_LIMIT must be {range}.", channelLimit.Normalize("lots", out _));
            Assert.Null(channelLimit.Validate(channelLimit.Minimum.ToString()));
        }
    }
}
