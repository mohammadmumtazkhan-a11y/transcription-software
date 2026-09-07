using System;

namespace PersonalBATranscriber.Core.Models;

public class CleanSentence
{
    public string SentenceId { get; set; } = string.Empty;
    public string ParentSegmentId { get; set; } = string.Empty;
    public double AnchorTimestamp { get; set; }
    public string SpeakerLabel { get; set; } = "Speaker 1";
    public string CleanedText { get; set; } = string.Empty;
    public string? UserEditedText { get; set; }
    public int DisplayOrder { get; set; }

    public string DisplayText => !string.IsNullOrWhiteSpace(UserEditedText) ? UserEditedText : CleanedText;

    public string FormattedTimestamp => TimeSpan.FromSeconds(AnchorTimestamp).ToString(@"hh\:mm\:ss");
}
