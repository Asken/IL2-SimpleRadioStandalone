using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Data;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Api
{
    public static class ApiScope
    {
        /// <summary>Status, clients, settings, bans, channel names and events.</summary>
        public const string Read = "read";

        /// <summary>Everything in read, plus changing settings, kick/ban/mute, bans and clearing events.</summary>
        public const string Write = "write";

        public static bool IsValid(string scope) => scope == Read || scope == Write;
    }

    public sealed class ApiKeyRecord
    {
        public long Id { get; init; }
        public string Name { get; init; }
        public string Scope { get; init; }
        public string Prefix { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? LastUsedAtUtc { get; init; }
        public DateTime? RevokedAtUtc { get; init; }

        public bool IsRevoked => RevokedAtUtc.HasValue;
    }

    /// <summary>
    /// API keys look like "srs_&lt;prefix&gt;_&lt;secret&gt;". Only a SHA-256 hash of the full key is stored;
    /// the key itself is shown once when it is created.
    /// </summary>
    public sealed class ApiKeyStore
    {
        private const string KeyMarker = "srs_";
        private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

        private readonly SrsDatabase _database;

        public ApiKeyStore(SrsDatabase database)
        {
            _database = database;
        }

        /// <summary>Creates a key and returns it in plain text; it cannot be retrieved later.</summary>
        public (ApiKeyRecord Record, string Key) Create(string name, string scope)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > 64)
            {
                throw new ArgumentException("API key name must be 1 to 64 characters.", nameof(name));
            }

            if (!ApiScope.IsValid(scope))
            {
                throw new ArgumentException($"Scope must be \"{ApiScope.Read}\" or \"{ApiScope.Write}\".", nameof(scope));
            }

            var prefix = RandomToken(6);
            var key = KeyMarker + prefix + "_" + RandomToken(32);
            var createdAt = DateTime.UtcNow;

            long id;
            using (var connection = _database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO api_keys (name, scope, prefix, key_hash, created_at) VALUES ($name, $scope, $prefix, $hash, $created);
SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$scope", scope);
                command.Parameters.AddWithValue("$prefix", prefix);
                command.Parameters.AddWithValue("$hash", Hash(key));
                command.Parameters.AddWithValue("$created", SrsDatabase.FormatTimestamp(createdAt));
                id = (long)command.ExecuteScalar();
            }

            return (new ApiKeyRecord { Id = id, Name = name, Scope = scope, Prefix = prefix, CreatedAtUtc = createdAt },
                key);
        }

        /// <summary>Returns the active key matching <paramref name="key"/>, or null.</summary>
        public ApiKeyRecord Validate(string key)
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyMarker, StringComparison.Ordinal) || key.Length > 200)
            {
                return null;
            }

            ApiKeyRecord record = null;
            using var connection = _database.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT id, name, scope, prefix, created_at, last_used_at, revoked_at FROM api_keys
WHERE key_hash = $hash AND revoked_at IS NULL;";
                command.Parameters.AddWithValue("$hash", Hash(key));
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    record = Read(reader);
                }
            }

            if (record == null)
            {
                return null;
            }

            var now = DateTime.UtcNow;
            if (record.LastUsedAtUtc == null || now - record.LastUsedAtUtc.Value > LastUsedPrecision)
            {
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE api_keys SET last_used_at = $now WHERE id = $id;";
                update.Parameters.AddWithValue("$now", SrsDatabase.FormatTimestamp(now));
                update.Parameters.AddWithValue("$id", record.Id);
                update.ExecuteNonQuery();
            }

            return record;
        }

        public IReadOnlyList<ApiKeyRecord> List()
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, name, scope, prefix, created_at, last_used_at, revoked_at FROM api_keys ORDER BY id DESC;";
            using var reader = command.ExecuteReader();
            var keys = new List<ApiKeyRecord>();
            while (reader.Read())
            {
                keys.Add(Read(reader));
            }

            return keys;
        }

        public bool Revoke(long id)
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE api_keys SET revoked_at = $now WHERE id = $id AND revoked_at IS NULL;";
            command.Parameters.AddWithValue("$now", SrsDatabase.FormatTimestamp(DateTime.UtcNow));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }

        private static ApiKeyRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader)
        {
            return new ApiKeyRecord
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Scope = reader.GetString(2),
                Prefix = reader.GetString(3),
                CreatedAtUtc = SrsDatabase.ParseTimestamp(reader.GetString(4)),
                LastUsedAtUtc = reader.IsDBNull(5) ? null : SrsDatabase.ParseTimestamp(reader.GetString(5)),
                RevokedAtUtc = reader.IsDBNull(6) ? null : SrsDatabase.ParseTimestamp(reader.GetString(6))
            };
        }

        private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

        private static string RandomToken(int bytes)
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
                .TrimEnd('=').Replace('+', '-').Replace('/', 'x');
        }
    }
}
