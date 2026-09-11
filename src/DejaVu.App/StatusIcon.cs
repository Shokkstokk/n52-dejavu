using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using DejaVu.Core.Engine;
using DejaVu.Core.Hardware;
using DejaVu.App.Localization;

namespace DejaVu.App;

/// <summary>
/// The notification-area icon. This is the application's permanent presence: the engine keeps
/// running even when the editor is closed.
///
/// <para><b>The shell's own API, called directly.</b> WPF has no notification icon of its
/// own, so the icon is registered with Shell_NotifyIcon against a hidden window of this
/// application's making. The bitmap is composed with GDI+, which is what System.Drawing is
/// doing here.</para>
///
/// <para>The menu is an ordinary WPF ContextMenu, so it picks up this application's own
/// ContextMenu, MenuItem and Separator templates and follows the chosen theme like every
/// other menu in the window.</para>
/// </summary>
public sealed class StatusIcon : IDisposable
{
    private const int NimAdd = 0x00;
    private const int NimModify = 0x01;
    private const int NimDelete = 0x02;

    private const int NifMessage = 0x01;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;

    /// <summary>Our own callback message. WM_APP is the first id an application may define.</summary>
    private const int CallbackMessage = 0x8000 + 1;

    private const int WmLeftDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;

    private readonly PadRuntime _engine;
    private readonly Dispatcher _dispatcher;
    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;

    private readonly ContextMenu _menu;
    private readonly MenuItem _open;
    private readonly MenuItem _profile;
    private readonly MenuItem _quit;

    private NotifyIconData _data;
    private bool _added;
    private bool _disposed;

    /// <summary>
    /// One icon per lamp combination, built once and kept, as raw HICONs.
    ///
    /// Eight of them, because three lamps are three bits. Cached rather than redrawn on every
    /// mode change: a mode key can be held, and drawing a fresh icon per keystroke would churn
    /// GDI handles for a picture that only ever takes eight forms. Held as handles rather than
    /// System.Drawing.Icon objects because the shell wants the handle anyway, and an Icon built
    /// with FromHandle does not own what it wraps - which made freeing them a second bookkeeping
    /// list that had to be kept in step with this one.
    /// </summary>
    private readonly Dictionary<(LedState State, bool Stopped), IntPtr> _icons = [];

    public event Action? OpenRequested;
    public event Action? QuitRequested;

    public StatusIcon(PadRuntime engine)
    {
        _engine = engine;
        _dispatcher = System.Windows.Application.Current.Dispatcher;

        // A real top-level window, deliberately never shown, rather than a message-only one.
        // Message-only windows do not receive broadcasts, and TaskbarCreated - which is how
        // the shell tells everyone to put their icons back after Explorer restarts - is
        // broadcast. WS_EX_TOOLWINDOW keeps it off the taskbar and out of Alt+Tab.
        _window = new HwndSource(new HwndSourceParameters("n52 DejaVu tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x80,
        });

        _window.AddHook(OnMessage);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        (_menu, _open, _profile, _quit) = BuildMenu();

        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = IconFor(engine.CurrentMode, offline: !engine.Connected),
            szTip = Strings.Get("AppTitle"),
        };

        _added = Shell_NotifyIcon(NimAdd, ref _data);
        RefreshIcon();

        // The lamps on the pad and the icon in the tray show the same thing, so they change
        // together. This is the only sign of the current keymap while the editor is closed,
        // which is most of the time.
        //
        // Both of these arrive on the engine's own thread, and Shell_NotifyIcon has to be
        // called from the thread that owns the window. BeginInvoke rather than Invoke: the
        // engine raises these while holding its own locks, and waiting on the interface thread
        // from inside one is how a deadlock gets built.
        _engine.ModeChanged += _ => _dispatcher.BeginInvoke(RefreshIcon);

