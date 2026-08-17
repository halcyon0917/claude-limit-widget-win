namespace ClaudeLimitWidget;

/// <summary>
/// Hidden top-level window that receives the "TaskbarCreated" broadcast
/// (sent when explorer restarts or primary-display DPI changes). The embedded
/// widget itself is a child of the taskbar and never receives broadcasts.
/// </summary>
public sealed class MessageWindow : NativeWindow, IDisposable
{
    private readonly uint _taskbarCreatedMsg;

    public event Action? TaskbarCreated;

    public MessageWindow()
    {
        _taskbarCreatedMsg = TaskbarInterop.RegisterWindowMessage("TaskbarCreated");
        CreateHandle(new CreateParams
        {
            Caption = "ClaudeLimitWidgetMsg",
            X = 0, Y = 0, Width = 0, Height = 0,
        });
    }

    protected override void WndProc(ref Message m)
    {
        if ((uint)m.Msg == _taskbarCreatedMsg)
            TaskbarCreated?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}
