using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Velocity.Data.Database;

namespace Velocity.Core.Tests.Support;

/// <summary>A throw-away SQLite database plus destination folder for manager-level tests.</summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("velocity-manager-");

    public TestDatabase()
    {
        Path = System.IO.Path.Combine(_directory.FullName, "test.db");
        new DatabaseInitializer(NullLogger<DatabaseInitializer>.Instance).InitializeAsync(Path).GetAwaiter().GetResult();
    }

    public string Path { get; }
    public string Folder => _directory.FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { _directory.Delete(true); } catch (IOException) { }
    }
}
