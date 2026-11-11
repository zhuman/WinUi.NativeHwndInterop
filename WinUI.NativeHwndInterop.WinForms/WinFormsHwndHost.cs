using System.ComponentModel;
using WfContainerControl = System.Windows.Forms.ContainerControl;
using WfControl = System.Windows.Forms.Control;
using WfDockStyle = System.Windows.Forms.DockStyle;
using WfMessage = System.Windows.Forms.Message;
using WfPreProcessControlState = System.Windows.Forms.PreProcessControlState;

namespace WinUI.NativeHwndInterop.WinForms;

/// <summary>
/// Hosts a Windows Forms <see cref="System.Windows.Forms.Control"/> inside a WinUI layout.
/// </summary>
/// <remarks>
/// <para>The caller owns <see cref="Child"/>. The host never disposes it; when the host is unloaded,
/// recreated or its child changes, the control is detached (and its original <c>Dock</c> restored) so it can be reused.</para>
/// <para>Supported: native-HWND keyboard preprocessing through <c>Control.PreProcessControlMessage</c>
/// (command keys, dialog keys, arrow navigation, multiline Tab semantics, Ctrl/Alt/Shift modifiers and mnemonics),
/// and Tab/Shift+Tab traversal that stops at the first/last tab stop (no wrap) and then hands focus back to XAML.</para>
/// <para>Not supported: <c>Application.Idle</c>/<c>Application.AddMessageFilter</c> notifications (no WinForms message loop runs),
/// mouse-wheel chaining between XAML and the hosted control, Form-level features (AcceptButton/CancelButton, menus, KeyPreview on a parent form),
/// per-monitor DPI re-scaling of the hosted control, and hosting a top-level <c>Form</c>.
/// The control must be created and used on the WinUI UI thread.</para>
/// </remarks>
public class WinFormsHwndHost : NativeHwndHost
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WfControl, WeakReference<WinFormsHwndHost>> Owners = new();
    private WfControl? _child;

    /// <summary>Gets or sets the caller-owned control. It must not be parented, assigned to another host, or a top-level form.</summary>
    /// <exception cref="InvalidOperationException">The host or control belongs to another UI thread, or the control is already parented or assigned to another host.</exception>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public WfControl? Child
    {
        get
        {
            VerifyUiThread();
            return _child;
        }
        set
        {
            VerifyUiThread();
            if (ReferenceEquals(_child, value))
            {
                return;
            }

            if (value is not null)
            {
                ValidateChild(value);
            }

            lock (Owners)
            {
                if (value is not null)
                {
                    if (Owners.TryGetValue(value, out var owner) && owner.TryGetTarget(out var host) && !ReferenceEquals(host, this))
                    {
                        throw new InvalidOperationException("The control is already assigned to another host. Clear that host's Child first.");
                    }

                    Owners.Remove(value);
                    Owners.Add(value, new WeakReference<WinFormsHwndHost>(this));
                }

                if (_child is not null)
                {
                    Owners.Remove(_child);
                }

                _child = value;
            }

            RecreateContent();
        }
    }

    /// <inheritdoc />
    protected override INativeHwndContent? CreateContent(nint parentWindow)
    {
        VerifyUiThread();
        if (_child is null)
        {
            return null;
        }

        ValidateChild(_child);
        return new WinFormsContent(parentWindow, _child);
    }

    private static void ValidateChild(WfControl value)
    {
        if (value.IsHandleCreated && value.InvokeRequired)
        {
            throw new InvalidOperationException("The control's HWND belongs to another UI thread.");
        }

        if (value.IsDisposed)
        {
            throw new ObjectDisposedException(value.GetType().FullName);
        }

        if (value is System.Windows.Forms.Form { TopLevel: true })
        {
            throw new ArgumentException("A top-level Form cannot be hosted. Set TopLevel to false first.", nameof(value));
        }

        if (value.Parent is not null)
        {
            throw new InvalidOperationException("The control already has a parent. Remove it from its parent first.");
        }
    }

    private void VerifyUiThread()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("WinFormsHwndHost must be accessed on its UI thread.");
        }
    }
}

