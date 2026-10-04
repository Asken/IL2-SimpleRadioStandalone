using System;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Data
{
    /// <summary>
    /// The server's SQLite database (srs.db in the data directory): settings, channel names,
    /// bans, the event log and API keys. Connections are pooled; open one per operation.
    /// </summary>
    public sealed class SrsDatabase
    {
        public const string FileName = "srs.db";

        private const int SchemaVersion = 1;

        private readonly string _connectionString;

        public SrsDatabase(string databasePath)
        {
            DatabasePath = databasePath;
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = true,
                DefaultTimeout = 30
            }.ToString();

            Initialize();
        }

        public string DatabasePath { get; }

        /// <summary>True when this process created the database; used to run one-off imports.</summary>
        public bool WasCreated { get; private set; }

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        public static string FormatTimestamp(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        public static DateTime ParseTimestamp(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        public static DateTime? ParseNullableTimestamp(object value)
        {
            return value is string text && text.Length > 0 ? ParseTimestamp(text) : (DateTime?)null;
        }

        private void Initialize()
        {
            using var connection = Open();

            using (var pragma = connection.CreateCommand())
            {
                // WAL lets the admin UI read while the server writes.
                pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                pragma.ExecuteNonQuery();
            }

            var version = ExecuteScalarInt(connection, "PRAGMA user_version;");
            if (version >= SchemaVersion)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS settings (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS channel_names (
    channel INTEGER PRIMARY KEY,
    name    TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS bans (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ip          TEXT NOT NULL,
    player_name TEXT,
    reason      TEXT,
    created_by  TEXT NOT NULL,
    created_at  TEXT NOT NULL,
    expires_at  TEXT
);
CREATE INDEX IF NOT EXISTS ix_bans_ip ON bans (ip);
CREATE TABLE IF NOT EXISTS events (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at TEXT NOT NULL,
    category    TEXT NOT NULL,
    actor       TEXT NOT NULL,
    message     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_events_occurred_at ON events (occurred_at);
CREATE TABLE IF NOT EXISTS api_keys (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    name         TEXT NOT NULL,
    scope        TEXT NOT NULL,
    prefix       TEXT NOT NULL UNIQUE,
    key_hash     BLOB NOT NULL UNIQUE,
    created_at   TEXT NOT NULL,
    last_used_at TEXT,
    revoked_at   TEXT
);
PRAGMA user_version = " + SchemaVersion.ToString(CultureInfo.InvariantCulture) + ";";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            WasCreated = version == 0;
        }

        private static int ExecuteScalarInt(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
}
