using System;

namespace PersonalBATranscriber.Core.Models;

public class CleanSentence
{
    public string SentenceId { get; set; } = string.Empty;
    public string ParentSegmentId { get; set; } = string.Empty;
    public double AnchorTimestamp { get; set; }
    public string SpeakerLabel { get; set; } = "Speaker 1";
    public string CleanedText { get; set; } = string.Empty;
    
    private string? _userEditedText;
    public string UserEditedText
    {
        get => _userEditedText ?? CleanedText;
        set => _userEditedText = value;
    }

    public int DisplayOrder { get; set; }

    public string DisplayText => UserEditedText;

    public string FormattedTimestamp => TimeSpan.FromSeconds(AnchorTimestamp).ToString(@"hh\:mm\:ss");
}
