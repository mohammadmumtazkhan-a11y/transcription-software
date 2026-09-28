using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalBATranscriber.Core.Services;

/// <summary>
/// Remembers spelling corrections the user teaches it (e.g. "teh" -> "the",
/// or a misheard product/person name) so the same mistake is automatically
/// fixed in every future transcription, not just the one it was found in.
/// Stored as plain JSON under the user's LocalAppData folder — this is not
/// sensitive data, unlike the Groq API key, so no encryption is needed.
/// </summary>
public static class SpellingCorrectionStore
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalBATranscriber");

    private static readonly string StoreFilePath = Path.Combine(AppDataFolder, "corrections.json");

    // Key: lowercase misspelling. Value: correction, exactly as the user typed it.
    private static Dictionary<string, string>? _cache;

    private static Dictionary<string, string> Load()
    {
        if (_cache != null) return _cache;

        if (File.Exists(StoreFilePath))
        {
            try
            {
                var json = File.ReadAllText(StoreFilePath);
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                         ?? new Dictionary<string, string>();
            }
            catch
            {
                _cache = new Dictionary<string, string>();
            }
        }
        else
        {
            _cache = new Dictionary<string, string>();
        }

        return _cache;
    }

    public static IReadOnlyDictionary<string, string> GetAll() => Load();

    public static void SaveCorrection(string wrongWord, string correctWord)
    {
        if (string.IsNullOrWhiteSpace(wrongWord) || string.IsNullOrWhiteSpace(correctWord))
            return;

        var dict = Load();
        dict[wrongWord.Trim().ToLowerInvariant()] = correctWord.Trim();

        Directory.CreateDirectory(AppDataFolder);
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StoreFilePath, json);
    }

    public static void RemoveCorrection(string wrongWord)
    {
        if (string.IsNullOrWhiteSpace(wrongWord)) return;

        var dict = Load();
        if (dict.Remove(wrongWord.Trim().ToLowerInvariant()))
        {
            Directory.CreateDirectory(AppDataFolder);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StoreFilePath, json);
        }
    }

    /// <summary>
    /// Replaces every whole-word, case-insensitive occurrence of <paramref name="wrongWord"/>
    /// with <paramref name="correctWord"/>, matching the original occurrence's capitalization
    /// style (all-caps, capitalized, or as-typed).
    /// </summary>
    public static string ApplyWholeWord(string text, string wrongWord, string correctWord, out int replacements)
    {
        replacements = 0;
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(wrongWord))
            return text;

        var pattern = $@"\b{Regex.Escape(wrongWord)}\b";
        var count = 0;

        var result = Regex.Replace(text, pattern, m =>
        {
            count++;
            return MatchCase(m.Value, correctWord);
        }, RegexOptions.IgnoreCase);

        replacements = count;
        return result;
    }

    /// <summary>
    /// Applies every previously-taught correction to a freshly transcribed piece of
    /// text, so mistakes the user already fixed once don't reappear.
    /// </summary>
    public static string ApplyAllKnownCorrections(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var dict = Load();
        if (dict.Count == 0) return text;

        foreach (var kv in dict)
        {
            text = ApplyWholeWord(text, kv.Key, kv.Value, out _);
        }

        return text;
    }

    private static string MatchCase(string original, string replacement)
    {
        if (string.IsNullOrEmpty(replacement) || string.IsNullOrEmpty(original))
            return replacement;

        if (char.IsUpper(original[0]))
        {
            bool allUpper = true;
            foreach (var c in original)
            {
                if (char.IsLetter(c) && !char.IsUpper(c)) { allUpper = false; break; }
            }

            if (allUpper && original.Length > 1)
                return replacement.ToUpperInvariant();

            return char.ToUpperInvariant(replacement[0]) + replacement.Substring(1);
        }

        return replacement;
    }
}
