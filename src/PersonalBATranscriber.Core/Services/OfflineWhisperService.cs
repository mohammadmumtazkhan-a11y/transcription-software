using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        string modelSize = "base",
        IEnumerable<string>? glossaryTerms = null,
        Action<CleanSentence, AcousticSegment, double>? onSegmentStreamed = null,
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

        progress?.Report($"Initializing faster-whisper '{modelSize}' on CPU...");

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

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        double totalDuration = 0;
        var results = new List<(CleanSentence, AcousticSegment)>();
        int order = 1;

        // Read stdout line by line in real-time without blocking EndOfStream property
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("INFO:DURATION:"))
            {
                var durStr = line.Substring("INFO:DURATION:".Length);
                if (double.TryParse(durStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDur))
                {
                    totalDuration = parsedDur;
                }
            }
            else if (line.StartsWith("STATUS:"))
            {
                progress?.Report(line.Substring("STATUS:".Length));
            }
            else if (line.StartsWith("SEGMENT:"))
            {
                var jsonStr = line.Substring("SEGMENT:".Length);
                try
                {
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;
                    var start = root.GetProperty("start").GetDouble();
                    var end = root.GetProperty("end").GetDouble();
                    var text = root.GetProperty("text").GetString() ?? "";
                    var avgLogProb = root.GetProperty("avg_logprob").GetDouble();
                    var compRatio = root.GetProperty("compression_ratio").GetDouble();

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
                    double progressRatio = totalDuration > 0 ? Math.Min(1.0, end / totalDuration) : 0;
                    onSegmentStreamed?.Invoke(sentence, segment, progressRatio);
                }
                catch { }
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;

        if (File.Exists(tempJson))
        {
            try { File.Delete(tempJson); } catch { }
        }

        if (process.ExitCode != 0 && results.Count == 0)
        {
            throw new InvalidOperationException($"Offline transcription failed (ExitCode {process.ExitCode}): {stderr}");
        }

        return results;
    }
}
