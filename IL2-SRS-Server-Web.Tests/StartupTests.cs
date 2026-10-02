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
        public void UsesTheApplicationFolderWhenItIsWritable()
        {
            var app = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "srs-app"));
            var programData = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "srs-programdata"));

            Assert.Equal(app, ServerPaths.ChooseDefaultDataDirectory(app, programData, _ => true));
            Assert.Equal(programData, ServerPaths.ChooseDefaultDataDirectory(app, programData, _ => false));
            Assert.Equal(app, ServerPaths.ChooseDefaultDataDirectory(app, null, _ => false));
        }

        [Theory]
        [InlineData(null, null, "http://127.0.0.1:8080/healthz")]
        [InlineData(null, "9090;9091", "http://127.0.0.1:9090/healthz")]
        [InlineData("http://127.0.0.1:1234/healthz", "8080", "http://127.0.0.1:1234/healthz")]
        public void HealthCheckTargetsTheConfiguredHttpPort(string url, string ports, string expected)
        {
            Assert.Equal(expected, HealthCheckCommand.ResolveUrl(url, ports));
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
