using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeLimitWidget;

/// <summary>Widget-owned OAuth tokens for one account.</summary>
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
/// Persists each account's tokens DPAPI-encrypted (current user) at
/// %APPDATA%\ClaudeLimitWidget\auth-{accountId}.dat — never plaintext on disk.
/// </summary>
public static class TokenStore
{
    /// <summary>Id given to the single account migrated from the old auth.dat.</summary>
    public const string LegacyAccountId = "legacy";

    private static string LegacyPath => Path.Combine(Config.Dir, "auth.dat");
    private static string PathFor(string accountId) => Path.Combine(Config.Dir, $"auth-{accountId}.dat");

    private static readonly object Gate = new();

    public static StoredTokens? Load(string accountId)
    {
        try
        {
            lock (Gate)
            {
                string path = PathFor(accountId);
                if (!File.Exists(path))
                    return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<StoredTokens>(Encoding.UTF8.GetString(plain));
            }
        }
        catch
        {
            return null; // corrupt/foreign blob → treat as signed out
        }
    }

    public static void Save(string accountId, StoredTokens tokens)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Config.Dir);
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens));
            File.WriteAllBytes(PathFor(accountId), ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
    }

    public static void Clear(string accountId)
    {
        lock (Gate)
        {
            try { File.Delete(PathFor(accountId)); } catch { }
        }
    }

    public static bool Exists(string accountId) => File.Exists(PathFor(accountId));

    /// <summary>Renames the pre-multi-account auth.dat onto the legacy account id.</summary>
    public static void MigrateLegacy()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(LegacyPath) && !File.Exists(PathFor(LegacyAccountId)))
                    File.Move(LegacyPath, PathFor(LegacyAccountId));
            }
        }
        catch
        {
            // Worst case the user signs in again.
        }
    }
}
