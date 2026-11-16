using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Velocity.Data.Database;

public sealed class DatabaseInitializer(ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS SchemaInfo (Version INTEGER NOT NULL);
            INSERT INTO SchemaInfo (Version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM SchemaInfo);
            CREATE TABLE IF NOT EXISTS Downloads (Id INTEGER PRIMARY KEY AUTOINCREMENT, Url TEXT NOT NULL, FileName TEXT NOT NULL, DestinationPath TEXT NOT NULL, TotalBytes INTEGER NULL, DownloadedBytes INTEGER NOT NULL DEFAULT 0, Status TEXT NOT NULL, CreatedUtc TEXT NOT NULL, CompletedUtc TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_Downloads_Status ON Downloads(Status);
            CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Categories (Name TEXT PRIMARY KEY, DestinationPath TEXT NOT NULL, Extensions TEXT NOT NULL);
            INSERT OR IGNORE INTO Categories (Name, DestinationPath, Extensions) VALUES ('General', '', '');
            INSERT OR IGNORE INTO Categories (Name, DestinationPath, Extensions) VALUES ('Documents', '', '.pdf,.doc,.docx,.txt,.xls,.xlsx');
            INSERT OR IGNORE INTO Categories (Name, DestinationPath, Extensions) VALUES ('Software', '', '.exe,.msi,.zip');
            INSERT OR IGNORE INTO Categories (Name, DestinationPath, Extensions) VALUES ('Videos', '', '.mp4,.mkv,.avi,.mov');
            INSERT OR IGNORE INTO Categories (Name, DestinationPath, Extensions) VALUES ('Music', '', '.mp3,.wav,.flac,.aac');
            INSERT OR IGNORE INTO Categories (Name, DestinationPath, Extensions) VALUES ('Images', '', '.png,.jpg,.jpeg,.gif,.webp');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Resume metadata added after the first release; CREATE TABLE IF NOT EXISTS leaves old databases untouched.
        await EnsureColumnAsync(connection, "FinalUrl", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ETag", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "LastModified", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ResumeSupported", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        // Per-download speed limit chosen by the user; 0 keeps following the app-wide setting.
        await EnsureColumnAsync(connection, "SpeedLimitBytesPerSecond", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        // Request headers (Referer, Cookie, Authorization, …) kept so a protected link still resumes after a restart.
        await EnsureColumnAsync(connection, "Headers", "TEXT NULL", cancellationToken);
        // Queue priority: a higher number starts first, and 0 keeps the order things were added in.
        await EnsureColumnAsync(connection, "Priority", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        logger.LogInformation("Velocity database initialized at {DatabasePath}", databasePath);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string column, string definition, CancellationToken cancellationToken)
    {
        var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(Downloads)";
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE Downloads ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
