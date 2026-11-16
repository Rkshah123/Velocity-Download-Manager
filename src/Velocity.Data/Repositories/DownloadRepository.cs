using System.Text.Json;
using Microsoft.Data.Sqlite;
using Velocity.Core.Interfaces;
using Velocity.Core.Models;

namespace Velocity.Data.Repositories;

public sealed class DownloadRepository(string databasePath) : IDownloadRepository
{
    public async Task<IReadOnlyList<DownloadRecord>> GetRecentAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, FileName, Url, DestinationPath, TotalBytes, DownloadedBytes, Status, FinalUrl, ETag, LastModified, ResumeSupported, SpeedLimitBytesPerSecond, Headers, Priority FROM Downloads ORDER BY Id DESC";
        var records = new List<DownloadRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new DownloadRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetInt64(5),
                Enum.Parse<DownloadStatus>(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                !reader.IsDBNull(10) && reader.GetInt64(10) != 0,
                reader.IsDBNull(11) ? 0 : (int)reader.GetInt64(11),
                ReadHeaders(reader.IsDBNull(12) ? null : reader.GetString(12)),
                reader.IsDBNull(13) ? 0 : (int)reader.GetInt64(13)));
        }
        return records;
    }

    public async Task<long> AddAsync(DownloadRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Downloads (Url, FileName, DestinationPath, TotalBytes, DownloadedBytes, Status, CreatedUtc, FinalUrl, ETag, LastModified, ResumeSupported, SpeedLimitBytesPerSecond, Headers, Priority) VALUES ($url, $fileName, $path, $total, $downloaded, $status, $created, $finalUrl, $etag, $lastModified, $resumable, $speedLimit, $headers, $priority); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$url", record.Url);
        command.Parameters.AddWithValue("$fileName", record.FileName);
        command.Parameters.AddWithValue("$path", record.DestinationPath);
        command.Parameters.AddWithValue("$total", (object?)record.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$downloaded", record.DownloadedBytes);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$finalUrl", (object?)record.FinalUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$etag", (object?)record.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastModified", (object?)record.LastModified ?? DBNull.Value);
        command.Parameters.AddWithValue("$resumable", record.ResumeSupported ? 1 : 0);
        command.Parameters.AddWithValue("$speedLimit", record.SpeedLimitBytesPerSecond);
        command.Parameters.AddWithValue("$headers", (object?)WriteHeaders(record.Headers) ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", record.Priority);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task UpdateAsync(DownloadRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Downloads SET FileName = $fileName, DestinationPath = $path, TotalBytes = $total, DownloadedBytes = $downloaded, Status = $status, FinalUrl = $finalUrl, ETag = $etag, LastModified = $lastModified, ResumeSupported = $resumable, SpeedLimitBytesPerSecond = $speedLimit, Headers = $headers, Priority = $priority WHERE Id = $id";
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$fileName", record.FileName);
        command.Parameters.AddWithValue("$path", record.DestinationPath);
        command.Parameters.AddWithValue("$total", (object?)record.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$downloaded", record.DownloadedBytes);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$finalUrl", (object?)record.FinalUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$etag", (object?)record.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastModified", (object?)record.LastModified ?? DBNull.Value);
        command.Parameters.AddWithValue("$resumable", record.ResumeSupported ? 1 : 0);
        command.Parameters.AddWithValue("$speedLimit", record.SpeedLimitBytesPerSecond);
        command.Parameters.AddWithValue("$headers", (object?)WriteHeaders(record.Headers) ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", record.Priority);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Headers live in one JSON column: the set is small and never queried, only carried with a download.</summary>
    private static string? WriteHeaders(IReadOnlyDictionary<string, string>? headers) =>
        headers is not { Count: > 0 } ? null : JsonSerializer.Serialize(headers);

    private static IReadOnlyDictionary<string, string>? ReadHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return headers is { Count: > 0 } ? headers : null;
        }
        catch (JsonException)
        {
            // A malformed value must never stop the library from loading; the link simply downloads unauthenticated.
            return null;
        }
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Downloads WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
