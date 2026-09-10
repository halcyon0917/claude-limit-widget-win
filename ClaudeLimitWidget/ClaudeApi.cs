using System.Net;
using System.Net.Http.Headers;

namespace ClaudeLimitWidget;

/// <summary>
/// Shared endpoint details, plus an up-front check that a credential can actually
/// read usage.
///
/// The check exists because not every Claude token can: `claude setup-token` mints
/// an inference-only credential, and both endpoints here demand the `user:profile`
/// scope, so such a token returns 403 forever. Catching that when the account is
/// added beats adding a widget that silently never fills in.
/// </summary>
public static class ClaudeApi
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";
    public const string UserAgent = "claude-code/2.1.201";
    public const string BetaHeader = "oauth-2025-04-20";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", BetaHeader);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return req;
    }

    /// <summary>
    /// Returns null when the token can read usage, otherwise a message explaining
    /// why not. A network failure is not treated as rejection — the caller should
    /// not lose an otherwise good account because the machine was briefly offline.
    /// </summary>
    public static string? DescribeUnusable(string token)
    {
        try
        {
            using var req = Request(HttpMethod.Get, UsageUrl, token);
            using var resp = Http.Send(req);

            if (resp.IsSuccessStatusCode)
                return null;

            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (resp.StatusCode == HttpStatusCode.Forbidden && body.Contains("scope requirement"))
                return "This token cannot read usage: it lacks the “user:profile” scope.\n\n" +
                       "Tokens from `claude setup-token` are inference-only, so they will never " +
                       "show limits. Use “Open sign-in page” instead — that flow requests the " +
                       "scope this needs.";

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return "This token was rejected (401). It may have been revoked or already expired.";

            if ((int)resp.StatusCode == 429)
                return null; // rate limited, not invalid — accept and let polling catch up

            return $"This token could not read usage (HTTP {(int)resp.StatusCode}).";
        }
        catch
        {
            return null; // offline or blocked: assume good rather than discard it
        }
    }
}
