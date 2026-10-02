using System;
using System.Collections.Generic;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting
{
    /// <summary>
    /// Arguments handled before ASP.NET Core sees the command line:
    /// --data-dir &lt;path&gt; (or --data-dir=&lt;path&gt;) and the WPF server's -cfg=&lt;file&gt;,
    /// which here names a server.cfg to import on first start.
    /// </summary>
    public sealed class StartupArguments
    {
        public string DataDirectory { get; private set; }
        public string ImportConfigFile { get; private set; }
        public string[] HostArguments { get; private set; } = Array.Empty<string>();

        public static StartupArguments Parse(string[] args)
        {
            var result = new StartupArguments();
            var remaining = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    result.DataDirectory = arg.Substring("--data-dir=".Length);
                }
                else if (string.Equals(arg, "--data-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--data-dir needs a folder path.");
                    }

                    result.DataDirectory = args[++i];
                }
                else if (arg.StartsWith("-cfg=", StringComparison.OrdinalIgnoreCase))
                {
                    result.ImportConfigFile = arg.Substring("-cfg=".Length).Trim();
                }
                else
                {
                    remaining.Add(arg);
                }
            }

            result.HostArguments = remaining.ToArray();
            return result;
        }
    }
}
