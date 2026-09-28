using System;

namespace PersonalBATranscriber.Core.Models;

public class ProjectMetadata
{
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectName { get; set; } = "Untitled Project";
    public string SourceMediaFilePath { get; set; } = string.Empty;
    public string ExtractedAudioFilePath { get; set; } = string.Empty;
    public double MediaDurationSeconds { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedDate { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Draft";
}
