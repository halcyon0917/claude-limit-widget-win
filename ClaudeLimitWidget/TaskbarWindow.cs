using static ClaudeLimitWidget.TaskbarInterop;

namespace ClaudeLimitWidget;

/// <summary>
/// The widget window. Preferred mode: embedded as a child of the real Win11
/// taskbar (Shell_TrayWnd), positioned just left of the tray (TrayNotifyWnd).
/// Falls back to a topmost floating tool window at the same spot when
/// embedding is unavailable.
/// </summary>
public sealed class TaskbarWindow : Form
{
    private static readonly Color KeyColor = Color.FromArgb(0, 0, 1); // not pure black: taskbar bg isn't

    private readonly WidgetRenderer _renderer = new();
    private readonly HoverTooltip _tooltip = new();
    private readonly System.Windows.Forms.Timer _positionTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 50 };

    private UsageSnapshot _usage = UsageSnapshot.Empty;
    private IntPtr _taskbar;
    private IntPtr _tray;
    private bool _embedded;
    private bool _forceFloat;
    private Rectangle _lastTarget;
    private int _reembedAttempts;
    private int _slot;

    public bool IsEmbedded => _embedded;

    /// <summary>
    /// True when this window can no longer be used and the owner must dispose and
    /// recreate it: either the OS destroyed our HWND, or the taskbar we were
    /// embedded in died (a reparented survivor never composites correctly on the
    /// recreated Win11 taskbar — a fresh HWND is the only reliable recovery).
    /// </summary>
    public bool NeedsRecreate { get; private set; }

    public TaskbarWindow(bool forceFloat)
    {
        _forceFloat = forceFloat;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;

        _positionTimer.Tick += (_, _) => EnsurePlacement();
        _animTimer.Tick += (_, _) => AnimTick();

        MouseEnter += (_, _) => _tooltip.ShowFor(_usage, RectangleToScreen(ClientRectangle), _renderer.StaleAfter);
        MouseLeave += (_, _) => _tooltip.Hide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!RecreatingHandle && !Disposing && !IsDisposed)
        {
            Log.Write("HWND destroyed externally → recreate requested");
            MarkDead();
        }
        base.OnHandleDestroyed(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void Start()
    {
        EnsurePlacement();
        Show();
        _positionTimer.Start();
        _animTimer.Start();
    }

    public void SetUsage(UsageSnapshot usage)
    {
        _usage = usage;
        if (IsHandleCreated)
            Invalidate();
    }

    public void SetStaleAfter(TimeSpan t) => _renderer.StaleAfter = t;

    /// <summary>
    /// Position among the widgets, 0 = nearest the tray. Widgets stack leftwards,
    /// so slot n sits n widget-widths further from the clock.
    /// </summary>
    public int Slot
    {
        get => _slot;
        set
        {
            if (_slot == value)
                return;
            _slot = value;
            _lastTarget = Rectangle.Empty; // force a reposition on the next tick
            if (IsHandleCreated)
                EnsurePlacement();
        }
    }

    /// <summary>Account name shown on the widget; empty uses the single-account layout.</summary>
    public void SetAccountLabel(string label)
    {
        if (_renderer.AccountLabel == label)
            return;
        _renderer.AccountLabel = label;
        if (IsHandleCreated)
            Invalidate();
    }

    public void SetTransparent(bool transparent)
    {
        _renderer.Transparent = transparent;
        if (IsHandleCreated)
            Invalidate();
    }

    /// <summary>Switch between embedded and float mode (tray menu).</summary>
    public void SetFloatMode(bool floatMode)
    {
        if (_forceFloat == floatMode)
            return;
        _forceFloat = floatMode;
        Detach();
        _lastTarget = Rectangle.Empty;
        EnsurePlacement();
    }

    /// <summary>
    /// Called when explorer restarts (TaskbarCreated broadcast) and this window was
    /// NOT embedded (float mode / never embedded) — embedded windows are recreated
    /// by the owner instead. Resets the retry budget and re-attempts embedding.
    /// </summary>
    public void OnTaskbarRecreated()
    {
        if (_embedded)
            return; // EnsurePlacement will detect the dead parent and request recreate
        _lastTarget = Rectangle.Empty;
        _reembedAttempts = 0;
        EnsurePlacement();
    }

    // ---- placement ----

    private void EnsurePlacement()
    {
        if (NeedsRecreate || IsDisposed)
            return;

        if (_embedded && (!IsWindow(_taskbar) || GetAncestor(Handle, GA_PARENT) != _taskbar))
        {
            // Our taskbar parent died (explorer restart). Reparenting this HWND
            // into the new taskbar leaves it invisible, so ask for a rebuild.
            Log.Write("taskbar parent died → recreate requested");
            MarkDead();
            return;
        }

        if (!_embedded && !_forceFloat && _reembedAttempts < 15)
            TryEmbed();

        Reposition();
    }

    private void MarkDead()
    {
        NeedsRecreate = true;
        _positionTimer.Stop();
        _animTimer.Stop();
        _tooltip.Hide();
    }

    private void TryEmbed()
    {
        _reembedAttempts++;

        var taskbar = FindRealTaskbar();
        if (taskbar == IntPtr.Zero)
        {
            Log.Write($"TryEmbed #{_reembedAttempts}: no real taskbar found");
            return;
        }

        var tray = FindTrayNotify(taskbar);
        if (tray == IntPtr.Zero)
        {
            Log.Write($"TryEmbed #{_reembedAttempts}: no TrayNotifyWnd");
            return;
        }

        if (SetParent(Handle, taskbar) == IntPtr.Zero)
        {
            Log.Write($"TryEmbed #{_reembedAttempts}: SetParent failed");
            return; // blocked (AV, integrity) → float fallback keeps working
        }

        Log.Write($"TryEmbed #{_reembedAttempts}: embedded into {taskbar:X}");

        _taskbar = taskbar;
        _tray = tray;
        _embedded = true;
        _reembedAttempts = 0;
        _lastTarget = Rectangle.Empty;
    }

    private void Detach()
    {
        if (_embedded)
        {
            SetParent(Handle, IntPtr.Zero);
            _embedded = false;
        }
        _taskbar = IntPtr.Zero;
    }

    private void Reposition()
    {
        IntPtr taskbar = _embedded ? _taskbar : FindRealTaskbar();
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out RECT tbRect))
            return;

        IntPtr tray = _embedded ? _tray : FindTrayNotify(taskbar);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out RECT trayRect))
            return;

        // Right after an explorer restart the tray reports a zero/degenerate rect
        // for a moment; positioning against it would fling us off-screen.
        if (trayRect.Width <= 0 || trayRect.Left <= tbRect.Left)
            return;

        int height = tbRect.Height;
        int width = WidgetRenderer.WidthFor(height);
        // Slot 0 sits just left of the tray; each further slot is one widget to its left.
        int offset = 4 + _slot * (width + 4);

        if (_embedded)
        {
            // Child coordinates are relative to the taskbar's client area.
            var target = new RECT
            {
                Left = trayRect.Left - width - offset,
                Top = tbRect.Top,
                Right = trayRect.Left - offset,
                Bottom = tbRect.Top + height,
            };
            MapWindowPoints(IntPtr.Zero, taskbar, ref target);
            var rect = new Rectangle(target.Left, target.Top, width, height);
            if (rect != _lastTarget)
            {
                SetWindowPos(Handle, HWND_TOP, rect.X, rect.Y, rect.Width, rect.Height,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE);
                _lastTarget = rect;
                Invalidate();
                GetWindowRect(Handle, out RECT actual);
                Log.Write($"Reposition embedded: target={rect} actualScreen=({actual.Left},{actual.Top},{actual.Width}x{actual.Height}) visible={IsWindowVisible(Handle)}");
            }
        }
        else
        {
            var rect = new Rectangle(trayRect.Left - width - offset, tbRect.Top, width, height);
            if (rect != _lastTarget)
            {
                TopMost = true;
                Bounds = rect;
                _lastTarget = rect;
                Invalidate();
            }
        }
    }

    // ---- animation ----

    private void AnimTick()
    {
        if (_renderer.Mascot.Tick(_animTimer.Interval))
            Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _renderer.Paint(e.Graphics, ClientRectangle, _usage);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _positionTimer.Dispose();
            _animTimer.Dispose();
            _renderer.Dispose();
            _tooltip.Dispose();
        }
        base.Dispose(disposing);
    }
}
