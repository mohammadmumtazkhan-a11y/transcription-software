using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PersonalBATranscriber.App.ViewModels;

namespace PersonalBATranscriber.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly DispatcherTimer _playbackTimer;
    private bool _isDraggingSlider = false;

    public MainWindow()
    {
        InitializeComponent();

        _vm = (MainWindowViewModel)DataContext;

        // Wire media playback events
        _vm.RequestMediaSeek += OnRequestMediaSeek;
        _vm.RequestPlay += OnRequestPlay;
        _vm.RequestPause += OnRequestPause;

        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SourceMediaFilePath))
            {
                if (File.Exists(_vm.SourceMediaFilePath))
                {
                    AudioPlayer.Source = new Uri(_vm.SourceMediaFilePath);
                }
            }
        };

        // Timer to update playback slider
        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();
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
        AudioPlayer.Position = position;
        _vm.CurrentPosition = position;
        _vm.CurrentPositionSeconds = position.TotalSeconds;
    }

    private void OnRequestPlay()
    {
        if (AudioPlayer.Source == null && File.Exists(_vm.SourceMediaFilePath))
        {
            AudioPlayer.Source = new Uri(_vm.SourceMediaFilePath);
        }
        AudioPlayer.Play();
        PlayPauseBtn.Content = "⏸ Pause";
    }

    private void OnRequestPause()
    {
        AudioPlayer.Pause();
        PlayPauseBtn.Content = "▶ Play";
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Only seek if the user dragged the slider directly
        if (TimelineSlider.IsMouseOver && Math.Abs(AudioPlayer.Position.TotalSeconds - e.NewValue) > 1.0)
        {
            AudioPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
        }
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            _vm.GroqApiKey = box.Password;
        }
    }
}