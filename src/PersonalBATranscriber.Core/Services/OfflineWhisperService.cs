using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PersonalBATranscriber.Core.Models;

namespace PersonalBATranscriber.Core.Services;

public class OfflineWhisperService
{
    private readonly string _pythonPath;
    private readonly string _scriptPath;

    public OfflineWhisperService(string? customPythonPath = null, string? customScriptPath = null)
    {
        _pythonPath = ResolvePythonPath(customPythonPath);
        _scriptPath = ResolveScriptPath(customScriptPath);
    }

    public static string ResolvePythonPath(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            return customPath;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var knownPython = Path.Combine(userProfile, @"AppData\Local\Programs\Python\Python312\python.exe");
        if (File.Exists(knownPython)) return knownPython;

        return "python.exe";
    }

    public static string ResolveScriptPath(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            return customPath;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var localScript = Path.Combine(baseDir, "offline_transcriber.py");
        if (File.Exists(localScript)) return localScript;

        var devPath = Path.Combine(baseDir, @"..\..\..\..\PersonalBATranscriber.Core\Services\offline_transcriber.py");
        if (File.Exists(devPath)) return Path.GetFullPath(devPath);

        return localScript;
    }

    public async Task<List<(CleanSentence Sentence, AcousticSegment Segment)>> TranscribeOfflineAsync(
        string audioFilePath,
        string modelSize = "small",
        IEnumerable<string>? glossaryTerms = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException($"Audio file not found: {audioFilePath}");

        var tempJson = Path.Combine(Path.GetTempPath(), $"offline_stt_{Guid.NewGuid():N}.json");

        var glossaryArgs = "";
        if (glossaryTerms != null)
        {
            var termsList = string.Join(" ", glossaryTerms);
            if (!string.IsNullOrWhiteSpace(termsList))
            {
                glossaryArgs = $"--glossary {termsList}";
            }
        }

        var arguments = $"\"{_scriptPath}\" --audio \"{audioFilePath}\" --model {modelSize} --task translate --output \"{tempJson}\" {glossaryArgs}";

        progress?.Report($"Running offline transcription on CPU using faster-whisper '{modelSize}'...");

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stderr = await stderrTask;

        if (process.ExitCode != 0 || !File.Exists(tempJson))
        {
            throw new InvalidOperationException($"Offline transcription failed (ExitCode {process.ExitCode}): {stderr}");
        }

        var jsonContent = await File.ReadAllTextAsync(tempJson, cancellationToken);
        File.Delete(tempJson);

        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;
        var segmentsArray = root.GetProperty("segments");

        var results = new List<(CleanSentence, AcousticSegment)>();
        int order = 1;

        foreach (var item in segmentsArray.EnumerateArray())
        {
            var start = item.GetProperty("start").GetDouble();
            var end = item.GetProperty("end").GetDouble();
            var text = item.GetProperty("text").GetString() ?? "";
            var avgLogProb = item.GetProperty("avg_logprob").GetDouble();
            var compRatio = item.GetProperty("compression_ratio").GetDouble();

            var segment = new AcousticSegment
            {
                SegmentId = $"SEG-OFF-{order:D4}",
                ChunkIndex = 0,
                StartTimeSeconds = start,
                EndTimeSeconds = end,
                RawTranscript = text,
                AvgLogProb = avgLogProb,
                CompressionRatio = compRatio,
                IsFlaggedForReview = avgLogProb < -0.8 || compRatio > 2.4
            };

            var sentence = new CleanSentence
            {
                SentenceId = $"SNT-OFF-{order:D4}",
                ParentSegmentId = segment.SegmentId,
                AnchorTimestamp = start,
                SpeakerLabel = (order % 2 == 1) ? "Speaker 1" : "Speaker 2",
                CleanedText = text,
                DisplayOrder = order++
            };

            results.Add((sentence, segment));
        }

        return results;
    }
}
