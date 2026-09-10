namespace ClaudeLimitWidget;

/// <summary>Small single-field prompt, used for renaming an account.</summary>
public static class TextPrompt
{
    /// <summary>Returns the entered text, or null when cancelled.</summary>
    public static string? Show(string title, string caption, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(380, 128),
            BackColor = Color.FromArgb(0x20, 0x20, 0x22),
            ForeColor = Color.FromArgb(0xE6, 0xE6, 0xE6),
            Font = new Font("Segoe UI", 9f),
        };

        var label = new Label { AutoSize = false, Bounds = new Rectangle(16, 14, 348, 22), Text = caption };
        var box = new TextBox
        {
            Bounds = new Rectangle(16, 42, 348, 26),
            Text = initial,
            BackColor = Color.FromArgb(0x2A, 0x2A, 0x2E),
            ForeColor = form.ForeColor,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var ok = Button("OK", new Rectangle(200, 86, 78, 28), form.ForeColor);
        ok.DialogResult = DialogResult.OK;
        var cancel = Button("Cancel", new Rectangle(286, 86, 78, 28), form.ForeColor);
        cancel.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
    }

    private static Button Button(string text, Rectangle bounds, Color fore)
    {
        var b = new Button { Text = text, Bounds = bounds, FlatStyle = FlatStyle.Flat, ForeColor = fore };
        b.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4A, 0x4E);
        return b;
    }
}
