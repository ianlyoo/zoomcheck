using ZoomCheck.Infrastructure.Persistence;

namespace ZoomCheck.Infrastructure.Tests;

/// <summary>
/// Creates an isolated on-disk SQLite database in a temp directory for a single test, and removes
/// it afterwards. On-disk (not in-memory) because the repository opens a new connection per call.
/// </summary>
internal sealed class TempSqliteDatabase : IDisposable
{
    private readonly string _directory;

    private TempSqliteDatabase(string directory, string databasePath)
    {
        _directory = directory;
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public static TempSqliteDatabase Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zoomcheck-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new TempSqliteDatabase(directory, Path.Combine(directory, "zoomcheck.db"));
    }

    public SqliteAttendanceRepository CreateRepository() => new(DatabasePath);

    public async Task<SqliteAttendanceRepository> CreateInitializedRepositoryAsync()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        return repository;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked file handle must not fail an otherwise passing test.
        }
    }
}
