using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeLimitWidget;

/// <summary>Widget-owned OAuth tokens for standalone mode.</summary>
public sealed record StoredTokens
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = "";

    /// <summary>Unix ms; 0 = unknown/never (e.g. long-lived setup tokens).</summary>
    [JsonPropertyName("expiresAtMs")]
    public long ExpiresAtMs { get; init; }

    public bool IsExpired =>
        ExpiresAtMs > 0 && ExpiresAtMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;
}

/// <summary>
/// Persists standalone-mode tokens DPAPI-encrypted (current user) at
/// %APPDATA%\ClaudeLimitWidget\auth.dat — never plaintext on disk.
/// </summary>
public static class TokenStore
{
    private static string FilePath => Path.Combine(Config.Dir, "auth.dat");
    private static readonly object Gate = new();

    public static StoredTokens? Load()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath))
                    return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<StoredTokens>(Encoding.UTF8.GetString(plain));
            }
        }
        catch
        {
            return null; // corrupt/foreign blob → treat as signed out
        }
    }

    public static void Save(StoredTokens tokens)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Config.Dir);
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens));
            File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { File.Delete(FilePath); } catch { }
        }
    }

    public static bool Exists => File.Exists(FilePath);
}
