using System.Diagnostics;

namespace ClaudeLimitWidget;

/// <summary>
/// Standalone sign-in: opens the Claude OAuth page in the user's browser and
/// accepts the pasted authorization code — or a directly pasted long-lived
/// token (e.g. from `claude setup-token`). Saves the result to TokenStore.
/// </summary>
public sealed class SignInDialog : Form
{
    private readonly TextBox _input;
    private readonly Label _status;
    private readonly Button _ok;
    private OAuthClient.PkceSession? _session;

    public SignInDialog()
    {
        Text = "Sign in to Claude — standalone mode";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 240);
        BackColor = Color.FromArgb(0x20, 0x20, 0x22);
        ForeColor = Color.FromArgb(0xE6, 0xE6, 0xE6);
        Font = new Font("Segoe UI", 9f);

        var steps = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(16, 12, 448, 74),
            Text = "1. Click “Open sign-in page” and log into the Claude account to track.\n" +
                   "2. Approve access, copy the code shown, and paste it below.\n\n" +
                   "Alternatively, paste a long-lived token from `claude setup-token` directly.",
        };

        var openBtn = new Button
        {
            Text = "Open sign-in page",
            Bounds = new Rectangle(16, 92, 150, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = ForeColor,
        };
        openBtn.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4A, 0x4E);
        openBtn.Click += (_, _) =>
        {
            _session = OAuthClient.StartSignIn();
            try
            {
                Process.Start(new ProcessStartInfo(_session.AuthorizeUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _status!.Text = "Could not open browser: " + ex.Message;
            }
        };

        _input = new TextBox
        {
            Bounds = new Rectangle(16, 136, 448, 26),
            PlaceholderText = "Paste the authorization code (or an sk-ant-… token) here",
            BackColor = Color.FromArgb(0x2A, 0x2A, 0x2E),
            ForeColor = ForeColor,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _status = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(16, 168, 448, 34),
            ForeColor = Color.FromArgb(0xE0, 0x96, 0x3C),
        };

        _ok = new Button
        {
            Text = "Save",
            Bounds = new Rectangle(300, 204, 78, 28),
            FlatStyle = FlatStyle.Flat,
            ForeColor = ForeColor,
        };
        _ok.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4A, 0x4E);
        _ok.Click += (_, _) => Submit();

        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(386, 204, 78, 28),
            FlatStyle = FlatStyle.Flat,
            ForeColor = ForeColor,
            DialogResult = DialogResult.Cancel,
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4A, 0x4E);

        Controls.AddRange(new Control[] { steps, openBtn, _input, _status, _ok, cancel });
        AcceptButton = _ok;
        CancelButton = cancel;
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
            StoredTokens tokens;
            if (value.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase))
            {
                // Direct long-lived token (claude setup-token). No refresh token; it
                // simply expires when the token does.
                tokens = new StoredTokens { AccessToken = value, RefreshToken = "", ExpiresAtMs = 0 };
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
                tokens = await Task.Run(() => OAuthClient.ExchangeCode(value, session.Verifier));
            }

            TokenStore.Save(tokens);
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
