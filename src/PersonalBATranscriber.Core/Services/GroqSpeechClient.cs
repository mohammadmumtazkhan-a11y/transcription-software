using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalBATranscriber.Core.Services;

public class GroqSpeechClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public const decimal WhisperLargeV3RatePerHour = 0.111m;
    public const decimal Llama8BInputRatePerMillion = 0.05m;
    public const decimal Llama8BOutputRatePerMillion = 0.08m;

    public GroqSpeechClient(string apiKey, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Groq API key cannot be null or empty.", nameof(apiKey));

        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<(string EnglishText, double DurationSeconds, decimal EstimatedCost)> TranscribeChunkAsync(
        string audioFilePath,
        IEnumerable<string>? glossaryTerms = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException($"Audio chunk not found: {audioFilePath}");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/translations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var content = new MultipartFormDataContent();
        
        var audioBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
        var fileContent = new ByteArrayContent(audioBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(audioFilePath));

        content.Add(new StringContent("whisper-large-v3"), "model");
        content.Add(new StringContent("verbose_json"), "response_format");

        if (glossaryTerms != null)
        {
            var prompt = "Domain Glossary: " + string.Join(", ", glossaryTerms);
            content.Add(new StringContent(prompt), "prompt");
        }

        request.Content = content;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Groq Audio API failed ({response.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
        var duration = root.TryGetProperty("duration", out var durProp) ? durProp.GetDouble() : 0.0;

        var cost = (decimal)(duration / 3600.0) * WhisperLargeV3RatePerHour;
        return (text.Trim(), duration, cost);
    }

    public async Task<(string CleanedText, int InTokens, int OutTokens, decimal Cost)> CleanTranscriptTextAsync(
        string rawText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return (string.Empty, 0, 0, 0m);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var systemPrompt = @"You are an expert IT Business Analyst transcription editor. Your job is to clean a raw meeting transcript into clear, professional, grammatically correct English.

STRICT RULES:
1. Output ONLY faithful English. If residual Hindi or Urdu speech appears, translate it into faithful English.
2. REMOVE filler words and vocal crutch phrases: 'um', 'uh', 'you know', 'like', 'sort of', 'basically', 'matlab', 'yani', 'achha', 'theek hai'.
3. REMOVE stuttering and false starts (e.g. 'We need to... we need to test this' -> 'We need to test this').
4. PRESERVE EVERY SUBSTANTIVE FACT: Every requirement, condition, negation ('not', 'never'), number, currency, date, API endpoint, database table, person name, and technical acronym must remain completely intact.
5. PRESERVE DISAGREEMENTS AND UNCERTAINTIES: Do NOT resolve ambiguities. If a speaker states 'I am unsure if the deadline is Tuesday', do NOT change it to 'The deadline is Tuesday'.
6. DO NOT SUMMARIZE. Maintain a 1:1 mapping with the conversational flow.
7. UNRESOLVED SPEECH: If speech is completely unintelligible, mark it visibly as [Inaudible: ~timestamp] or [Unclear: phrase?]. Never fabricate text.";

        var payload = new
        {
            model = "llama-3.1-8b-instant",
            temperature = 0.1,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = $"Raw Transcript:\n{rawText}\n\nCleaned English Transcript:" }
            }
        };

        request.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Fallback to raw text if LLM formatting temporarily errors
            return (rawText, 0, 0, 0m);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cleaned = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? rawText;

        int inTokens = 0, outTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inTokens = usage.TryGetProperty("prompt_tokens", out var pTok) ? pTok.GetInt32() : 0;
            outTokens = usage.TryGetProperty("completion_tokens", out var cTok) ? cTok.GetInt32() : 0;
        }

        var cost = (inTokens * Llama8BInputRatePerMillion / 1_000_000m) +
                   (outTokens * Llama8BOutputRatePerMillion / 1_000_000m);

        return (cleaned.Trim(), inTokens, outTokens, cost);
    }
}
