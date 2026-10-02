using System.IO;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting
{
    public static class ServerLogging
    {
        private const string Layout =
            "${longdate} | ${level:uppercase=true} | ${logger} | ${message} ${exception:format=toString,Data:maxInnerExceptionLevel=1}";

        /// <summary>
        /// Logs to the console (picked up by `docker logs`) and to serverlog.txt in the data directory,
        /// matching the file the WPF server writes.
        /// </summary>
        public static LoggingConfiguration Create(string dataDirectory)
        {
            var config = new LoggingConfiguration();

            var console = new ConsoleTarget("console") { Layout = Layout };

            var file = new FileTarget("file")
            {
                FileName = Path.Combine(dataDirectory, "serverlog.txt"),
                ArchiveFileName = Path.Combine(dataDirectory, "serverlog.old.txt"),
                MaxArchiveFiles = 1,
                ArchiveAboveSize = 104857600,
                Layout = Layout
            };
            var asyncFile = new AsyncTargetWrapper("asyncFile", file)
            {
                QueueLimit = 5000,
                OverflowAction = AsyncTargetWrapperOverflowAction.Discard
            };

            // Keep framework chatter down; the hosting lifetime messages (listening URLs) are still useful.
            config.AddRule(LogLevel.Info, LogLevel.Fatal, console, "Microsoft.Hosting.Lifetime", final: true);
            config.AddRule(LogLevel.Info, LogLevel.Fatal, asyncFile, "Microsoft.Hosting.Lifetime", final: true);
            config.AddRule(LogLevel.Trace, LogLevel.Info, new NullTarget(), "Microsoft.*", final: true);
            config.AddRule(LogLevel.Trace, LogLevel.Info, new NullTarget(), "System.Net.Http.*", final: true);
            config.AddRule(LogLevel.Info, LogLevel.Fatal, console);
            config.AddRule(LogLevel.Info, LogLevel.Fatal, asyncFile);

            return config;
        }
    }
}
