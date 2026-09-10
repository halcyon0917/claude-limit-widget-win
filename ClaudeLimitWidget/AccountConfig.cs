using System.Text.Json.Serialization;

namespace ClaudeLimitWidget;

/// <summary>Where an account's credentials come from.</summary>
public enum AccountKind
{
    /// <summary>Follows whoever is logged into the Claude Code CLI (no stored token).</summary>
    Cli,

    /// <summary>Browser OAuth sign-in owned by the widget; refreshable.</summary>
    OAuth,

    /// <summary>Pasted long-lived token from `claude setup-token`; not refreshable.</summary>
    SetupToken,
}

/// <summary>
/// One tracked account. Each enabled account gets its own taskbar widget, its own
/// poller and its own cache file, keyed by <see cref="Id"/>.
/// </summary>
public sealed class AccountConfig
{
    /// <summary>Stable id; also names this account's token and cache files.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AccountKind Kind { get; set; } = AccountKind.Cli;

    /// <summary>User-chosen short name shown on the widget. Falls back to the fetched email.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>When false the account is kept but no widget is shown and no polling happens.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Only the CLI account can read the statusline bridge file.</summary>
    [JsonIgnore]
    public bool UsesStatusline => Kind == AccountKind.Cli;

    /// <summary>Label to show when the account has no user-chosen name yet.</summary>
    public string DisplayName(AccountInfo? account)
    {
        if (Label.Length > 0)
            return Label;
        if (account?.Email is { Length: > 0 } email)
            return email.Split('@')[0];
        return Kind == AccountKind.Cli ? "CLI" : "account";
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}
