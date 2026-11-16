using Microsoft.Data.Sqlite;

namespace Velocity.Data.Repositories;

public sealed class SettingsRepository(string databasePath)
{
    public async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}"); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT Key, Value FROM Settings";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}"); await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var pair in values)
        {
            var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value";
            command.Parameters.AddWithValue("$key", pair.Key); command.Parameters.AddWithValue("$value", pair.Value); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
