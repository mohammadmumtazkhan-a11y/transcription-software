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

    [ObservableProperty]
    private string _selectedOfflineModel = "small"; // base, small, medium

    public ObservableCollection<string> AvailableOfflineModels { get; } = new() { "base", "small", "medium" };

    public ObservableCollection<CleanSentence> Sentences { get; } = new();
    public ObservableCollection<GlossaryTerm> GlossaryTerms { get; } = new();

    public ProjectMetadata CurrentProject { get; private set; } = new();

    // Event invoked when media player should seek
    public event Action<TimeSpan>? RequestMediaSeek;
    public event Action? RequestPlay;
    public event Action? RequestPause;

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
            // 1. Check Duration (< 3 hours)
            var duration = await _audioExtractor.GetMediaDurationSecondsAsync(SourceMediaFilePath, _cts.Token);
            CurrentProject.MediaDurationSeconds = duration;
            TotalDuration = TimeSpan.FromSeconds(duration);

            if (duration > (3 * 3600))
            {
                MessageBox.Show($"File duration ({TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}) exceeds the 3-hour limit.", "File Too Long", MessageBoxButton.OK, MessageBoxImage.Error);
                IsProcessing = false;
                return;
            }

            // 2. Extract 16kHz audio
            StatusMessage = "2/4 Extracting 16kHz mono audio...";
            ProgressValue = 0.20;

            var tempDir = Path.Combine(Path.GetTempPath(), "PersonalBATranscriber", CurrentProject.ProjectId);
            var wavPath = await _audioExtractor.Extract16kHzMonoAudioAsync(SourceMediaFilePath, tempDir, cancellationToken: _cts.Token);
            CurrentProject.ExtractedAudioFilePath = wavPath;

            // 3. Chunk audio
            StatusMessage = "3/4 Segmenting audio into 10-minute speech chunks...";
            ProgressValue = 0.35;
            var chunks = await _audioExtractor.ChunkAudioAsync(wavPath, Path.Combine(tempDir, "chunks"), cancellationToken: _cts.Token);

            // 4. Reserve budget and call Groq
            var groqClient = new GroqSpeechClient(GroqApiKey.Trim());
            var terms = GlossaryTerms.Select(t => t.TermText).ToList();

            var estimatedCost = (decimal)(duration / 3600.0) * GroqSpeechClient.WhisperLargeV3RatePerHour + 0.02m;
            var txId = await _ledgerService.ReserveSpendAsync(CurrentProject.ProjectId, "GROQ", "whisper-large-v3", duration, estimatedCost);

            StatusMessage = $"4/4 Cloud transcribing {chunks.Count} audio chunk(s) with Groq Whisper Large-V3...";
            Sentences.Clear();

            decimal totalActualCost = 0m;
            int totalInTokens = 0, totalOutTokens = 0;
            int sentenceOrder = 1;

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                StatusMessage = $"Transcribing chunk {i + 1} of {chunks.Count}...";

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

                    var sentence = new CleanSentence
                    {
                        SentenceId = $"SNT-{sentenceOrder:D4}",
                        ParentSegmentId = $"SEG-{i:D4}",
                        AnchorTimestamp = segTime,
                        SpeakerLabel = (sentenceOrder % 2 == 1) ? "Speaker 1" : "Speaker 2",
                        CleanedText = cleanText,
                        DisplayOrder = sentenceOrder++
                    };

                    Sentences.Add(sentence);
                    segTime += timeStep;
                }

                ProgressValue = 0.35 + (0.60 * ((double)(i + 1) / chunks.Count));
            }

            // Commit final spend in ledger
            await _ledgerService.CommitSpendAsync(txId, totalActualCost, totalInTokens, totalOutTokens);
            await RefreshBudgetDisplayAsync();

            ProgressValue = 1.0;
            StatusMessage = $"Transcription complete! Generated {Sentences.Count} sentence turns. Total Job Cost: ${totalActualCost:F4}.";
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
        ProgressValue = 0.10;
        StatusMessage = "1/3 Probing media file and extracting 16kHz audio...";
        _cts = new CancellationTokenSource();

        try
        {
            var duration = await _audioExtractor.GetMediaDurationSecondsAsync(SourceMediaFilePath, _cts.Token);
            CurrentProject.MediaDurationSeconds = duration;
            TotalDuration = TimeSpan.FromSeconds(duration);

            var tempDir = Path.Combine(Path.GetTempPath(), "PersonalBATranscriber", CurrentProject.ProjectId);
            var wavPath = await _audioExtractor.Extract16kHzMonoAudioAsync(SourceMediaFilePath, tempDir, cancellationToken: _cts.Token);
            CurrentProject.ExtractedAudioFilePath = wavPath;

            ProgressValue = 0.30;
            StatusMessage = $"2/3 Running 100% offline transcription on CPU using faster-whisper '{SelectedOfflineModel}'...";

            var terms = GlossaryTerms.Select(t => t.TermText).ToList();
            var progressReporter = new Progress<string>(msg => StatusMessage = msg);

            var results = await _offlineService.TranscribeOfflineAsync(
                wavPath, 
                SelectedOfflineModel, 
                terms, 
                progressReporter, 
                _cts.Token);

            Sentences.Clear();
            foreach (var (sentence, _) in results)
            {
                Sentences.Add(sentence);
            }

            ProgressValue = 1.0;
            StatusMessage = $"Offline transcription complete! Generated {Sentences.Count} sentence turns on local CPU. Cloud cost: $0.00.";
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
        SelectedSentence = sentence;
        RequestMediaSeek?.Invoke(TimeSpan.FromSeconds(sentence.AnchorTimestamp));
        RequestPlay?.Invoke();
        IsPlaying = true;
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            RequestPause?.Invoke();
            IsPlaying = false;
        }
        else
        {
            RequestPlay?.Invoke();
            IsPlaying = true;
        }
    }

    [RelayCommand]
    public void SkipBackward()
    {
        var newPos = CurrentPosition - TimeSpan.FromSeconds(5);
        if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
        RequestMediaSeek?.Invoke(newPos);
    }

    [RelayCommand]
    public void SkipForward()
    {
        var newPos = CurrentPosition + TimeSpan.FromSeconds(5);
        if (newPos > TotalDuration) newPos = TotalDuration;
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
