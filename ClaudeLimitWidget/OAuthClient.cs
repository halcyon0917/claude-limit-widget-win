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
    /// <summary>
    /// Token endpoints, tried in order. All front the same OAuth service, but they sit
    /// behind different Cloudflare rate-limit rules: on a shared/CGNAT address the
    /// console and platform hosts have been seen returning 429 for hours to every
    /// request, while api.anthropic.com kept evaluating them. Falling through is safe
    /// only on a 429, because that response means the code was never consumed.
    /// </summary>
    private static readonly string[] TokenUrls =
    {
        "https://api.anthropic.com/v1/oauth/token",
        "https://platform.claude.com/v1/oauth/token",
        "https://console.anthropic.com/v1/oauth/token",
    };
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
        HttpResponseMessage? limited = null;
        string limitedText = "";

        foreach (string url in TokenUrls)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", ClaudeApi.UserAgent);

            var resp = Http.Send(req);
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if ((int)resp.StatusCode == 429)
            {
                // Refused before the code was looked at, so it is still valid for the next host.
                Log.Write($"token endpoint rate limited: {new Uri(url).Host}; trying next");
                limited?.Dispose();
                limited = resp;
                limitedText = text;
                continue;
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(DescribeFailure(resp, text));
                limited?.Dispose();
                return Parse(text, fallbackRefresh);
            }
        }

        // Every host refused us; report the last rate-limit response.
        using (limited)
            throw new InvalidOperationException(DescribeFailure(limited!, limitedText));
    }

    private static StoredTokens Parse(string text, string fallbackRefresh)
    {
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

    /// <summary>
    /// Turns an OAuth error response into something a person can act on. The raw
    /// body is JSON meant for machines, and the two failures users actually hit —
    /// rate limiting and a stale code — both need a specific next step.
    /// </summary>
    private static string DescribeFailure(HttpResponseMessage resp, string body)
    {
        int status = (int)resp.StatusCode;
        string kind = "";
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj)
                kind = obj["error"]?["type"]?.GetValue<string>()
                       ?? obj["error"]?.GetValue<string>()
                       ?? "";
        }
        catch
        {
            // Non-JSON body; fall back to the status code alone.
        }

        if (status == 429)
        {
            string wait = resp.Headers.RetryAfter?.Delta is { } delta
                ? $"about {Math.Max(1, (int)delta.TotalMinutes)} min"
                : "a few minutes";
            return $"Every sign-in endpoint is rate limiting this network right now. Wait {wait}, " +
                   "then click \u201cOpen sign-in page\u201d again for a fresh code \u2014 the one above " +
                   "cannot be reused. If this persists for hours, the limit is likely on a shared " +
                   "address (e.g. CGNAT); try from another network or a phone hotspot.";
        }

        if (status == 400 || kind == "invalid_grant")
            return "That code did not work. Codes are single-use and expire quickly \u2014 click " +
                   "\u201cOpen sign-in page\u201d again and paste the new one.";

        if (status is 401 or 403)
            return "Sign-in was rejected. Make sure you approved access for the intended account, " +
                   "then try again with a fresh code.";

        string detail = kind.Length > 0 ? $" ({kind})" : "";
        return $"Sign-in failed with HTTP {status}{detail}. Try again with a fresh code.";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
