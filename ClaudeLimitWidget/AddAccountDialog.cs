using System.Diagnostics;

namespace ClaudeLimitWidget;

/// <summary>
/// Adds or re-authenticates one account via browser OAuth: the user approves in the
/// browser and pastes the resulting "code#state" back. A raw sk-ant token is still
/// accepted, but `claude setup-token` output is inference-only and cannot read usage,
/// so the caller validates before storing. Returns the credentials rather than saving
/// them, so the caller decides which account id they belong to.
/// </summary>
public sealed class AddAccountDialog : Form
{
    private readonly TextBox _label;
    private readonly TextBox _input;
    private readonly Label _status;
    private readonly Button _ok;
    private OAuthClient.PkceSession? _session;

    /// <summary>Credentials obtained; only valid when the dialog returns OK.</summary>
    public StoredTokens? Tokens { get; private set; }

    /// <summary>OAuth for a browser sign-in, SetupToken for a pasted sk-ant token.</summary>
    public AccountKind Kind { get; private set; } = AccountKind.OAuth;

    /// <summary>Optional friendly name typed by the user.</summary>
    public string AccountLabel => _label.Text.Trim();

    public AddAccountDialog(string title = "Add a Claude account", string existingLabel = "")
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 318);
        BackColor = Color.FromArgb(0x20, 0x20, 0x22);
        ForeColor = Color.FromArgb(0xE6, 0xE6, 0xE6);
        Font = new Font("Segoe UI", 9f);

        var steps = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(16, 12, 468, 92),
            Text = "1. Click “Open sign-in page” and log into the account you want to track.\n" +
                   "2. Approve access. The page then shows a code that looks like\n" +
                   "     abc123…#xyz789…  — copy the whole thing, including the #.\n" +
                   "3. Paste it below and click Save.\n\n" +
                   "Note: a token from `claude setup-token` will not work here — it cannot read usage.",
        };

        var labelCaption = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(16, 112, 110, 24),
            Text = "Name (optional)",
        };

        _label = new TextBox
        {
            Bounds = new Rectangle(130, 110, 354, 26),
            Text = existingLabel,
            PlaceholderText = "e.g. work — shown on the widget",
            BackColor = Color.FromArgb(0x2A, 0x2A, 0x2E),
            ForeColor = ForeColor,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var openBtn = MakeButton("Open sign-in page", new Rectangle(16, 150, 150, 30));
        openBtn.Click += (_, _) =>
        {
            _session = OAuthClient.StartSignIn();
            try
            {
                Process.Start(new ProcessStartInfo(_session.AuthorizeUrl) { UseShellExecute = true });
                _status!.Text = "Browser opened — paste the code below when you have it.";
            }
            catch (Exception ex)
            {
                _status!.Text = "Could not open browser: " + ex.Message;
            }
        };

        _input = new TextBox
        {
            Bounds = new Rectangle(16, 194, 468, 26),
            PlaceholderText = "Paste the code from the sign-in page (looks like abc123…#xyz789…)",
            BackColor = Color.FromArgb(0x2A, 0x2A, 0x2E),
            ForeColor = ForeColor,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _status = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(16, 226, 468, 44),
            ForeColor = Color.FromArgb(0xE0, 0x96, 0x3C),
        };

        _ok = MakeButton("Save", new Rectangle(320, 276, 78, 28));
        _ok.Click += (_, _) => Submit();

        var cancel = MakeButton("Cancel", new Rectangle(406, 276, 78, 28));
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange(new Control[] { steps, labelCaption, _label, openBtn, _input, _status, _ok, cancel });
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    private Button MakeButton(string text, Rectangle bounds)
    {
        var b = new Button
        {
            Text = text,
            Bounds = bounds,
            FlatStyle = FlatStyle.Flat,
            ForeColor = ForeColor,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4A, 0x4E);
        return b;
    }

    private async void Submit()
    {
        string value = _input.Text.Trim();
        if (value.Length == 0)
        {
            _status.Text = "Paste the code or a token first.";
            return;
        }

        _ok.Enabled = false;
        _status.Text = "Verifying…";
        try
        {
            if (value.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase))
            {
                // Direct long-lived token: no refresh half, so it simply stops
                // working when it expires.
                Tokens = new StoredTokens { AccessToken = value, RefreshToken = "", ExpiresAtMs = 0 };
                Kind = AccountKind.SetupToken;
            }
            else
            {
                if (_session is null)
                {
                    _status.Text = "Click “Open sign-in page” first — the code is tied to that page.";
                    _ok.Enabled = true;
                    return;
                }
                var session = _session;
                Tokens = await Task.Run(() => OAuthClient.ExchangeCode(value, session.Verifier));
                Kind = AccountKind.OAuth;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _ok.Enabled = true;
        }
    }
}
