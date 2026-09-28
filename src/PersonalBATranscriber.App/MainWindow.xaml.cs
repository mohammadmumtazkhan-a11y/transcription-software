using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PersonalBATranscriber.App.ViewModels;
using PersonalBATranscriber.Core.Models;

namespace PersonalBATranscriber.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _sentenceStopTimer;
    private bool _isDraggingSlider = false;

    // MediaElement ignores Position changes made before the file has finished
    // opening, so a seek issued right after (re)loading the source is queued
    // here and applied in MediaOpened. This is what made the first click on a
    // sentence start from the wrong place.
    private bool _isMediaOpened = false;
    private TimeSpan? _pendingSeek;
    private bool _pendingPlay;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();

        _vm = (MainWindowViewModel)DataContext;

        // Wire media playback events
        _vm.RequestMediaSeek += OnRequestMediaSeek;
        _vm.RequestPlay += OnRequestPlay;
        _vm.RequestPause += OnRequestPause;

        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.PlaybackMediaPath))
            {
                LoadPlaybackSource(_vm.PlaybackMediaPath);
            }
        };

        // Timer to update playback slider
        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        AudioPlayer.MediaOpened += AudioPlayer_MediaOpened;
        AudioPlayer.MediaFailed += (s, e) => _isMediaOpened = false;

        // Dedicated fast timer for the sentence-stop check. CompositionTarget.Rendering
        // was tried first but WPF throttles/stops that event when nothing on screen is
        // animating, which is exactly the case here (audio plays, UI stays static) -
        // so the stop check silently never ran. A DispatcherTimer always fires.
        _sentenceStopTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _sentenceStopTimer.Tick += SentenceStopTimer_Tick;
        _sentenceStopTimer.Start();
    }

    /// <summary>
    /// Keeps the window (and its title bar / close button) fully on screen.
    /// The XAML asks for 1240x820, which at 125-150% display scaling is taller
    /// than a 1080p screen and pushed the title bar off the top.
    /// </summary>
    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea; // screen minus taskbar, in DIPs
        MinWidth = Math.Min(MinWidth, work.Width);
        MinHeight = Math.Min(MinHeight, work.Height);
        if (Width > work.Width) Width = work.Width;
        if (Height > work.Height) Height = work.Height;

        // Small screens: just open maximised.
        if (work.Height < 820 || work.Width < 1240)
            WindowState = WindowState.Maximized;
    }

    private void LoadPlaybackSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var newUri = new Uri(path);
        if (AudioPlayer.Source != null && AudioPlayer.Source == newUri)
            return;

        // Keep the listener where they were when swapping to the WAV copy.
        var resumeAt = AudioPlayer.Source != null && _isMediaOpened ? AudioPlayer.Position : (TimeSpan?)null;
        bool wasPlaying = _vm.IsPlaying;

        _isMediaOpened = false;
        AudioPlayer.Source = newUri;
        if (resumeAt.HasValue && _pendingSeek == null) _pendingSeek = resumeAt;
        _pendingPlay = wasPlaying;

        // With LoadedBehavior=Manual the file only opens once playback is
        // requested, so open it now (paused) so the first sentence click can
        // seek precisely without waiting.
        AudioPlayer.Play();
        if (!wasPlaying) AudioPlayer.Pause();
    }

    private void AudioPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _isMediaOpened = true;
        if (_pendingSeek.HasValue)
        {
            AudioPlayer.Position = _pendingSeek.Value;
            _pendingSeek = null;
        }
        if (_pendingPlay)
        {
            AudioPlayer.Play();
            _pendingPlay = false;
        }
    }

    private void SentenceStopTimer_Tick(object? sender, EventArgs e)
    {
        var stopAt = _vm.PlaybackStopAtSeconds;
        if (stopAt == null || !_vm.IsPlaying || !_isMediaOpened)
            return;

        if (AudioPlayer.Position.TotalSeconds >= stopAt.Value)
        {
            _vm.NotifySentencePlaybackFinished();
        }
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (AudioPlayer.NaturalDuration.HasTimeSpan && !_isDraggingSlider)
        {
            _vm.CurrentPosition = AudioPlayer.Position;
            _vm.CurrentPositionSeconds = AudioPlayer.Position.TotalSeconds;

            if (_vm.TotalDuration == TimeSpan.Zero)
            {
                _vm.TotalDuration = AudioPlayer.NaturalDuration.TimeSpan;
                _vm.TotalDurationSeconds = AudioPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            }
        }
    }

    private void OnRequestMediaSeek(TimeSpan position)
    {
        if (!_isMediaOpened)
            _pendingSeek = position;
        else
            AudioPlayer.Position = position;
        _vm.CurrentPosition = position;
        _vm.CurrentPositionSeconds = position.TotalSeconds;
    }

    private void OnRequestPlay()
    {
        if (AudioPlayer.Source == null)
        {
            LoadPlaybackSource(_vm.PlaybackMediaPath);
        }
        if (!_isMediaOpened)
            _pendingPlay = true;
        AudioPlayer.Play();
        PlayPauseBtn.Content = "⏸ Pause";
    }

    private void OnRequestPause()
    {
        _pendingPlay = false;
        AudioPlayer.Pause();
        PlayPauseBtn.Content = "▶ Play";
    }

    private void AudioPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        // Reset to the start of the clip and clear playing state so the row
        // icon and the toolbar button both fall back to "Play" instead of
        // getting stuck showing "Pause" forever.
        AudioPlayer.Stop();
        PlayPauseBtn.Content = "▶ Play";
        _vm.NotifyPlaybackEnded();
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Only seek if the user dragged the slider directly
        if (TimelineSlider.IsMouseOver && Math.Abs(AudioPlayer.Position.TotalSeconds - e.NewValue) > 1.0)
        {
            _vm.CancelSentenceStop();
            AudioPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
        }
    }

    private void CloseAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsProcessing)
        {
            var answer = MessageBox.Show(
                "A transcription is still running. Close the application and cancel it?",
                "Close Application", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;

            // Stops the background faster-whisper / Groq job before exiting.
            _vm.CancelProcessingCommand.Execute(null);
        }

        AudioPlayer.Stop();
        Application.Current.Shutdown();
    }

    // --- Right-click "Correct Spelling" on a word in the transcript ---

    private string? _pendingWrongWord;
    private CleanSentence? _pendingSentence;

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '\'' || c == '-';

    private void SentenceTextBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;

        var text = textBox.Text;
        var mousePos = Mouse.GetPosition(textBox);
        int charIndex = textBox.GetCharacterIndexFromPoint(mousePos, true);

        if (string.IsNullOrEmpty(text) || charIndex < 0 || charIndex >= text.Length || !IsWordChar(text[charIndex]))
        {
            // Right-clicked on empty space or punctuation - nothing to correct.
            e.Handled = true;
            _pendingWrongWord = null;
            _pendingSentence = null;
            return;
        }

        int start = charIndex;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        int end = charIndex;
        while (end < text.Length - 1 && IsWordChar(text[end + 1])) end++;

        var word = text.Substring(start, end - start + 1);
        textBox.Select(start, end - start + 1);

        _pendingWrongWord = word;
        _pendingSentence = textBox.DataContext as CleanSentence;

        if (textBox.ContextMenu?.Items.Count > 0 && textBox.ContextMenu.Items[0] is MenuItem menuItem)
        {
            menuItem.Header = $"Correct Spelling of \"{word}\"\u2026";
        }
    }

    private void CorrectSpellingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingWrongWord))
            return;

        var dialog = new Views.SpellingCorrectionDialog(_pendingWrongWord)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.CorrectedText))
        {
            _vm.ApplySpellingCorrection(_pendingWrongWord, dialog.CorrectedText);
        }
    }
}