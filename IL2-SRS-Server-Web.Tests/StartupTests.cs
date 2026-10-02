using System;
using System.IO;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Admin;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting;
using Xunit;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Tests
{
    public class StartupTests
    {
        [Fact]
        public void ParsesDataDirectoryAndImportFileAndPassesTheRestToTheHost()
        {
            var parsed = StartupArguments.Parse(new[]
                { "--data-dir", "C:\\srs\\main", "-cfg=other.cfg", "--urls=http://*:8080" });

            Assert.Equal("C:\\srs\\main", parsed.DataDirectory);
            Assert.Equal("other.cfg", parsed.ImportConfigFile);
            Assert.Equal(new[] { "--urls=http://*:8080" }, parsed.HostArguments);
        }

        [Fact]
        public void ParsesDataDirectoryWithEqualsSign()
        {
            Assert.Equal("/data", StartupArguments.Parse(new[] { "--data-dir=/data" }).DataDirectory);
        }

        [Fact]
        public void DataDirectoryWithoutValueIsRejected()
        {
            Assert.Throws<ArgumentException>(() => StartupArguments.Parse(new[] { "--data-dir" }));
        }

        [Fact]
        public void RelativePathsResolveInsideTheDataDirectory()
        {
            var baseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "srs-data"));

            Assert.Equal(Path.Combine(baseDirectory, "exports", "clients.json"),
                ServerPaths.ResolveAgainst(baseDirectory, "exports/clients.json".Replace('/', Path.DirectorySeparatorChar)));

            var rooted = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsewhere.json"));
            Assert.Equal(rooted, ServerPaths.ResolveAgainst(baseDirectory, rooted));
        }

        [Fact]
        public void DataDirectoryFallsBackWhenNotConfigured()
        {
            var fallback = Path.GetFullPath(Path.GetTempPath());
            Assert.Equal(fallback, ServerPaths.ResolveDataDirectory("  ", fallback));
        }

        [Theory]
        [InlineData("/", true)]
        [InlineData("/clients?x=1", true)]
        [InlineData("//evil.example", false)]
        [InlineData("/\\evil.example", false)]
        [InlineData("https://evil.example", false)]
        [InlineData("clients", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyLocalReturnUrlsAreAccepted(string url, bool expected)
        {
            Assert.Equal(expected, AdminEndpoints.IsLocalUrl(url));
        }
    }
}
