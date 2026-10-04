using Ciribob.IL2.SimpleRadio.Standalone.Server.Admin;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting;
using Xunit;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Tests
{
    public class AdminAuthOptionsTests
    {
        [Fact]
        public void MissingPasswordStopsStartup()
        {
            var error = Assert.Throws<ServerStartupException>(() => AdminAuthOptions.Create(null, null, null));
            Assert.Contains(AdminAuthOptions.PasswordKey, error.Message);
        }

        [Fact]
        public void ShortPasswordStopsStartup()
        {
            Assert.Throws<ServerStartupException>(() => AdminAuthOptions.Create("1234567", null, null));
        }

        [Fact]
        public void VerifiesOnlyTheConfiguredPassword()
        {
            var options = AdminAuthOptions.Create("correct-horse", null, null);

            Assert.False(options.Disabled);
            Assert.True(options.Verify("correct-horse"));
            Assert.False(options.Verify("Correct-horse"));
            Assert.False(options.Verify(""));
            Assert.False(options.Verify(null));
        }

        [Fact]
        public void ReadsPasswordFromFileWithoutTrailingNewline()
        {
            using var temp = new TempDirectory();
            var file = temp.File("password");
            System.IO.File.WriteAllText(file, "from-a-secret-file\n");

            var options = AdminAuthOptions.Create(null, file, null);

            Assert.True(options.Verify("from-a-secret-file"));
        }

        [Fact]
        public void CanBeExplicitlyDisabled()
        {
            var options = AdminAuthOptions.Create(null, null, "true");

            Assert.True(options.Disabled);
            Assert.True(options.Verify("anything"));
        }
    }
}
