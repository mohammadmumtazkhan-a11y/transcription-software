using System;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using PersonalBATranscriber.Core.Models;

namespace PersonalBATranscriber.Core.Services;

public class CostLedgerService
{
    private readonly string _connectionString;
    public const decimal MonthlyBudgetCapUSD = 20.00m;

    public CostLedgerService(string? customDbPath = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PersonalBATranscriber");
        Directory.CreateDirectory(appData);

        var dbPath = customDbPath ?? Path.Combine(appData, "ledger.db");
        _connectionString = $"Data Source={dbPath};Version=3;";
        InitializeDatabase(dbPath);
    }

    private void InitializeDatabase(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            SQLiteConnection.CreateFile(dbPath);
        }

        using var conn = new SQLiteConnection(_connectionString);
        conn.Open();

        var sql = @"
            CREATE TABLE IF NOT EXISTS CostLedger (
                TransactionId TEXT PRIMARY KEY,
                Timestamp TEXT NOT NULL,
                ProjectId TEXT,
                Provider TEXT NOT NULL,
                ModelName TEXT NOT NULL,
                AudioDurationSeconds REAL NOT NULL,
                InputTokens INTEGER DEFAULT 0,
                OutputTokens INTEGER DEFAULT 0,
                EstimatedCostUSD REAL NOT NULL,
                ActualCostUSD REAL NOT NULL,
                Status TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS GlobalGlossary (
                TermId TEXT PRIMARY KEY,
                TermText TEXT NOT NULL UNIQUE,
                Category TEXT NOT NULL,
                DateAdded TEXT NOT NULL
            );
        ";

        using var cmd = new SQLiteCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public async Task<(decimal CommittedSpend, decimal ReservedSpend, double TotalAudioHours)> GetMonthlyUsageAsync(DateTime? date = null)
    {
        var targetDate = date ?? DateTime.UtcNow;
        var startOfMonth = new DateTime(targetDate.Year, targetDate.Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("o");
        var endOfMonth = new DateTime(targetDate.Year, targetDate.Month, DateTime.DaysInMonth(targetDate.Year, targetDate.Month), 23, 59, 59, DateTimeKind.Utc).ToString("o");

        using var conn = new SQLiteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT 
                COALESCE(SUM(CASE WHEN Status = 'COMMITTED' THEN ActualCostUSD ELSE 0 END), 0) as Committed,
                COALESCE(SUM(CASE WHEN Status = 'RESERVED' THEN EstimatedCostUSD ELSE 0 END), 0) as Reserved,
                COALESCE(SUM(CASE WHEN Status = 'COMMITTED' THEN AudioDurationSeconds ELSE 0 END), 0) as TotalSeconds
            FROM CostLedger
            WHERE Timestamp >= @start AND Timestamp <= @end;
        ";

        using var cmd = new SQLiteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@start", startOfMonth);
        cmd.Parameters.AddWithValue("@end", endOfMonth);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var committed = Convert.ToDecimal(reader["Committed"]);
            var reserved = Convert.ToDecimal(reader["Reserved"]);
            var seconds = Convert.ToDouble(reader["TotalSeconds"]);
            return (committed, reserved, seconds / 3600.0);
        }

        return (0m, 0m, 0);
    }

    public async Task<bool> CanAffordAsync(decimal estimatedCostUSD)
    {
        var (committed, reserved, _) = await GetMonthlyUsageAsync();
        return (committed + reserved + estimatedCostUSD) <= MonthlyBudgetCapUSD;
    }

    public async Task<string> ReserveSpendAsync(
        string projectId, 
        string provider, 
        string modelName, 
        double audioDurationSeconds, 
        decimal estimatedCostUSD)
    {
        var (committed, reserved, _) = await GetMonthlyUsageAsync();
        if ((committed + reserved + estimatedCostUSD) > MonthlyBudgetCapUSD)
        {
            throw new InvalidOperationException($"Monthly budget limit of ${MonthlyBudgetCapUSD:F2} would be exceeded. Committed: ${committed:F2}, Reserved: ${reserved:F2}, Requested: ${estimatedCostUSD:F2}.");
        }

        var transactionId = Guid.NewGuid().ToString("N");
        using var conn = new SQLiteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            INSERT INTO CostLedger (
                TransactionId, Timestamp, ProjectId, Provider, ModelName,
                AudioDurationSeconds, InputTokens, OutputTokens,
                EstimatedCostUSD, ActualCostUSD, Status
            ) VALUES (
                @id, @ts, @proj, @provider, @model,
                @dur, 0, 0,
                @est, 0, 'RESERVED'
            );
        ";

        using var cmd = new SQLiteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", transactionId);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@proj", projectId);
        cmd.Parameters.AddWithValue("@provider", provider);
        cmd.Parameters.AddWithValue("@model", modelName);
        cmd.Parameters.AddWithValue("@dur", audioDurationSeconds);
        cmd.Parameters.AddWithValue("@est", Convert.ToDouble(estimatedCostUSD));

        await cmd.ExecuteNonQueryAsync();
        return transactionId;
    }

    public async Task CommitSpendAsync(
        string transactionId, 
        decimal actualCostUSD, 
        int inputTokens = 0, 
        int outputTokens = 0)
    {
        using var conn = new SQLiteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            UPDATE CostLedger
            SET ActualCostUSD = @actual,
                InputTokens = @inTok,
                OutputTokens = @outTok,
                Status = 'COMMITTED'
            WHERE TransactionId = @id;
        ";

        using var cmd = new SQLiteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@actual", Convert.ToDouble(actualCostUSD));
        cmd.Parameters.AddWithValue("@inTok", inputTokens);
        cmd.Parameters.AddWithValue("@outTok", outputTokens);
        cmd.Parameters.AddWithValue("@id", transactionId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ReleaseReservationAsync(string transactionId)
    {
        using var conn = new SQLiteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "UPDATE CostLedger SET Status = 'FAILED' WHERE TransactionId = @id;";
        using var cmd = new SQLiteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", transactionId);
        await cmd.ExecuteNonQueryAsync();
    }
}
