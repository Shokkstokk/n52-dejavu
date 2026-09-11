using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using DejaVu.App.Localization;

namespace DejaVu.App;

/// <summary>
/// The "press the key you want" dialog.
///
/// What it records is the scancode - the physical position of the key - rather than the
/// letter printed on it. That is what keeps an assignment correct whatever keyboard layout is
/// active, and it is what the pad and the games agree on.
///
/// <para><b>The key is read from the window message, not from WPF.</b> WPF hands a keystroke
/// over as a <c>Key</c> enum value, which says what was pressed but not where. Turning one
/// back into a scancode means MapVirtualKey, which drops the extended-key bit, and the arrows
/// then come back indistinguishable from the numeric keypad - Right Arrow and Num 6 are both
/// scancode 0x4D, Up and Num 8 both 0x48. Compensating for that needs a hand-kept list of
/// which virtual keys are extended, and such a list is only ever as right as the last person
/// to edit it.</para>
///
/// <para>Reading the raw key message removes the problem rather than working around it. The
/// scancode and the extended flag both travel in lParam, put there by the keyboard driver, so
/// they are read rather than reconstructed - correct for every key, including any this code
/// has never heard of. Two keys still need help: Num Lock lies about being extended, and
/// Pause cannot be described by a scancode and a flag at all. Both are handled where the
/// message is read.</para>
/// </summary>
public sealed class KeyPrompt : Window
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    /// <summary>lParam bit 24: the key is from the extended set, so its scancode carries E0.</summary>
    private const long ExtendedFlag = 1L << 24;

    /// <summary>lParam bit 30: the key was already down, so this is an auto-repeat.</summary>
    private const long WasAlreadyDown = 1L << 30;

    private const int VkPause = 0x13;
    private const int VkNumLock = 0x90;

    private readonly TextBlock _preview;
    private readonly Button _accept;

    /// <summary>Scancode of the captured key.</summary>
    public ushort ScanCode { get; private set; }

    /// <summary>True when that scancode needs the E0 prefix to mean what was pressed.</summary>
    public bool Extended { get; private set; }

    public KeyPrompt()
    {
        Title = Strings.Get("CaptureTitle");
        Owner = null;
        Width = 380;

        // Height is measured, never stated. The instruction above wraps to as many lines as
        // its wording needs, and a fixed height once cut the buttons off the bottom when that
        // wording grew by a sentence.
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Painted("Background");

        // A window of its own inherits none of the editor's type. References rather than
        // copies, so a change of theme reaches it like everything else.
        SetResourceReference(FontFamilyProperty, "AppFontFamily");
        SetResourceReference(FontSizeProperty, "FontBody");

        var instruction = new TextBlock
        {
            Text = Strings.Get("CaptureHelp"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Painted("TextDim"),
        };

        _preview = new TextBlock
        {
            // Blank until a key arrives. It held an ellipsis, which reads as "working on it"
            // when nothing is happening at all - the window is waiting on the user, not the
            // other way round.
            Text = string.Empty,
            FontWeight = FontWeights.SemiBold,
            Foreground = Painted("Text"),
            TextAlignment = TextAlignment.Center,

            // Sized for a key name, but it also has to carry a sentence on the occasions a
            // key is refused. Wrapping costs nothing for "F13" and stops the other case
            // being sliced down the middle.
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 16),
        };

        // The only display type in the application. This is the answer the window exists to
        // give, and it has the whole window to give it in.
        _preview.SetResourceReference(FontSizeProperty, "FontDisplay");

        // Blank, but the space it will need is held open. An empty TextBlock has no height at
        // all, and the window measures its own height - so it would open short and then jump
        // taller the instant a key arrived, moving its own buttons out from under the pointer.
        if (Application.Current?.Resources["FontDisplay"] is double display)
            _preview.MinHeight = Math.Ceiling(display * 1.4);

        _accept = new Button
        {
            Content = Strings.Get("OkLabel"),
            MinWidth = 100,
            IsEnabled = false,
        };

        var abandon = new Button
        {
            Content = Strings.Get("CancelLabel"),
            MinWidth = 100,
            Margin = new Thickness(0, 0, 6, 0),
        };

        _accept.Click += (_, _) => Finish(true);
        abandon.Click += (_, _) => Finish(false);

        var choices = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { abandon, _accept },
        };

        // Three rows: say what to do, show what arrived, offer the way out. The middle row
        // takes whatever height the answer needs so the buttons never move as it changes.
        var body = new Grid { Margin = new Thickness(18) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(instruction, 0);
        Grid.SetRow(_preview, 1);
        Grid.SetRow(choices, 2);

        body.Children.Add(instruction);
        body.Children.Add(_preview);
        body.Children.Add(choices);

        Content = body;
    }
    private void Finish(bool keep)
    {
        DialogResult = keep;
        Close();
    }

    private static Brush Painted(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>
    /// Listens ahead of WPF, not behind it.
    ///
    /// An HwndSource hook is the tidier-looking option and does not work: WPF claims Tab for
    /// focus navigation before user hooks in that chain are reached, so the one key most
    /// likely to be assigned to a pad button could never be captured. ThreadPreprocessMessage
    /// sees the raw message before any of that, which is the only place every key is still
    /// on the table.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ComponentDispatcher.ThreadPreprocessMessage += Intercept;
    }

    /// <summary>Stops listening. This filter is thread-wide, so leaving it attached would
    /// swallow every keystroke in the application once the dialog had gone.</summary>
    protected override void OnClosed(EventArgs e)
    {
        ComponentDispatcher.ThreadPreprocessMessage -= Intercept;
        base.OnClosed(e);
    }

    /// <summary>
    /// Takes every key message the application receives while this is open, and lets none of
    /// them through.
    ///
    /// Swallowing them all is the point rather than a side effect: any key has to be
    /// capturable, so Escape must not close the window, Space and Enter must not press the
    /// buttons behind the message, and Alt must not open a system menu. Those stay reachable
    /// with the mouse for as long as this is open.
    /// </summary>
    private void Intercept(ref MSG message, ref bool handled)
    {
        var pressed = message.message is WmKeyDown or WmSysKeyDown;
        var released = message.message is WmKeyUp or WmSysKeyUp;

        if (!pressed && !released) return;

        handled = true;

        var details = (long)message.lParam;
        var virtualKey = (int)message.wParam;

        // Bit 30 means "was already down", which is true of every release and of every
        // auto-repeat. Only a repeat is worth discarding.
        if (pressed && (details & WasAlreadyDown) != 0) return;

        // Releases are read as well as presses, for one key: Print Screen is delivered by
        // Windows as a key-up with no key-down at all, so a press-only reader never sees it.
        // Every other key simply reports itself twice with the same answer.
        var scanCode = (ushort)((details >> 16) & 0xFF);
        if (scanCode == 0) return;

        var extended = (details & ExtendedFlag) != 0;

        // Pause cannot be expressed as a scancode and an extended flag. The real sequence is
        // E1-prefixed - E1 1D 45 - and what Windows reports of it is 0x45, which is Num Lock's
        // code. Assigning it would silently bind Num Lock instead, so it is refused outright.
        if (virtualKey == VkPause)
        {
            _preview.Text = Strings.Get("CaptureUnsupported");
            _accept.IsEnabled = false;
            return;
        }

        // Num Lock is reported with the extended bit set even though its scancode carries no
        // E0 prefix - a Windows quirk, not a property of the key. Left alone it would be
        // stored as an extended 0x45, which is nothing at all, and sent as nothing.
        if (virtualKey == VkNumLock) extended = false;

        ScanCode = scanCode;
        Extended = extended;

        _preview.Text = Core.Input.KeyList.Describe(ScanCode, Extended);
        _accept.IsEnabled = true;
    }
}
