using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalBATranscriber.Core.Services;

public class FFmpegAudioExtractor
{
    private readonly string _ffmpegPath;

    public FFmpegAudioExtractor(string? customFfmpegPath = null)
    {
        _ffmpegPath = ResolveFFmpegPath(customFfmpegPath);
    }

    public string FFmpegPath => _ffmpegPath;

    public static string ResolveFFmpegPath(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
        {
            return customPath;
        }

        // 1. Check local application directory / bin
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var localBin = Path.Combine(baseDir, "ffmpeg.exe");
        if (File.Exists(localBin)) return localBin;

        var localAssets = Path.Combine(baseDir, "Assets", "ffmpeg.exe");
        if (File.Exists(localAssets)) return localAssets;

        // 2. Check known Winget install path on this machine
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var wingetPath = Path.Combine(userProfile, @"AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.0.1-full_build\bin\ffmpeg.exe");
        if (File.Exists(wingetPath)) return wingetPath;

        // 3. Fallback to PATH
        return "ffmpeg.exe";
    }

    public async Task<double> GetMediaDurationSecondsAsync(string mediaFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaFilePath))
            throw new FileNotFoundException($"Media file not found: {mediaFilePath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = $"-i \"{mediaFilePath}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Concurrently drain stdout & stderr to prevent pipe buffer deadlocks
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stderr = await stderrTask;
        await stdoutTask;

        // Parse: Duration: 01:23:45.67,
        var match = Regex.Match(stderr, @"Duration:\s*(\d{2}):(\d{2}):(\d{2}\.?\d*)");
        if (match.Success)
        {
            var hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            return (hours * 3600) + (minutes * 60) + seconds;
        }

        return 0;
    }

    public async Task<string> Extract16kHzMonoAudioAsync(
        string mediaFilePath, 
        string outputDirectory, 
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaFilePath))
            throw new FileNotFoundException($"Media file not found: {mediaFilePath}");

        Directory.CreateDirectory(outputDirectory);
        var outputWav = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(mediaFilePath)}_16k.wav");

        // Extract 16kHz Mono 16-bit PCM WAV with -loglevel error to prevent pipe saturation
        var arguments = $"-y -loglevel error -i \"{mediaFilePath}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 \"{outputWav}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Concurrently drain stdout and stderr so OS pipe buffers never fill up
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stderr = await stderrTask;
        await stdoutTask;

        if (process.ExitCode != 0 || !File.Exists(outputWav))
        {
            throw new InvalidOperationException($"FFmpeg audio extraction failed (ExitCode {process.ExitCode}): {stderr}");
        }

        progress?.Report(1.0);
        return outputWav;
    }

    public async Task<List<(string ChunkPath, double StartSeconds, double DurationSeconds)>> ChunkAudioAsync(
        string wavFilePath,
        string chunksDirectory,
        double chunkLengthSeconds = 600.0, // 10 minutes default
        double overlapSeconds = 1.0,
        CancellationToken cancellationToken = default)
    {
        var totalDuration = await GetMediaDurationSecondsAsync(wavFilePath, cancellationToken);
        var chunks = new List<(string, double, double)>();

        Directory.CreateDirectory(chunksDirectory);

        double currentStart = 0;
        int chunkIndex = 0;

        while (currentStart < totalDuration)
        {
            var currentDuration = Math.Min(chunkLengthSeconds, totalDuration - currentStart);
            var chunkFileName = Path.Combine(chunksDirectory, $"chunk_{chunkIndex:D4}.wav");

            var args = $"-y -loglevel error -ss {currentStart.ToString("F2", CultureInfo.InvariantCulture)} -t {currentDuration.ToString("F2", CultureInfo.InvariantCulture)} -i \"{wavFilePath}\" -c copy \"{chunkFileName}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            await stderrTask;
            await stdoutTask;

            if (File.Exists(chunkFileName))
            {
                chunks.Add((chunkFileName, currentStart, currentDuration));
            }

            // Step forward accounting for overlap
            currentStart += (chunkLengthSeconds - overlapSeconds);
            chunkIndex++;
        }

        return chunks;
    }
}
