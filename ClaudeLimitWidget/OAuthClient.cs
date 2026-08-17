using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeLimitWidget;

/// <summary>
/// Minimal OAuth PKCE client for standalone mode, using the same public flow
/// Claude Code's /login uses (community-documented; unofficial). The user signs
/// in via their browser and pastes the resulting code back into the widget.
/// </summary>
public static class OAuthClient
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string RedirectUri = "https://console.anthropic.com/oauth/code/callback";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const string Scopes = "org:create_api_key user:profile user:inference";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record PkceSession(string Verifier, string AuthorizeUrl);

    public static PkceSession StartSignIn()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string url = "https://claude.ai/oauth/authorize" +
                     "?code=true" +
                     $"&client_id={ClientId}" +
                     "&response_type=code" +
                     $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                     $"&scope={Uri.EscapeDataString(Scopes)}" +
                     $"&code_challenge={challenge}" +
                     "&code_challenge_method=S256" +
                     $"&state={verifier}";
        return new PkceSession(verifier, url);
    }

    /// <summary>Exchanges the pasted "code#state" value for tokens. Throws with a readable message.</summary>
    public static StoredTokens ExchangeCode(string pasted, string verifier)
    {
        string[] parts = pasted.Trim().Split('#');
        string code = parts[0];
        string state = parts.Length > 1 ? parts[1] : verifier;

        var body = new JsonObject
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["state"] = state,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        };
        return PostForTokens(body, fallbackRefresh: "");
    }

    /// <summary>Refreshes a rotated token pair. Returns null on failure (caller keeps old pair).</summary>
    public static StoredTokens? Refresh(string refreshToken)
    {
        try
        {
            var body = new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
            };
            return PostForTokens(body, fallbackRefresh: refreshToken);
        }
        catch (Exception ex)
        {
            Log.Write($"token refresh failed: {ex.Message}");
            return null;
        }
    }

    private static StoredTokens PostForTokens(JsonObject body, string fallbackRefresh)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.201");

        using var resp = Http.Send(req);
        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sign-in failed (HTTP {(int)resp.StatusCode}). {Snippet(text)}");

        if (JsonNode.Parse(text) is not JsonObject obj || obj["access_token"] is null)
            throw new InvalidOperationException("Sign-in failed: unexpected response.");

        long expiresIn = obj["expires_in"]?.GetValue<long?>() ?? 0;
        return new StoredTokens
        {
            AccessToken = obj["access_token"]!.GetValue<string>(),
            RefreshToken = obj["refresh_token"]?.GetValue<string>() ?? fallbackRefresh,
            ExpiresAtMs = expiresIn > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000
                : 0,
        };
    }

    private static string Snippet(string s) => s.Length > 120 ? s[..120] : s;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
