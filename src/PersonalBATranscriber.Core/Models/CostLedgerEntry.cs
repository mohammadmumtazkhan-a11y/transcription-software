using System;

namespace PersonalBATranscriber.Core.Models;

public class CostLedgerEntry
{
    public string TransactionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ProjectId { get; set; } = string.Empty;
    public string Provider { get; set; } = "GROQ";
    public string ModelName { get; set; } = "whisper-large-v3";
    public double AudioDurationSeconds { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCostUSD { get; set; }
    public decimal ActualCostUSD { get; set; }
    public string Status { get; set; } = "COMMITTED"; // RESERVED, COMMITTED, FAILED
}
