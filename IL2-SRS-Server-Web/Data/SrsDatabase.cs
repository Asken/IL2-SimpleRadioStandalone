using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
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

        /// <summary>
        /// Schema changes, in order; migration N takes the database from version N to N + 1. Every migration must be
        /// safe to run again (CREATE ... IF NOT EXISTS; ADD COLUMN is skipped when the column exists): whenever the
        /// database's version differs from <see cref="SchemaVersion"/>, all of them run, so databases numbered by an
        /// older build, a newer one or another branch (upstream's version 2 is the coalition channel names) still
        /// end up with every table and column. A database numbered higher is set back to this build's version, so a
        /// build with more migrations runs them all again rather than skipping its own.
        /// </summary>
        private static readonly string[] Migrations =
        {
            // 1: initial schema.
            @"
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
);",
            // 2: per-coalition channel names (upstream 1.0.5.0).
            @"
CREATE TABLE IF NOT EXISTS coalition_channel_names (
    coalition INTEGER NOT NULL,
    channel   INTEGER NOT NULL,
    name      TEXT NOT NULL,
    PRIMARY KEY (coalition, channel)
);",
            // 3: typed events (type, client and JSON payload; null for older rows).
            @"
ALTER TABLE events ADD COLUMN type TEXT;
ALTER TABLE events ADD COLUMN client_guid TEXT;
ALTER TABLE events ADD COLUMN payload TEXT;
CREATE INDEX IF NOT EXISTS ix_events_type ON events (type);
CREATE INDEX IF NOT EXISTS ix_events_client_guid ON events (client_guid);",
            // 4: integrations and their runs (the outbox the dispatcher works through).
            @"
CREATE TABLE IF NOT EXISTS integrations (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT NOT NULL,
    description   TEXT,
    enabled       INTEGER NOT NULL,
    trigger_type  TEXT NOT NULL,
    conditions    TEXT NOT NULL,
    action_type   TEXT NOT NULL,
    action_config TEXT NOT NULL,
    retry_mode    TEXT NOT NULL,
    max_retries   INTEGER NOT NULL,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS integration_executions (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    integration_id   INTEGER NOT NULL,
    integration_name TEXT NOT NULL,
    action_type      TEXT NOT NULL,
    event_type       TEXT NOT NULL,
    event_message    TEXT,
    data             TEXT NOT NULL,
    status           TEXT NOT NULL,
    attempts         INTEGER NOT NULL,
    created_at       TEXT NOT NULL,
    next_attempt_at  TEXT,
    finished_at      TEXT,
    last_error       TEXT,
    result           TEXT
);
CREATE INDEX IF NOT EXISTS ix_integration_executions_due ON integration_executions (status, next_attempt_at);
CREATE INDEX IF NOT EXISTS ix_integration_executions_integration ON integration_executions (integration_id);",
            // 5: values an action keeps for its integration between runs (e.g. the Discord message it updates).
            @"
CREATE TABLE IF NOT EXISTS integration_state (
    integration_id INTEGER NOT NULL,
    key            TEXT NOT NULL,
    value          TEXT NOT NULL,
    updated_at     TEXT NOT NULL,
    PRIMARY KEY (integration_id, key)
);"
        };

        public static int SchemaVersion => Migrations.Length;

        private static readonly Regex AddColumn = new(@"^\s*ALTER\s+TABLE\s+(\w+)\s+ADD\s+COLUMN\s+(\w+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
            if (version == SchemaVersion)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            foreach (var migration in Migrations)
            {
                foreach (var statement in migration.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(statement) || HasColumnAlready(connection, transaction, statement))
                    {
                        continue;
                    }

                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = statement + ";";
                    command.ExecuteNonQuery();
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version = " + SchemaVersion.ToString(CultureInfo.InvariantCulture) + ";";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            WasCreated = version == 0;
        }

        /// <summary>True for an ALTER TABLE ... ADD COLUMN whose column is already there.</summary>
        private static bool HasColumnAlready(SqliteConnection connection, SqliteTransaction transaction, string statement)
        {
            var match = AddColumn.Match(statement);
            if (!match.Success)
            {
                return false;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM pragma_table_info($table);";
            command.Parameters.AddWithValue("$table", match.Groups[1].Value);
            using var reader = command.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }

            return columns.Contains(match.Groups[2].Value);
        }

        private static int ExecuteScalarInt(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
}
