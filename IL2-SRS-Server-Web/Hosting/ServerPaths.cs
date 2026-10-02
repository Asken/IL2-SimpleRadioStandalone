using System;
using System.IO;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting
{
    /// <summary>
    /// Resolves where the server keeps server.cfg, banned.txt, exports, logs and keys.
    /// In the container this is the /data volume (SRS_DATA_DIR); otherwise the application folder.
    /// </summary>
    public static class ServerPaths
    {
        public const string DataDirectoryVariable = "SRS_DATA_DIR";

        public static string DataDirectory { get; private set; } =
            ResolveDataDirectory(Environment.GetEnvironmentVariable(DataDirectoryVariable), AppContext.BaseDirectory);

        public static void UseDataDirectory(string directory)
        {
            DataDirectory = Path.GetFullPath(directory);
            Directory.CreateDirectory(DataDirectory);
        }

        /// <summary>Returns an absolute path; relative paths are taken relative to the data directory.</summary>
        public static string Resolve(string path)
        {
            return ResolveAgainst(DataDirectory, path);
        }

        internal static string ResolveDataDirectory(string configured, string fallback)
        {
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim());
        }

        internal static string ResolveAgainst(string baseDirectory, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A path is required.", nameof(path));
            }

            path = path.Trim();
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
        }
    }
}
