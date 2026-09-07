namespace PersonalBATranscriber.Core.Models;

public class AcousticSegment
{
    public string SegmentId { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public double StartTimeSeconds { get; set; }
    public double EndTimeSeconds { get; set; }
    public string RawTranscript { get; set; } = string.Empty;
    public double AvgLogProb { get; set; }
    public double CompressionRatio { get; set; }
    public bool IsFlaggedForReview { get; set; }
}
