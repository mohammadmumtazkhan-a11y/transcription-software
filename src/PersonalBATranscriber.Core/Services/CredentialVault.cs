using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PersonalBATranscriber.Core.Services;

public static class CredentialVault
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalBATranscriber");

    private static readonly string SecretFilePath = Path.Combine(AppDataFolder, "vault.dat");

    // Optional entropy to ensure defense-in-depth
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PersonalBATranscriber.Vault.Entropy");

    public static void SaveApiKey(string keyName, string apiKeyValue)
    {
        Directory.CreateDirectory(AppDataFolder);
        var combinedKey = $"{keyName}={apiKeyValue}";
        var plainBytes = Encoding.UTF8.GetBytes(combinedKey);

        // Encrypt with CurrentUser scope: only this Windows user on this machine can decrypt
        var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SecretFilePath, cipherBytes);
    }

    public static string? GetApiKey(string keyName)
    {
        if (!File.Exists(SecretFilePath)) return null;

        try
        {
            var cipherBytes = File.ReadAllBytes(SecretFilePath);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            var content = Encoding.UTF8.GetString(plainBytes);

            foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim().Equals(keyName, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[1].Trim();
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static void ClearApiKey()
    {
        if (File.Exists(SecretFilePath))
        {
            File.Delete(SecretFilePath);
        }
    }
}
