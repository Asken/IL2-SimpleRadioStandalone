using System;
using System.IO;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Hosting
{
    /// <summary>
    /// Resolves the data directory (srs.db, logs, exports, keys): --data-dir, else SRS_DATA_DIR (/data in the
    /// container), else the application folder. On Windows, when the application folder is not writable
    /// (e.g. under Program Files), %ProgramData%\IL2-SRS-Server is used instead.
    /// </summary>
    public static class ServerPaths
    {
        public const string DataDirectoryVariable = "SRS_DATA_DIR";

        public const string WindowsFallbackFolderName = "IL2-SRS-Server";

        public static string DataDirectory { get; private set; } = Path.GetFullPath(AppContext.BaseDirectory);

        /// <summary>The data directory to use when --data-dir is not given.</summary>
        public static string ResolveDefaultDataDirectory()
        {
            var configured = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured.Trim());
            }

            var fallback = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    WindowsFallbackFolderName)
                : null;
            return ChooseDefaultDataDirectory(AppContext.BaseDirectory, fallback, IsWritable);
        }

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

        internal static string ChooseDefaultDataDirectory(string applicationDirectory, string fallbackDirectory,
            Func<string, bool> isWritable)
        {
            applicationDirectory = Path.GetFullPath(applicationDirectory);
            return fallbackDirectory == null || isWritable(applicationDirectory)
                ? applicationDirectory
                : Path.GetFullPath(fallbackDirectory);
        }

        private static bool IsWritable(string directory)
        {
            var probe = Path.Combine(directory, ".srs-write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
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
