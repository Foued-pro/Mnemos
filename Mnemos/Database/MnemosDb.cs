using Microsoft.Data.Sqlite;
namespace Mnemos.Database;

/// <summary>
/// Manages the SQLite connection and exposes internal helpers
/// shared by all partial class files.
/// </summary>
public partial class MnemosDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly Action<string>? _log;
    private static readonly object _dbLock = new();

    /// <summary>
    /// Opens the database, enables WAL journaling, and runs migrations.
    /// </summary>
    /// <param name="dbPath">Full path to the SQLite database file.</param>
    /// <param name="log">Optional logging callback.</param>
    public MnemosDb(string dbPath, Action<string>? log = null)
    {
        _log = log;
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");
        RunMigrations();
    }

    /// <summary>
    /// Releases the database connection.
    /// </summary>
    public void Dispose() => _conn.Dispose();

    // ── Internal helpers (shared by all partial files) ──────

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void ExecuteWithParams(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static long ToUnix(string iso) =>
        DateTimeOffset.TryParse(iso, out var dto)
            ? dto.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}