        // A pad that is not connected draws unlit whatever the mode: showing a keymap lit
        // would claim the pad is doing something it is not.
        _engine.Status += _ => _dispatcher.BeginInvoke(RefreshIcon);
    }

    // =====================================================================
    //  The menu
    // =====================================================================

    private (ContextMenu Menu, MenuItem Open, MenuItem Profile, MenuItem Quit) BuildMenu()
    {
        var menu = new ContextMenu();

        var title = new TextBlock
        {
            Text = Strings.Get("AppTitle"),
            FontWeight = System.Windows.FontWeights.SemiBold,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new System.Windows.Thickness(9, 0, 0, 0),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        // A MenuItem that cannot be interacted with, rather than a bare panel.
        //
        // The bare panel was the first attempt and was wrong: a Menu builds a MenuItem
        // container for anything that is not already a MenuItem or a Separator, so the panel
        // was wrapped in one and behaved like an entry - it highlighted on hover and closed
        // the menu when clicked. IsHitTestVisible stops both at the source, since a highlight
        // needs the pointer to land on it. Focusable false keeps the keyboard off it too.
        //
        // Not IsEnabled=false, which is the obvious alternative: the template dims a disabled
        // item to half opacity, and a title that looks disabled reads as something broken
        // rather than as a heading. That treatment is right for the Profile line below, which
        // does look like an entry and has to say plainly that pressing it does nothing.
        var header = new MenuItem
        {
            IsHitTestVisible = false,
            Focusable = false,
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new System.Windows.Controls.Image
                    {
                        Width = 20,
                        Height = 20,
                        Source = AppIcon(),
                    },
                    title,
                },
            },
        };

        var open = Item(Strings.Get("OpenEditor"), () => OpenRequested?.Invoke());
        open.FontWeight = System.Windows.FontWeights.Bold;

        // Not a command: it reports which profile is in use. Disabled is exactly right here,
        // unlike on the title above - this one looks like a menu entry because it sits among
        // them, so it has to say plainly that pressing it does nothing.
        var profile = new MenuItem { IsEnabled = false };

        var quit = Item(Strings.Get("QuitLabel"), () => QuitRequested?.Invoke());

        menu.Items.Add(header);
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(profile);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        return (menu, open, profile, quit);

        MenuItem Item(string header, Action run)
        {
            var item = new MenuItem { Header = header };

            // Posted, not run inline. A menu only disappears once the click handler has
            // returned and the interface thread gets a frame to draw in - and Quit releases
            // the pad and the foreground watcher, each of which waits up to a second for its
            // thread to come home. Run inline, that left the menu painted on screen for
            // seconds after the icon had gone, which reads as a hang.
            item.Click += (_, _) => _dispatcher.BeginInvoke(DispatcherPriority.Background, run);
            return item;
        }
    }

    /// <summary>
    /// The application's own icon, for the menu's title row.
    ///
    /// DecodePixelWidth matters for an .ico: the file holds several sizes and WPF picks the
    /// frame closest to the size asked for. Without it the 256-pixel frame is decoded and
    /// scaled down to twenty, which is both wasteful and softer than the frame drawn for
    /// roughly this size.
    /// </summary>
    private static BitmapImage AppIcon()
    {
        var icon = new BitmapImage();

        icon.BeginInit();
        icon.UriSource = new Uri("pack://application:,,,/Assets/n52dejavu.ico");
        icon.DecodePixelWidth = 20;
        icon.CacheOption = BitmapCacheOption.OnLoad;
        icon.EndInit();
        icon.Freeze();

        return icon;
    }

    /// <summary>
    /// Opens the menu at the pointer.
    ///
    /// SetForegroundWindow first, and it is not optional: a menu raised from a window that is
    /// not in the foreground does not receive activation, which means it never notices the
    /// click that should dismiss it and hangs on screen until something else takes focus. This
    /// is the oldest wart in notification-area programming and every implementation carries the
    /// same two lines.
    /// </summary>
    private void ShowMenu()
    {
        Refresh();
        SetForegroundWindow(_window.Handle);

        GetCursorPos(out var cursor);

        // The shell reports physical pixels; WPF places popups in device-independent units.
        var point = _window.CompositionTarget.TransformFromDevice.Transform(
            new System.Windows.Point(cursor.X, cursor.Y));

        _menu.Placement = PlacementMode.AbsolutePoint;
        _menu.HorizontalOffset = point.X;
        _menu.VerticalOffset = point.Y;
        _menu.IsOpen = true;
    }

    private void Refresh()
    {
        _open.Header = Strings.Get("OpenEditor");
        _quit.Header = Strings.Get("QuitLabel");
        _profile.Header = Strings.Get("TrayProfile", _engine.ActiveProfile?.Name ?? "—");
    }

    // =====================================================================
    //  Window messages
    // =====================================================================

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            switch ((int)lParam)
            {
                case WmLeftDoubleClick:
                    OpenRequested?.Invoke();
                    handled = true;
                    break;

                case WmRightButtonUp:
                    ShowMenu();
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        // Explorer has restarted and every icon in the notification area went with it. Without
        // this the application keeps running, keeps remapping, and becomes invisible and
        // unquittable - the tray icon being the only way to reach it.
        if (message == _taskbarCreated && !_disposed)
        {
            _added = Shell_NotifyIcon(NimAdd, ref _data);
            RefreshIcon();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void RefreshIcon()
    {
        if (_disposed) return;

        _data.hIcon = IconFor(_engine.CurrentMode, offline: !_engine.Connected);

        // The icon says which lamps are lit; the tooltip says which keymap that is. Reading a
        // combination of three colors back to a number is not something anyone should have to
        // do from memory.
        //
        // With no pad it names no keymap at all. CurrentMode still holds whichever one was
        // last selected, so reporting it would be describing the state of a device that is
        // not there - and the lamps it refers to are not lit, because there is nothing to
        // light them.
        _data.szTip = Truncate(_engine.Connected
            ? Strings.Get("TrayKeymap", Strings.Get($"ModeName{_engine.CurrentMode + 1}"))
            : Strings.Get("TrayNoController"));

        if (_added) Shell_NotifyIcon(NimModify, ref _data);
    }

    // =====================================================================
    //  Drawing the icon
    // =====================================================================

    /// <summary>The three lamps, and the artwork each one wears lit and unlit.</summary>
    private static readonly (LedState Lamp, string Name)[] Lamps =
    [
        (LedState.Red, "red"),
        (LedState.Green, "green"),
        (LedState.Blue, "blue"),
    ];

    /// <summary>
    /// The artwork, loaded once each and kept for the session.
    ///
    /// Four files: the plate with every lamp dark, and one lit lamp each. Loading them on
    /// demand rather than up front costs nothing - whichever keymap is live, at most four are
    /// ever asked for.
    /// </summary>
    private readonly Dictionary<string, Bitmap> _art = [];

    private Bitmap Art(string name)
    {
        if (_art.TryGetValue(name, out var cached)) return cached;

        var uri = new Uri($"Assets/{name}.png", UriKind.Relative);
        using var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        return _art[name] = new Bitmap(stream);
    }

    /// <summary>
    /// The icon: three lamps showing the current keymap, or the n when there is no pad.
    ///
    /// The pad shows the current keymap on its own lamps, but only while it is in view. This
    /// puts the same information in the notification area, which is where the application
    /// lives whenever the editor is closed.
    ///
    /// Composed from artwork rather than drawn: eight keymaps times two states is sixteen
    /// combinations of three bars, and building them from six files means the lamps in the
    /// tray are the same drawing as the lamps everywhere else rather than a second version
    /// of them that has to be kept in step by hand.
    ///
    /// A pad that is not connected is not drawn as lamps at all. Drawing it as three unlit
    /// lamps made it pixel-identical to keymap 1, which lights none either - the icon claimed
    /// a keymap was active while the pad was doing nothing. Dimming them fixed the ambiguity
    /// but not the noticing: a dimmer version of the same shape still has to be looked at to
    /// be read. The n is a different silhouette, so it registers without being examined, and
    /// "no pad" is not a keymap state - it is the absence of one, so drawing it in the
    /// keymap's own language was wrong to begin with.
    /// </summary>
    private IntPtr IconFor(int mode, bool offline)
    {
        var state = !offline && mode >= 0 && mode < PadRuntime.ModeLeds.Length
            ? PadRuntime.ModeLeds[mode]
            : LedState.Off;

        if (_icons.TryGetValue((state, offline), out var cached)) return cached;

        using var bitmap = new Bitmap(32, 32);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(System.Drawing.Color.Transparent);

            // The plate carries every lamp already dark, and a lit lamp is laid over its own
            // dark twin. Both are the same canvas and the same path, so a lamp covers exactly
            // what it replaces - there is no alignment arithmetic here to drift when the
            // artwork moves, which is the whole reason the parts are cut this way.
            //
            // The plate is also what makes the icon legible. Three bars alone had no
            // silhouette: on a dark taskbar with few lamps lit they read as faint marks
            // floating on nothing, and a tray icon that fades into the taskbar says the
            // application has crashed - a worse thing to communicate than a dull icon. An
            // edge gives it an outline to be seen by whatever colour is behind it.
            var whole = new Rectangle(0, 0, 32, 32);
            g.DrawImage(Art("tray-plate"), whole);

            foreach (var (lamp, name) in Lamps)
            {
                if (state.HasFlag(lamp)) g.DrawImage(Art($"tray-lamp-{name}"), whole);
            }

            if (offline)
            {
                // Struck through, and this is the whole reason the state is legible.
                //
                // No pad lights no lamps, and neither does keymap 1 - so drawn as lamps alone
                // the two states are the same picture, and the icon claims a keymap is active
                // while the pad is not there. The slash is what separates them, and it has to
                // be a mark of a different KIND rather than a different arrangement of the
                // same bars: a diagonal over three verticals reads without being studied,
                // which is the only kind of reading a 16-pixel icon gets.
                //
                // Corner to corner over the whole bitmap, not just the bars. A slash inset to
                // the artwork looks like part of it; one that runs off the edges reads as
                // something laid on top.
                using var slash = new Pen(System.Drawing.Color.FromArgb(0xE0, 0x52, 0x52), 4f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };

                g.DrawLine(slash, 4, 28, 28, 4);
            }
        }

        return Cache(state, offline, bitmap);
    }

    /// <summary>Turns a drawn bitmap into an icon handle and keeps it for the session.</summary>
    private IntPtr Cache(LedState state, bool offline, Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        _icons[(state, offline)] = handle;
        return handle;
    }

    /// <summary>
    /// Keeps the tooltip inside the field the shell gives it.
    ///
    /// NOTIFYICONDATAW.szTip is 128 characters including the terminator, and the marshaller
    /// throws rather than truncates when a longer string is handed to a fixed-length field.
    /// </summary>
    private static string Truncate(string value)
        => value.Length <= 127 ? value : value[..124] + "...";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added) Shell_NotifyIcon(NimDelete, ref _data);

        _menu.IsOpen = false;
        foreach (var art in _art.Values) art.Dispose();
        _art.Clear();

        // Nothing else owns these: GetHicon hands back a handle and forgets about it.
        foreach (var handle in _icons.Values) DestroyIcon(handle);
        _icons.Clear();

        _window.RemoveHook(OnMessage);
        _window.Dispose();
    }

    // =====================================================================
    //  Native
    // =====================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
