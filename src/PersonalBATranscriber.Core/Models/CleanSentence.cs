using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PersonalBATranscriber.Core.Models;

public class CleanSentence : INotifyPropertyChanged
{
    public string SentenceId { get; set; } = string.Empty;
    public string ParentSegmentId { get; set; } = string.Empty;
    public double AnchorTimestamp { get; set; }

    // End of this sentence's audio in seconds. Used to stop playback
    // automatically when the sentence finishes. 0 means "unknown" (e.g. an
    // older project file) and the player falls back to the next sentence's start.
    public double EndTimestamp { get; set; }
    public string SpeakerLabel { get; set; } = "Speaker 1";
    public string CleanedText { get; set; } = string.Empty;
    
    private string? _userEditedText;
    public string UserEditedText
    {
        get => _userEditedText ?? CleanedText;
        set
        {
            if (_userEditedText != value)
            {
                _userEditedText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public int DisplayOrder { get; set; }

    public string DisplayText => UserEditedText;

    public string FormattedTimestamp => TimeSpan.FromSeconds(AnchorTimestamp).ToString(@"hh\:mm\:ss");

    // Tracks whether this specific sentence's audio is the one currently
    // playing, so the UI can show a Pause affordance instead of restarting
    // playback every time the row is clicked.
    private bool _isCurrentlyPlaying;
    public bool IsCurrentlyPlaying
    {
        get => _isCurrentlyPlaying;
        set
        {
            if (_isCurrentlyPlaying != value)
            {
                _isCurrentlyPlaying = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
