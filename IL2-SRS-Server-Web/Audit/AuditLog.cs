using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Ciribob.IL2.SimpleRadio.Standalone.Server.Data;
using Microsoft.Extensions.Hosting;
using NLog;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Audit
{
    public static class AuditCategory
    {
        public const string Server = "server";
        public const string Client = "client";
        public const string Admin = "admin";
        public const string Auth = "auth";

        public static readonly IReadOnlyList<string> All = new[] { Server, Client, Admin, Auth };
    }

    public sealed class AuditEvent
    {
        public long Id { get; init; }
        public DateTime OccurredAtUtc { get; init; }
        public string Category { get; init; }
        public string Actor { get; init; }
        public string Message { get; init; }
    }

    public sealed class AuditRetention
    {
        public const string DaysKey = "SRS_EVENT_RETENTION_DAYS";
        public const string MaxRowsKey = "SRS_EVENT_MAX_ROWS";

        public int Days { get; init; } = 30;
        public int MaxRows { get; init; } = 100_000;
    }

    /// <summary>
    /// The event log admins see in the UI and API: logins, admin actions, server and client events.
    /// Writes are queued and stored in batches so network threads never wait on the database.
    /// Every event is also written to the regular log.
    /// </summary>
    public sealed class AuditLog : BackgroundService
    {
        private static readonly Logger Logger = LogManager.GetLogger("Audit");
        private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);

        private readonly SrsDatabase _database;
        private readonly AuditRetention _retention;
        private readonly Channel<AuditEvent> _queue = Channel.CreateBounded<AuditEvent>(
            new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        public AuditLog(SrsDatabase database, AuditRetention retention)
        {
            _database = database;
            _retention = retention;
        }

        public AuditRetention Retention => _retention;

        public void Write(string category, string actor, string message)
        {
            var auditEvent = new AuditEvent
            {
                OccurredAtUtc = DateTime.UtcNow,
                Category = category,
                Actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor,
                Message = message
            };

            Logger.Info($"[{auditEvent.Category}] {auditEvent.Actor}: {auditEvent.Message}");
            _queue.Writer.TryWrite(auditEvent);
        }

        public IReadOnlyList<AuditEvent> Query(string category = null, string search = null, DateTime? sinceUtc = null,
            int limit = 200)
        {
            limit = Math.Clamp(limit, 1, 1000);

            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(category))
            {
                where.Add("category = $category");
                command.Parameters.AddWithValue("$category", category.Trim());
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                where.Add("(message LIKE $search ESCAPE '\\' OR actor LIKE $search ESCAPE '\\')");
                command.Parameters.AddWithValue("$search", "%" + EscapeLike(search.Trim()) + "%");
            }

            if (sinceUtc.HasValue)
            {
                where.Add("occurred_at >= $since");
                command.Parameters.AddWithValue("$since", SrsDatabase.FormatTimestamp(sinceUtc.Value));
            }

            command.CommandText = "SELECT id, occurred_at, category, actor, message FROM events" +
                                  (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty) +
                                  " ORDER BY id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);

            var events = new List<AuditEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new AuditEvent
                {
                    Id = reader.GetInt64(0),
                    OccurredAtUtc = SrsDatabase.ParseTimestamp(reader.GetString(1)),
                    Category = reader.GetString(2),
                    Actor = reader.GetString(3),
                    Message = reader.GetString(4)
                });
            }

            return events;
        }

        public long Count()
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM events;";
            return (long)command.ExecuteScalar();
        }

        /// <summary>Deletes every stored event, then records who cleared the log.</summary>
        public long Clear(string actor)
        {
            long deleted;
            using (var connection = _database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM events;";
                deleted = command.ExecuteNonQuery();
            }

            Write(AuditCategory.Admin, actor, $"Cleared the event log ({deleted} events)");
            return deleted;
        }

        /// <summary>Removes events older than the retention period and beyond the row limit.</summary>
        public long ApplyRetention(DateTime utcNow)
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
DELETE FROM events WHERE occurred_at < $cutoff;
DELETE FROM events WHERE id <= (SELECT id FROM events ORDER BY id DESC LIMIT 1 OFFSET $maxRows);";
            command.Parameters.AddWithValue("$cutoff", SrsDatabase.FormatTimestamp(utcNow.AddDays(-_retention.Days)));
            command.Parameters.AddWithValue("$maxRows", _retention.MaxRows);
            return command.ExecuteNonQuery();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var nextRetention = DateTime.UtcNow;
            var batch = new List<AuditEvent>();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromMinutes(1));
                    await _queue.Reader.WaitToReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Woke up for the retention check.
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                Flush(batch);

                if (DateTime.UtcNow >= nextRetention)
                {
                    nextRetention = DateTime.UtcNow + RetentionInterval;
                    RunRetention();
                }
            }

            Flush(batch);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            Flush(new List<AuditEvent>());
        }

        private void Flush(List<AuditEvent> batch)
        {
            batch.Clear();
            while (_queue.Reader.TryRead(out var auditEvent))
            {
                batch.Add(auditEvent);
            }

            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                foreach (var auditEvent in batch)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        "INSERT INTO events (occurred_at, category, actor, message) VALUES ($at, $category, $actor, $message);";
                    command.Parameters.AddWithValue("$at", SrsDatabase.FormatTimestamp(auditEvent.OccurredAtUtc));
                    command.Parameters.AddWithValue("$category", auditEvent.Category);
                    command.Parameters.AddWithValue("$actor", auditEvent.Actor);
                    command.Parameters.AddWithValue("$message", auditEvent.Message);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Unable to store {batch.Count} events");
            }
        }

        private void RunRetention()
        {
            try
            {
                var removed = ApplyRetention(DateTime.UtcNow);
                if (removed > 0)
                {
                    Logger.Info(string.Format(CultureInfo.InvariantCulture,
                        "Removed {0} events older than {1} days or beyond {2} rows", removed, _retention.Days,
                        _retention.MaxRows));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Event log retention failed");
            }
        }

        private static string EscapeLike(string value)
        {
            return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        }
    }
}
