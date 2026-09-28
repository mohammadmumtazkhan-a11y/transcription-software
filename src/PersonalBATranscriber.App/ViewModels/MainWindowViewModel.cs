using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PersonalBATranscriber.Core.Models;
using PersonalBATranscriber.Core.Services;

namespace PersonalBATranscriber.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly CostLedgerService _ledgerService;
    private readonly FFmpegAudioExtractor _audioExtractor;
    private readonly OfflineWhisperService _offlineService;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _projectTitle = "New Transcription Session";

    [ObservableProperty]
    private string _sourceMediaFilePath = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready. Open an audio or video file to begin.";

    [ObservableProperty]
    private bool _isProcessing = false;

    [ObservableProperty]
    private double _progressValue = 0.0;

    [ObservableProperty]
    private string _budgetDisplay = "$0.00 / $20.00 (0.0 hrs)";

    [ObservableProperty]
    private string _groqApiKey = string.Empty;

    [ObservableProperty]
    private string _newTermText = string.Empty;

    [ObservableProperty]
    private CleanSentence? _selectedSentence;

    [ObservableProperty]
    private TimeSpan _currentPosition = TimeSpan.Zero;

    [ObservableProperty]
    private TimeSpan _totalDuration = TimeSpan.Zero;

    [ObservableProperty]
    private double _currentPositionSeconds = 0.0;

    [ObservableProperty]
    private double _totalDurationSeconds = 0.0;

    [ObservableProperty]
    private bool _isPlaying = false;

    // Audio file actually fed to the player. After transcription this switches
    // to the extracted 16 kHz PCM WAV, which seeks sample-accurately (compressed
    // MP3/M4A/MP4 seeking in WPF's MediaElement can land a second or more off).
    [ObservableProperty]
    private string _playbackMediaPath = string.Empty;

    // Small padding so the first/last syllable isn't clipped.
    private const double SentenceLeadInSeconds = 0.10;
    private const double SentenceTailSeconds = 0.20;

    /// <summary>
    /// When set, playback must stop automatically once the player reaches this
    /// position (end of the sentence the user clicked). Null = free playback.
    /// </summary>
    public double? PlaybackStopAtSeconds { get; private set; }

    [ObservableProperty]
    private string _selectedOfflineModel = "base"; // base (fastest), small (balanced), medium (accurate)

    public ObservableCollection<string> AvailableOfflineModels { get; } = new() { "base", "small", "medium" };

    public ObservableCollection<CleanSentence> Sentences { get; } = new();
    public ObservableCollection<GlossaryTerm> GlossaryTerms { get; } = new();

    public ProjectMetadata CurrentProject { get; private set; } = new();

    // Event invoked when media player should seek
    public event Action<TimeSpan>? RequestMediaSeek;
    public event Action? RequestPlay;
    public event Action? RequestPause;

    partial void OnSourceMediaFilePathChanged(string value)
    {
        PlaybackMediaPath = value;
    }

    public MainWindowViewModel()
    {
        _ledgerService = new CostLedgerService();
        _audioExtractor = new FFmpegAudioExtractor();
        _offlineService = new OfflineWhisperService();

        // Load existing API key if available
        var key = CredentialVault.GetApiKey("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            GroqApiKey = key;
        }

        // Initialize default BA glossary terms
        GlossaryTerms.Add(new GlossaryTerm { TermText = "API Gateway", Category = "TECH" });
        GlossaryTerms.Add(new GlossaryTerm { TermText = "Idempotency", Category = "TECH" });
        GlossaryTerms.Add(new GlossaryTerm { TermText = "Microservice", Category = "TECH" });
        GlossaryTerms.Add(new GlossaryTerm { TermText = "Dead-Letter Queue", Category = "TECH" });

        _ = RefreshBudgetDisplayAsync();
    }

    public async Task RefreshBudgetDisplayAsync()
    {
        try
        {
            var (committed, reserved, hours) = await _ledgerService.GetMonthlyUsageAsync();
            BudgetDisplay = $"${committed:F2} / ${CostLedgerService.MonthlyBudgetCapUSD:F2} ({hours:F1} hrs used)";
        }
        catch (Exception ex)
        {
            BudgetDisplay = $"Ledger Error: {ex.Message}";
        }
    }

    [RelayCommand]
    public void OpenSettings()
    {
        var dialog = new Views.SettingsDialog
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            GroqApiKey = dialog.SavedApiKey;
            StatusMessage = "Groq API key configured successfully.";
            _ = RefreshBudgetDisplayAsync();
        }
    }

    [RelayCommand]
    public void SaveApiKey()
    {
        OpenSettings();
    }

    [RelayCommand]
    public void AddGlossaryTerm()
    {
        if (string.IsNullOrWhiteSpace(NewTermText)) return;

        var term = NewTermText.Trim();
        if (!GlossaryTerms.Any(t => t.TermText.Equals(term, StringComparison.OrdinalIgnoreCase)))
        {
            GlossaryTerms.Add(new GlossaryTerm { TermText = term, Category = "Domain" });
        }
        NewTermText = string.Empty;
    }

    [RelayCommand]
    public void RemoveGlossaryTerm(GlossaryTerm term)
    {
        if (term != null && GlossaryTerms.Contains(term))
        {
            GlossaryTerms.Remove(term);
        }
    }

    /// <summary>
    /// Called when the user right-clicks a word in the transcript and supplies the
    /// correct spelling. Fixes every occurrence of the word in the current transcript
    /// and remembers the correction so future transcriptions apply it automatically.
    /// </summary>
    public void ApplySpellingCorrection(string wrongWord, string correctWord)
    {
        if (string.IsNullOrWhiteSpace(wrongWord) || string.IsNullOrWhiteSpace(correctWord))
            return;

        SpellingCorrectionStore.SaveCorrection(wrongWord, correctWord);

        int totalReplacements = 0;
        foreach (var sentence in Sentences)
        {
            var updated = SpellingCorrectionStore.ApplyWholeWord(sentence.UserEditedText, wrongWord, correctWord, out var count);
            if (count > 0)
            {
                sentence.UserEditedText = updated;
                totalReplacements += count;
            }
        }

        StatusMessage = totalReplacements > 0
            ? $"Corrected \"{wrongWord}\" \u2192 \"{correctWord}\" ({totalReplacements} occurrence{(totalReplacements == 1 ? string.Empty : "s")} updated). This spelling will be auto-corrected in future transcriptions too."
            : $"Saved \"{wrongWord}\" \u2192 \"{correctWord}\" as a correction for future transcriptions.";
    }

    [RelayCommand]
    public void SelectMediaFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Audio or Video Recording",
            Filter = "Media Files (*.mp3;*.wav;*.m4a;*.aac;*.mp4;*.mkv;*.mov)|*.mp3;*.wav;*.m4a;*.aac;*.mp4;*.mkv;*.mov|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            SourceMediaFilePath = dialog.FileName;
            ProjectTitle = Path.GetFileNameWithoutExtension(dialog.FileName);
            CurrentProject = new ProjectMetadata
            {
                ProjectName = ProjectTitle,
                SourceMediaFilePath = SourceMediaFilePath
            };
            StatusMessage = $"Selected: {Path.GetFileName(SourceMediaFilePath)}. Ready to transcribe.";
        }
    }

    [RelayCommand]
    public async Task StartTranscriptionAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceMediaFilePath) || !File.Exists(SourceMediaFilePath))
        {
            MessageBox.Show("Please select an audio or video file first by clicking 'Open Recording...'.", "No File Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(GroqApiKey))
        {
            var dialog = new Views.SettingsDialog
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SavedApiKey))
            {
                GroqApiKey = dialog.SavedApiKey;
            }
            else
            {
                StatusMessage = "Transcription requires a valid Groq API key.";
                return;
            }
        }

        IsProcessing = true;
        ProgressValue = 0.05;
        StatusMessage = "1/4 Inspecting media file and probing duration...";
        _cts = new CancellationTokenSource();

        try
        {
            var mediaPath = SourceMediaFilePath;
            var apiKey = GroqApiKey.Trim();
            var terms = GlossaryTerms.Select(t => t.TermText).ToList();
            var projectId = CurrentProject.ProjectId;

            await Task.Run(async () =>
            {
                // 1. Check Duration (< 3 hours)
                var duration = await _audioExtractor.GetMediaDurationSecondsAsync(mediaPath, _cts.Token);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    CurrentProject.MediaDurationSeconds = duration;
                    TotalDuration = TimeSpan.FromSeconds(duration);
                });

                if (duration > (3 * 3600))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show($"File duration ({TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}) exceeds the 3-hour limit.", "File Too Long", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return;
                }

                // 2. Extract 16kHz audio
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "2/4 Extracting 16kHz mono audio...";
                    ProgressValue = 0.20;
                });

                var tempDir = Path.Combine(Path.GetTempPath(), "PersonalBATranscriber", projectId);
                var wavPath = await _audioExtractor.Extract16kHzMonoAudioAsync(mediaPath, tempDir, cancellationToken: _cts.Token);
                CurrentProject.ExtractedAudioFilePath = wavPath;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (File.Exists(wavPath)) PlaybackMediaPath = wavPath;
                });

                // 3. Chunk audio
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "3/4 Segmenting audio into 10-minute speech chunks...";
                    ProgressValue = 0.35;
                });
                var chunks = await _audioExtractor.ChunkAudioAsync(wavPath, Path.Combine(tempDir, "chunks"), cancellationToken: _cts.Token);

                // 4. Reserve budget and call Groq
                var groqClient = new GroqSpeechClient(apiKey);

                var estimatedCost = (decimal)(duration / 3600.0) * GroqSpeechClient.WhisperLargeV3RatePerHour + 0.02m;
                var txId = await _ledgerService.ReserveSpendAsync(projectId, "GROQ", "whisper-large-v3", duration, estimatedCost);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"4/4 Cloud transcribing {chunks.Count} audio chunk(s) with Groq Whisper Large-V3...";
                    Sentences.Clear();
                });

                decimal totalActualCost = 0m;
                int totalInTokens = 0, totalOutTokens = 0;
                int sentenceOrder = 1;

                for (int i = 0; i < chunks.Count; i++)
                {
                    var chunk = chunks[i];
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        StatusMessage = $"Transcribing chunk {i + 1} of {chunks.Count}...";
                    });

                    var (rawText, chunkDur, chunkCost) = await groqClient.TranscribeChunkAsync(chunk.ChunkPath, terms, _cts.Token);
                    totalActualCost += chunkCost;

                    // Cleanup with Llama 3.1 8B
                    var (cleanedText, inTok, outTok, llmCost) = await groqClient.CleanTranscriptTextAsync(rawText, _cts.Token);
                    totalActualCost += llmCost;
                    totalInTokens += inTok;
                    totalOutTokens += outTok;

                    // Split into sentences and add to collection
                    var sentenceTexts = cleanedText.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries);
                    var segTime = chunk.StartSeconds;
                    var timeStep = sentenceTexts.Length > 0 ? (chunk.DurationSeconds / sentenceTexts.Length) : 0;

                    foreach (var text in sentenceTexts)
                    {
                        var cleanText = text.Trim();
                        if (!cleanText.EndsWith('.') && !cleanText.EndsWith('!') && !cleanText.EndsWith('?'))
                            cleanText += ".";

                        // Apply any spelling corrections the user has taught the app previously.
                        cleanText = SpellingCorrectionStore.ApplyAllKnownCorrections(cleanText);

                        var sentence = new CleanSentence
                        {
                            SentenceId = $"SNT-{sentenceOrder:D4}",
                            ParentSegmentId = $"SEG-{i:D4}",
                            AnchorTimestamp = segTime,
                            EndTimestamp = segTime + timeStep,
                            SpeakerLabel = (sentenceOrder % 2 == 1) ? "Speaker 1" : "Speaker 2",
                            CleanedText = cleanText,
                            DisplayOrder = sentenceOrder++
                        };

                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            Sentences.Add(sentence);
                        });
                        segTime += timeStep;
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressValue = 0.35 + (0.60 * ((double)(i + 1) / chunks.Count));
                    });
                }

                // Commit final spend in ledger
                await _ledgerService.CommitSpendAsync(txId, totalActualCost, totalInTokens, totalOutTokens);
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await RefreshBudgetDisplayAsync();
                    ProgressValue = 1.0;
                    StatusMessage = $"Transcription complete! Generated {Sentences.Count} sentence turns. Total Job Cost: ${totalActualCost:F4}.";
                });
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Transcription was canceled by user.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show($"Transcription failed:\n{ex.Message}", "Processing Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public async Task StartOfflineTranscriptionAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceMediaFilePath) || !File.Exists(SourceMediaFilePath))
        {
            MessageBox.Show("Please select an audio or video file first by clicking 'Open Recording...'.", "No File Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsProcessing = true;
        ProgressValue = 0.05;
        StatusMessage = "1/3 Probing media file duration...";
        _cts = new CancellationTokenSource();

        try
        {
            var mediaPath = SourceMediaFilePath;
            var projectId = CurrentProject.ProjectId;
            var model = SelectedOfflineModel;
            var terms = GlossaryTerms.Select(t => t.TermText).ToList();

            await Task.Run(async () =>
            {
                var duration = await _audioExtractor.GetMediaDurationSecondsAsync(mediaPath, _cts.Token);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    CurrentProject.MediaDurationSeconds = duration;
                    TotalDuration = TimeSpan.FromSeconds(duration);
                    ProgressValue = 0.15;
                    StatusMessage = "1/3 Extracting 16kHz audio with FFmpeg...";
                });

                var tempDir = Path.Combine(Path.GetTempPath(), "PersonalBATranscriber", projectId);
                var wavPath = await _audioExtractor.Extract16kHzMonoAudioAsync(mediaPath, tempDir, cancellationToken: _cts.Token);
                CurrentProject.ExtractedAudioFilePath = wavPath;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (File.Exists(wavPath)) PlaybackMediaPath = wavPath;
                });

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressValue = 0.25;
                    StatusMessage = $"2/3 Running offline transcription on CPU using faster-whisper '{model}'...";
                    Sentences.Clear();
                });

                var progressReporter = new Progress<string>(msg =>
                {
                    Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = msg);
                });

                Action<CleanSentence, AcousticSegment, double> onStreamed = (sentence, segment, ratio) =>
                {
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // Apply any spelling corrections the user has taught the app previously.
                        sentence.CleanedText = SpellingCorrectionStore.ApplyAllKnownCorrections(sentence.CleanedText);
                        Sentences.Add(sentence);
                        ProgressValue = 0.25 + (0.70 * ratio);
                        StatusMessage = $"Offline [{model}]: [{sentence.FormattedTimestamp} / {TotalDuration:hh\\:mm\\:ss}] ({Sentences.Count} sentences) • {sentence.DisplayText}";
                    });
                };

                await _offlineService.TranscribeOfflineAsync(
                    wavPath, 
                    model, 
                    terms, 
                    onStreamed,
                    progressReporter, 
                    _cts.Token);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressValue = 1.0;
                    StatusMessage = $"Offline transcription complete! Generated {Sentences.Count} sentence turns on local CPU. Cloud cost: $0.00.";
                });
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Offline transcription canceled by user.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Offline Error: {ex.Message}";
            MessageBox.Show($"Offline transcription failed:\n{ex.Message}", "Offline Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    public void CancelProcessing()
    {
        _cts?.Cancel();
        StatusMessage = "Canceling processing...";
    }

    [RelayCommand]
    public void PlaySentence(CleanSentence? sentence)
    {
        if (sentence == null) return;

        // Clicking the sentence that is already playing pauses it in place
        // instead of restarting it from the top.
        if (IsPlaying && SelectedSentence == sentence)
        {
            RequestPause?.Invoke();
            IsPlaying = false;
            sentence.IsCurrentlyPlaying = false;
            return;
        }

        if (SelectedSentence != null)
        {
            SelectedSentence.IsCurrentlyPlaying = false;
        }

        var (start, end) = GetSentencePlaybackRange(sentence);

        SelectedSentence = sentence;
        PlaybackStopAtSeconds = end;
        RequestMediaSeek?.Invoke(TimeSpan.FromSeconds(start));
        RequestPlay?.Invoke();
        IsPlaying = true;
        sentence.IsCurrentlyPlaying = true;
    }

    /// <summary>
    /// Works out exactly which slice of audio belongs to a sentence:
    /// its own start/end timestamps, with a tiny lead-in/tail, never running
    /// into the next sentence. Falls back to the next sentence's start when
    /// the end time is unknown (older project files).
    /// </summary>
    public (double Start, double? End) GetSentencePlaybackRange(CleanSentence sentence)
    {
        int index = Sentences.IndexOf(sentence);
        double? nextStart = null;
        if (index >= 0 && index + 1 < Sentences.Count)
        {
            var next = Sentences[index + 1].AnchorTimestamp;
            if (next > sentence.AnchorTimestamp) nextStart = next;
        }

        double start = Math.Max(0, sentence.AnchorTimestamp - SentenceLeadInSeconds);

        double? end = sentence.EndTimestamp > sentence.AnchorTimestamp
            ? sentence.EndTimestamp + SentenceTailSeconds
            : nextStart;

        if (end.HasValue && nextStart.HasValue && end.Value > nextStart.Value && sentence.EndTimestamp <= nextStart.Value)
        {
            // Don't bleed the tail padding into the next sentence.
            end = Math.Max(sentence.EndTimestamp, nextStart.Value);
        }

        if (end.HasValue && TotalDurationSeconds > 0)
            end = Math.Min(end.Value, TotalDurationSeconds);

        return (start, end);
    }

    /// <summary>
    /// Called by the code-behind when playback reaches the end of the clicked
    /// sentence. Pauses and rewinds to the sentence start so it can be replayed.
    /// </summary>
    public void NotifySentencePlaybackFinished()
    {
        PlaybackStopAtSeconds = null;
        RequestPause?.Invoke();
        IsPlaying = false;
        if (SelectedSentence != null)
        {
            SelectedSentence.IsCurrentlyPlaying = false;
            var (start, _) = GetSentencePlaybackRange(SelectedSentence);
            RequestMediaSeek?.Invoke(TimeSpan.FromSeconds(start));
        }
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            RequestPause?.Invoke();
            IsPlaying = false;
            if (SelectedSentence != null)
            {
                SelectedSentence.IsCurrentlyPlaying = false;
            }
        }
        else
        {
            // Toolbar Play is free playback of the whole recording; only the
            // per-sentence play buttons stop at a sentence boundary.
            PlaybackStopAtSeconds = null;
            RequestPlay?.Invoke();
            IsPlaying = true;
        }
    }

    // Called from the code-behind when MediaElement reaches the end of the
    // recording naturally, so state doesn't get stuck showing "Pause".
    public void NotifyPlaybackEnded()
    {
        PlaybackStopAtSeconds = null;
        IsPlaying = false;
        if (SelectedSentence != null)
        {
            SelectedSentence.IsCurrentlyPlaying = false;
        }
    }

    public void CancelSentenceStop() => PlaybackStopAtSeconds = null;

    [RelayCommand]
    public void SkipBackward()
    {
        var newPos = CurrentPosition - TimeSpan.FromSeconds(5);
        if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
        PlaybackStopAtSeconds = null;
        RequestMediaSeek?.Invoke(newPos);
    }

    [RelayCommand]
    public void SkipForward()
    {
        var newPos = CurrentPosition + TimeSpan.FromSeconds(5);
        if (newPos > TotalDuration) newPos = TotalDuration;
        PlaybackStopAtSeconds = null;
        RequestMediaSeek?.Invoke(newPos);
    }

    [RelayCommand]
    public void ExportToWord()
    {
        if (Sentences.Count == 0)
        {
            MessageBox.Show("There is no transcript content to export.", "Empty Transcript", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Export Transcript to Microsoft Word (.docx)",
            Filter = "Word Document (*.docx)|*.docx",
            FileName = $"{ProjectTitle}_Transcript_{DateTime.Now:yyyyMMdd}.docx"
        };

        if (saveDialog.ShowDialog() == true)
        {
            try
            {
                var exporter = new DocxExportService();
                exporter.ExportToWord(saveDialog.FileName, CurrentProject, Sentences);
                StatusMessage = $"Exported successfully to: {Path.GetFileName(saveDialog.FileName)}";
                MessageBox.Show($"Transcript exported successfully!\n\nFile: {saveDialog.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    public async Task SaveProjectAsync()
    {
        var saveDialog = new SaveFileDialog
        {
            Title = "Save Transcription Project",
            Filter = "Transcription Project (*.taproj)|*.taproj",
            FileName = $"{ProjectTitle}.taproj"
        };

        if (saveDialog.ShowDialog() == true)
        {
            try
            {
                var segments = Enumerable.Range(0, 1).Select(i => new AcousticSegment
                {
                    SegmentId = "SEG-0001",
                    ChunkIndex = 0,
                    StartTimeSeconds = 0,
                    EndTimeSeconds = CurrentProject.MediaDurationSeconds,
                    RawTranscript = string.Join(" ", Sentences.Select(s => s.CleanedText))
                });

                await ProjectDatabaseService.SaveProjectAsync(saveDialog.FileName, CurrentProject, segments, Sentences);
                StatusMessage = $"Project saved: {Path.GetFileName(saveDialog.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save project: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
