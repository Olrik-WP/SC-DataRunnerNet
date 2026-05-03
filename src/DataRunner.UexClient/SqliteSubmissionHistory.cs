using System.Globalization;
using System.Text.Json;
using DataRunner.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace DataRunner.UexClient;

/// <summary>
/// SQLite-backed audit log of every UEX submission attempt.
/// File is stored in %LOCALAPPDATA%\SC-DataRunnerNet\history.sqlite by default.
/// </summary>
public sealed class SqliteSubmissionHistory : ISubmissionHistory
{
    private readonly string _connectionString;

    public SqliteSubmissionHistory(string? overrideDbPath = null)
    {
        var dbPath = overrideDbPath ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS submissions (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              at_unix_seconds INTEGER NOT NULL,
              id_terminal INTEGER NOT NULL,
              terminal_display_name TEXT,
              is_production INTEGER NOT NULL,
              ok INTEGER NOT NULL,
              http_status_code INTEGER NOT NULL,
              api_status TEXT,
              api_message TEXT,
              source_image TEXT,
              request_json TEXT NOT NULL,
              response_json TEXT NOT NULL,
              submitted_commodity_ids TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_submissions_terminal_at ON submissions (id_terminal, at_unix_seconds DESC);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Schema migration: add `source_images` (JSON array of filenames) for
        // merged submissions. Safe to call repeatedly: SQLite's PRAGMA + INFO
        // check tells us whether the column already exists. We don't backfill
        // legacy rows — `GetSubmittedSourceImagesAsync` falls back to
        // source_image when source_images is NULL.
        if (!await ColumnExistsAsync(conn, "submissions", "source_images", ct))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE submissions ADD COLUMN source_images TEXT NULL;";
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            // PRAGMA table_info layout: cid, name, type, notnull, dflt_value, pk
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task<long> RecordAsync(SubmissionRecord record, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO submissions
              (at_unix_seconds, id_terminal, terminal_display_name, is_production, ok,
               http_status_code, api_status, api_message, source_image, source_images,
               request_json, response_json, submitted_commodity_ids)
            VALUES
              ($at, $term, $name, $prod, $ok, $code, $status, $msg, $img, $imgs, $req, $resp, $ids);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$at", record.At.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$term", record.IdTerminal);
        cmd.Parameters.AddWithValue("$name", (object?)record.TerminalDisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prod", record.IsProduction ? 1 : 0);
        cmd.Parameters.AddWithValue("$ok", record.Ok ? 1 : 0);
        cmd.Parameters.AddWithValue("$code", record.HttpStatusCode);
        cmd.Parameters.AddWithValue("$status", (object?)record.ApiStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msg", (object?)record.ApiMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$img", (object?)record.SourceImage ?? DBNull.Value);
        // SourceImages is empty for legacy callers / single-shot pre-merge code.
        // Don't persist an empty JSON array as it adds nothing — keep NULL so
        // the union query in GetSubmittedSourceImagesAsync stays cheap.
        var imgsJson = (record.SourceImages?.Count ?? 0) > 0
            ? (object)JsonSerializer.Serialize(record.SourceImages)
            : DBNull.Value;
        cmd.Parameters.AddWithValue("$imgs", imgsJson);
        cmd.Parameters.AddWithValue("$req", record.RequestJson);
        cmd.Parameters.AddWithValue("$resp", record.ResponseJson);
        cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(record.SubmittedCommodityIds));

        var result = await cmd.ExecuteScalarAsync(ct);
        var id = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        record.Id = id;
        return id;
    }

    public async Task<IReadOnlyList<SubmissionRecord>> GetRecentByTerminalAsync(
        int idTerminal, TimeSpan window, CancellationToken ct = default)
    {
        var sinceUnix = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, at_unix_seconds, id_terminal, terminal_display_name, is_production, ok,
                   http_status_code, api_status, api_message, source_image,
                   request_json, response_json, submitted_commodity_ids, source_images
            FROM submissions
            WHERE id_terminal = $term AND at_unix_seconds >= $since
            ORDER BY at_unix_seconds DESC;
            """;
        cmd.Parameters.AddWithValue("$term", idTerminal);
        cmd.Parameters.AddWithValue("$since", sinceUnix);

        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<SubmissionRecord>> GetAllAsync(int? limit = 200, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, at_unix_seconds, id_terminal, terminal_display_name, is_production, ok,
                   http_status_code, api_status, api_message, source_image,
                   request_json, response_json, submitted_commodity_ids, source_images
            FROM submissions
            ORDER BY at_unix_seconds DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit ?? 200);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<HashSet<string>> GetSubmittedSourceImagesAsync(bool productionOnly = true, CancellationToken ct = default)
    {
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        // We pull BOTH columns in one query and union them in memory:
        //   - source_image   : single primary filename (always populated)
        //   - source_images  : JSON array of all merged sources (populated for
        //                      merged submissions; NULL for legacy / single-shot)
        // The union ensures stragglers from a merge are also skipped at rescan.
        // We filter on `ok = 1` because an HTTP 4xx / 5xx response means UEX
        // did NOT accept the data — the user should be able to re-process the
        // screenshot, fix issues, and re-submit.
        var prodFilter = productionOnly ? "AND is_production = 1" : "";
        cmd.CommandText = $"""
            SELECT source_image, source_images FROM submissions
            WHERE ok = 1 {prodFilter};
            """;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0))
            {
                var primary = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(primary)) set.Add(primary);
            }
            if (!reader.IsDBNull(1))
            {
                var json = reader.GetString(1);
                try
                {
                    var arr = JsonSerializer.Deserialize<List<string>>(json);
                    if (arr is null) continue;
                    foreach (var name in arr)
                    {
                        if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
                    }
                }
                catch (JsonException)
                {
                    // Corrupted JSON — skip silently; the primary column already
                    // gives us the most important entry for that row.
                }
            }
        }
        return set;
    }

    private static async Task<IReadOnlyList<SubmissionRecord>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<SubmissionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var idsJson = reader.GetString(12);
            var imgsJson = reader.IsDBNull(13) ? null : reader.GetString(13);
            list.Add(new SubmissionRecord
            {
                Id = reader.GetInt64(0),
                At = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                IdTerminal = reader.GetInt32(2),
                TerminalDisplayName = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsProduction = reader.GetInt32(4) == 1,
                Ok = reader.GetInt32(5) == 1,
                HttpStatusCode = reader.GetInt32(6),
                ApiStatus = reader.IsDBNull(7) ? null : reader.GetString(7),
                ApiMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                SourceImage = reader.IsDBNull(9) ? null : reader.GetString(9),
                RequestJson = reader.GetString(10),
                ResponseJson = reader.GetString(11),
                SubmittedCommodityIds = JsonSerializer.Deserialize<List<int>>(idsJson) ?? new(),
                SourceImages = imgsJson is null
                    ? new()
                    : JsonSerializer.Deserialize<List<string>>(imgsJson) ?? new(),
            });
        }
        return list;
    }

    private SqliteConnection Open() => new(_connectionString);

    private static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SC-DataRunnerNet",
            "history.sqlite");
}