internal sealed class WinFormsContent : INativeHwndContent
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSDEADCHAR = 0x0107;

    private readonly HostContainer _container;
    private readonly WfControl _child;
    private readonly WfDockStyle _originalDock;
    private bool _inTranslate;
    private bool _disposed;

    public WinFormsContent(nint parentWindow, WfControl child)
    {
        _child = child;
        _originalDock = child.Dock;
        _container = new HostContainer(parentWindow) { NavigateOut = OnNavigateOut };
        try
        {
            _child.Dock = WfDockStyle.Fill;
            _container.Controls.Add(_child);
            _container.CreateControl();
            _ = _container.Handle;
        }
        catch
        {
            Detach();
            _container.Dispose();
            throw;
        }
    }

    public nint WindowHandle => _container.IsHandleCreated ? _container.Handle : 0;

    public event EventHandler<NativeFocusNavigationEventArgs>? FocusNavigationRequested;

    public bool Focus(bool forward)
    {
        if (_disposed || !_container.IsHandleCreated)
        {
            return false;
        }

        if (!_container.SelectNextControl(null, forward, tabStopOnly: true, nested: true, wrap: false))
        {
            return false;
        }

        var active = _container.ActiveControl;
        if (active is not null && !active.ContainsFocus)
        {
            active.Focus();
        }

        return true;
    }

    public bool TranslateMessage(ref NativeMessage message)
    {
        if (_disposed || _inTranslate || message.Id < WM_KEYDOWN || message.Id > WM_SYSDEADCHAR)
        {
            return false;
        }

        var control = WfControl.FromChildHandle(message.Window);
        if (control is null)
        {
            return false;
        }

        _inTranslate = true;
        try
        {
            var msg = WfMessage.Create(message.Window, (int)message.Id, (nint)message.WParam, message.LParam);
            // MessageNeeded/MessageNotNeeded both mean "continue with TranslateMessage/DispatchMessage".
            return control.PreProcessControlMessage(ref msg) == WfPreProcessControlState.MessageProcessed;
        }
        finally
        {
            _inTranslate = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _container.NavigateOut = null;
        Detach();
        _container.Dispose();
    }

    private void Detach()
    {
        if (ReferenceEquals(_child.Parent, _container))
        {
            _container.Controls.Remove(_child);
        }

        if (!_child.IsDisposed)
        {
            _child.Dock = _originalDock;
        }
    }

    private bool OnNavigateOut(bool forward)
    {
        var args = new NativeFocusNavigationEventArgs(forward);
        FocusNavigationRequested?.Invoke(this, args);
        return args.Handled;
    }

    /// <summary>Child window of the XAML host HWND that wraps the user control and bounds Tab traversal.</summary>
    private sealed class HostContainer : WfContainerControl
    {
        private readonly nint _parent;

        public HostContainer(nint parent)
        {
            _parent = parent;
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
            TabStop = false;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<bool, bool>? NavigateOut { get; set; }

        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get
            {
                const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
                const int WS_EX_APPWINDOW = 0x40000, WS_EX_CONTROLPARENT = 0x10000;
                var cp = base.CreateParams;
                cp.Parent = _parent;
                cp.Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
                cp.ExStyle = (cp.ExStyle & ~WS_EX_APPWINDOW) | WS_EX_CONTROLPARENT;
                return cp;
            }
        }

        // ActiveControl may be an ancestor of the focused leaf; starting there re-enters its subtree.
        protected override bool ProcessTabKey(bool forward)
        {
            var focused = FindFocusedDescendant(this);
            if (SelectNextControl(focused, forward, tabStopOnly: true, nested: true, wrap: false))
            {
                return true;
            }

            NavigateOut?.Invoke(forward);
            return true;
        }

        private static WfControl? FindFocusedDescendant(WfControl root)
        {
            foreach (WfControl child in root.Controls)
            {
                if (child.ContainsFocus)
                {
                    return FindFocusedDescendant(child) ?? child;
                }
            }

            return root.Focused ? root : null;
        }
    }
}
