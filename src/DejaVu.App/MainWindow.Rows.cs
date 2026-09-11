using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DejaVu.Core.Model;
using DejaVu.App.Localization;

// MouseButton exists in System.Windows.Input too, and both namespaces are imported
// here. In this file it always means the model's version: the button the n52 should
// emit, not the one the user clicked.
using MouseButton = DejaVu.Core.Model.MouseButton;

namespace DejaVu.App;

/// <summary>
/// The assignment lists either side of the device.
///
/// Every control gets one row: a checkbox that switches the assignment on or off, the control's
/// name, and a dropdown holding what it does. The dropdown is the assignment for all but a
/// few kinds - picking "C" from it is the whole interaction - so the settings strip below
/// stays shut unless the chosen kind has something left to say.
///
/// The rows replaced writing the assignments onto the artwork. Labels drawn on the device
/// sat on top of the drawing, collided with the numbers already printed there, and had
/// nowhere to put anything longer than a single key name.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// One entry in a row's dropdown: a complete assignment, not just a kind.
    ///
    /// Flattening the action kinds and their values into a single list is what lets one
    /// dropdown finish the job. "Key: C", "Click: Right" and "Hold Keymap 3" are all one
    /// choice each, so the common case never needs a second control.
    /// </summary>
    private sealed record AssignChoice(
        string Group,
        string Label,
        ActionKind Kind,
        ushort ScanCode = 0,
        bool Extended = false,
        MouseButton MouseButton = MouseButton.Left,
        int WheelDelta = 1,
        ModeSwitch ModeSwitch = ModeSwitch.Cycle,
        int ModeIndex = 0,
        bool Capture = false,
        string? MacroId = null)
    {
        public override string ToString() => Label;
    }

    private static List<AssignChoice>? _choices;

    /// <summary>
    /// The fixed part of what a control can be set to, shared by every row.
    ///
    /// Deliberately does NOT contain the key catalog. Listing all 115 keys here made the
    /// dropdown four fifths keyboard and a chore to navigate, for a choice that is quicker
    /// made by pressing the key. One entry opens the capture dialog instead. The full
    /// catalog is still reachable in the settings strip, which is what keeps F13 to F24
    /// assignable - they exist on no physical keyboard, so they can never be captured.
    /// </summary>
    private static List<AssignChoice> Choices
    {
        get
        {
            if (_choices is not null) return _choices;

            var list = new List<AssignChoice>
            {
                // Silencing a control is the checkbox's job, not a choice in here. Offering both
                // put three near-synonyms in front of the user - "not assigned", "send
                // nothing" and an unchecked row - two of which did the same thing.
                new(Strings.Get("GroupBasic"), Strings.Get("ChoiceDefault"), ActionKind.Passthrough),
            };

            foreach (var b in Enum.GetValues<MouseButton>())
                list.Add(new AssignChoice(Strings.Get("GroupMouse"),
                    Strings.Get($"Click{b}"), ActionKind.MouseButton, MouseButton: b));

            // One entry, not one per direction. Which way it scrolls and how far are both
            // set in the strip, the same as the text an assignment types or the program it
            // opens - a dropdown that listed every direction would have to list every count
            // next, and neither belongs in a list of what an input does.
            list.Add(new AssignChoice(Strings.Get("GroupMouse"), Strings.Get("ChoiceScroll"),
                ActionKind.MouseWheel, WheelDelta: 1));

            list.Add(new AssignChoice(Strings.Get("GroupKeymap"), Strings.Get("ModeSwitchCycle"),
                ActionKind.Mode, ModeSwitch: ModeSwitch.Cycle));

            for (var i = 0; i < Profile.ModeCount; i++)
            {
                var name = Strings.Get($"ModeName{i + 1}");
                list.Add(new AssignChoice(Strings.Get("GroupKeymap"), Strings.Get("ChoiceSwitchTo", name),
                    ActionKind.Mode, ModeSwitch: ModeSwitch.Select, ModeIndex: i));
                list.Add(new AssignChoice(Strings.Get("GroupKeymap"), Strings.Get("ChoiceHold", name),
                    ActionKind.Mode, ModeSwitch: ModeSwitch.Hold, ModeIndex: i));
            }

            // These three cannot be finished in a dropdown - a string, a file path and a
            // sequence of steps are not values you can list - so they open the strip below.
            list.Add(new AssignChoice(Strings.Get("GroupOther"), Strings.Get("ChoiceText"), ActionKind.Text));
            list.Add(new AssignChoice(Strings.Get("GroupOther"), Strings.Get("ChoiceLaunch"), ActionKind.Launch));
            list.Add(new AssignChoice(Strings.Get("GroupOther"), Strings.Get("ChoiceSelectMacro"), ActionKind.Macro));

            return _choices = list;
        }
    }

    /// <summary>Program names already looked up, keyed by path.</summary>
    private static readonly Dictionary<string, string> ProgramNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A readable name for a program, for the row that launches it.
    ///
    /// Windows executables carry a FileDescription resource, which is usually the name a
    /// person would use - "Google Chrome", "Notepad". Usually, not always: GIMP's reads "GNU
    /// Image Manipulation Program", which is accurate and far too long for a dropdown. So the
    /// description is used when it fits and the file name when it does not, which gets
    /// "Google Chrome" from one and "gimp" from the other.
    ///
    /// Cached because the rows refresh often and this reads the file each time otherwise.
    /// </summary>
    private static string ProgramName(string path)
    {
        if (ProgramNames.TryGetValue(path, out var cached)) return cached;

        var name = System.IO.Path.GetFileNameWithoutExtension(path);

        // A shortcut is already named by whoever installed it, and named for people: "GIMP
        // 3.2.0" rather than "gimp". Nothing to look up, and nothing better to be had.
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return ProgramNames[path] = name;

        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var described = info.FileDescription ?? info.ProductName;

            if (!string.IsNullOrWhiteSpace(described) && described.Length <= 22)
                name = described.Trim();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // A path that cannot be read still has a file name, which is enough.
        }

        return ProgramNames[path] = name;
    }

    private static string Ellipsize(string value, int length)
    {
        value = value.Replace(Environment.NewLine, " ").Trim();
        return value.Length <= length ? value : value[..(length - 1)] + "…";
    }

    private sealed record Row(Border Container, CheckBox Check, ComboBox Combo);

    private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a row per control, keypad keys on the left and everything else on the right.
    /// The order comes from PadLayout, so the lists follow the device rather than a second
    /// ordering that could drift away from it.
    /// </summary>
    private void BuildAssignRows()
    {
        PanelLeft.Children.Clear();
        PanelRight.Children.Clear();
        _rows.Clear();

        foreach (var button in PadLayout.Buttons)
        {
            var check = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                ToolTip = Strings.Get("EnabledTooltip"),
            };

            var name = new TextBlock
            {
                Text = LabelOf(button),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 84,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var combo = new ComboBox
            {
                Width = 172,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = button,

                // The row sets SelectedItem itself, so the collection view must not also
                // get a say. Left at its default a ComboBox synchronizes its selection with
                // the view's CurrentItem whenever the source is a collection view - and a
                // view resets CurrentItem to its first entry on an internal refresh, which
                // drags the selection back to "Controller default" and raises a real
                // SelectionChanged for it. That writes Passthrough over whatever was bound,
                // without anyone having touched the dropdown.
                IsSynchronizedWithCurrentItem = false,
            };
            combo.GroupStyle.Add((GroupStyle)FindResource("ChoiceGroupStyle"));

            combo.SelectionChanged += RowChoiceChanged;

            // Opening a row's dropdown is also how you select that control, so the strip
            // below and the highlight on the artwork follow without a separate click.
            combo.DropDownOpened += (_, _) => Select(button);

            var grid = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(check, Dock.Left);
            DockPanel.SetDock(name, Dock.Left);
            grid.Children.Add(check);
            grid.Children.Add(name);
            grid.Children.Add(combo);

            // Every row is outlined, not just the selected one. An outline that appears from
            // nothing moves the rows below it by a pixel and reads as a flicker; an outline
            // that is always there and only changes color does not. It also makes the list
            // legible as a list of separate controls rather than as a column of text.
            var container = new Border
            {
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("Line"),
                Background = System.Windows.Media.Brushes.Transparent,
                Child = grid,
                Tag = button,
            };

            container.MouseLeftButtonUp += (_, _) => RowClicked(button);

            // Right-click opens a menu rather than clearing on the spot. See
            // MainWindow.Selection.cs for why that single click had to go.
            container.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                RowRightClicked(button, container);
            };

            check.Checked += (_, _) => CheckChanged(button, true);
            check.Unchecked += (_, _) => CheckChanged(button, false);

            (button.Group == ButtonGroup.Keypad ? PanelLeft : PanelRight).Children.Add(container);
            _rows[button.Id] = new Row(container, check, combo);
        }
    }

    /// <summary>
    /// The choices offered by one row: the shared list, preceded by its own keyboard entries.
    ///
    /// The keyboard part has to be per row because it reports that row's current key - the
    /// dropdown has to read "Ctrl+C", not "Keyboard", or the lists stop saying what the pad
    /// does at a glance. Building it per row is cheap now that the catalog is not in here.
    /// </summary>
    private List<AssignChoice> ChoicesFor(Assignment action, PadInput button)
    {
        var list = new List<AssignChoice>(Choices.Count + 2);
        var group = Strings.Get("GroupKeyboard");

        // The key already bound, shown as itself so the row reads as an assignment rather
        // than as a prompt. Selecting it does nothing: it is already the selection.
        if (action.Kind == ActionKind.Key)
            list.Add(new AssignChoice(group, Describe(action), ActionKind.Key,
                action.ScanCode, action.Extended));

        list.Add(new AssignChoice(group, Strings.Get("ChoicePressKey"), ActionKind.Key, Capture: true));

        // A bound program is named, not described as "launch a program". Added before the
        // shared list so ChoiceFor picks this one up first - the generic entry stays below it
        // and is what reopens the browse dialog.
        if (action.Kind == ActionKind.Launch && !string.IsNullOrWhiteSpace(action.Path))
            list.Add(new AssignChoice(Strings.Get("GroupOther"),
                Strings.Get("ChoiceLaunchNamed", ProgramName(action.Path!)), ActionKind.Launch));

        // Same for typed text: a row reading "Type text" says nothing about which text.
        if (action.Kind == ActionKind.Text && !string.IsNullOrWhiteSpace(action.Text))
            list.Add(new AssignChoice(Strings.Get("GroupOther"),
                Strings.Get("ChoiceTextNamed", Ellipsize(action.Text!, 18)), ActionKind.Text));

        // Same idea for macros, and for the same reason: exactly one entry, the bound one,
        // so a user with forty macros does not get forty rows in every dropdown. Choosing
        // from the library happens in the strip below.
        if (BoundMacro(action) is { } macro)
            list.Add(new AssignChoice(Strings.Get("GroupOther"),
                Strings.Get("ChoiceMacroNamed", macro.Name), ActionKind.Macro, MacroId: macro.Id));

        // "Hold Keymap N" is withheld from the wheel rotations, and only from those.
        //
        // They report one impulse per notch and never a release, so there is no "while held"
        // for a hold to last: the engine has to complete the press itself, and the keymap
        // flicks over and back again too fast to see. Offering a choice whose whole meaning
        // is "until you let go" on the one input that cannot be held is an invitation to a
        // bug report. The wheel CLICK is a real button and keeps it.
        list.AddRange(button.IsMomentary
            ? Choices.Where(c => c.Kind != ActionKind.Mode || c.ModeSwitch != ModeSwitch.Hold)
            : Choices);

        return list;
    }

    /// <summary>Matches a stored assignment to the dropdown entry that represents it.</summary>
    private static AssignChoice? ChoiceFor(Assignment action, List<AssignChoice> Choices) => action.Kind switch
    {
        // The row's own entry, never the capture prompt.
        ActionKind.Key => Choices.FirstOrDefault(c => c.Kind == ActionKind.Key && !c.Capture),

        // A silenced control has no assignment behind it, so it shows the default entry.
        // The unchecked, grayed-out row is what says it is switched off.
        ActionKind.None => Choices.FirstOrDefault(c => c.Kind == ActionKind.Passthrough),

        ActionKind.MouseButton => Choices.FirstOrDefault(c =>
            c.Kind == ActionKind.MouseButton && c.MouseButton == action.MouseButton),

        // There is only one, and everything about it is set in the strip.
        ActionKind.MouseWheel => Choices.FirstOrDefault(c => c.Kind == ActionKind.MouseWheel),

        ActionKind.Mode => Choices.FirstOrDefault(c =>
            c.Kind == ActionKind.Mode && c.ModeSwitch == action.ModeSwitch
            && (c.ModeSwitch == ModeSwitch.Cycle || c.ModeIndex == action.ModeIndex)),

        // The entry naming the bound macro, falling back to the prompt when the macro
        // it pointed at has been deleted.
        ActionKind.Macro => Choices.FirstOrDefault(c => c.Kind == ActionKind.Macro && c.MacroId == action.MacroId)
                            ?? Choices.FirstOrDefault(c => c.Kind == ActionKind.Macro),

        _ => Choices.FirstOrDefault(c => c.Kind == action.Kind),
    };

    /// <summary>Brings every row up to date with the keymap being shown.</summary>
    private void RefreshRows()
    {
        var wasLoading = _loading;
        _loading = true;

        // The controller default is read-only. Its rows still show what each control does -
        // its own key, on every one - but neither the checkbox nor the dropdown can be moved.
        var editable = _profile is not { IsControllerDefault: true };

        foreach (var (id, row) in _rows)
        {
            var action = _profile?.GetBinding(_mode, id) ?? Assignment.Passthrough();
            var assigned = action.Kind is not (ActionKind.None or ActionKind.Passthrough);

            // The checkbox means one thing on every row: this control does something. Unchecked
            // is dead whether or not there is an assignment parked behind it.
            var live = action.Kind != ActionKind.None && (!assigned || action.Enabled);

            // Rebuilt each refresh because the keyboard entry carries this row's own key,
            // which changes as the binding does.
            var items = PadLayout.FindById(id) is { } button
                ? ChoicesFor(action, button)
                : ChoicesFor(action, PadLayout.Buttons[0]);
            var view = new ListCollectionView(items);
            view.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(AssignChoice.Group)));

            row.Combo.ItemsSource = view;
            row.Combo.SelectedItem = ChoiceFor(action, items);

            row.Check.IsChecked = live;
            row.Check.IsEnabled = editable;

            // Switched off means switched off: the dropdown is locked and dimmed rather
            // than inviting a choice that would not take effect. Any parked assignment stays
            // visible in it, so it is clear the setting survived being turned off.
            row.Combo.IsEnabled = live && editable;
            row.Combo.Opacity = live ? 1.0 : 0.45;
        }

        _loading = wasLoading;
        RecolorRows();
    }

    /// <summary>
    /// Repaints the rows without rebuilding them.
    ///
    /// Split out of <see cref="RefreshRows"/> because the two are asked for at very different
    /// rates. Rebuilding a row means composing its choice list and wrapping it in a fresh
    /// collection view, twenty-five times over - fine when a binding changes, far too much
    /// when the reason is that a color moved. Dragging in the color picker repaints the
    /// whole window on every mouse movement, and doing it the expensive way made the picker
    /// stutter while the rows underneath were rebuilt for no reason: none of their content
    /// had changed, only their two brushes.
    /// </summary>
    private void RecolorRows()
    {
        var line = (Brush)FindResource("Line");
        var highlight = (Brush)FindResource("Highlight");
        var hover = (Brush)FindResource("Hover");

        foreach (var (id, row) in _rows)
        {
            // Selection is carried by the outline, in the same color a control lights up when
            // it is pressed - so "the one I am looking at" reads the same whether you are
            // looking at the list or at the drawing. Deliberately not the accent: the accent
            // frames every pane and draws the artwork, so a selection wearing it would be one
            // more accent-colored line among a hundred.
            //
            // The fill is Hover rather than AccentMuted for the same reason: it lifts the row
            // off the pane without adding a third color to the argument.
            var chosen = IsMarked(id);

            row.Container.BorderBrush = chosen ? highlight : line;
            row.Container.Background = chosen ? hover : System.Windows.Media.Brushes.Transparent;
        }
    }

    private void RowChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { Tag: PadInput button } combo) return;
        if (combo.SelectedItem is not AssignChoice choice) return;

        Select(button);

        // Choosing a key is done by pressing it. Nothing is written until the dialog comes
        // back with one, so cancelling leaves the row exactly as it was - the refresh at the
        // end puts the dropdown back to whatever is actually bound.
        if (choice.Capture)
        {
            var capture = new KeyPrompt { Owner = this };

            if (capture.ShowDialog() == true)
            {
                var bound = CurrentAction();
                bound.Kind = ActionKind.Key;
                bound.Enabled = true;
                bound.ScanCode = capture.ScanCode;
                bound.Extended = capture.Extended;
                Commit();
            }

            LoadAction();
            RefreshTiles();
            return;
        }

        var action = CurrentAction();
        action.Kind = choice.Kind;
        action.Enabled = true;

        switch (choice.Kind)
        {
            case ActionKind.Key:
                action.ScanCode = choice.ScanCode;
                action.Extended = choice.Extended;
                break;

            case ActionKind.MouseButton:
                action.MouseButton = choice.MouseButton;
                break;

            case ActionKind.MouseWheel:
                // Whatever was already set stands; a fresh assignment scrolls up once.
                if (action.WheelDelta == 0) action.WheelDelta = 1;
                break;

            case ActionKind.Mode:
                action.ModeSwitch = choice.ModeSwitch;
                action.ModeIndex = choice.ModeIndex;
                break;

            case ActionKind.Macro:
                // Only when the entry names one. Picking "select a macro" leaves the binding
                // pointing nowhere on purpose, and opens the chooser in the strip.
                if (choice.MacroId is not null) action.MacroId = choice.MacroId;
                break;
        }

        Commit();
        LoadAction();
        RefreshTiles();
    }

    /// <summary>
    /// Switches a control on or off.
    ///
    /// Off always means dead - the control sends neither an assignment nor its own signal.
    /// What that takes depends on what is there: an assignment is parked, keeping every
    /// setting for when it comes back, while a control with nothing assigned is silenced
    /// outright. Switching back on undoes whichever was done.
    /// </summary>
    private void CheckChanged(PadInput button, bool enabled)
    {
        if (_loading || _profile is null) return;

        ApplyEnabled(button, enabled);
        Commit();
        RefreshTiles();
    }

    /// <summary>
    /// Switches one control on or off, without saving. Split out so the bulk buttons can
    /// run it across a selection and persist once at the end rather than once per row.
    /// </summary>
    private void ApplyEnabled(PadInput button, bool enabled)
    {
        if (_profile is null) return;

        var action = _profile.GetBinding(_mode, button.Id);
        var assigned = action.Kind is not (ActionKind.None or ActionKind.Passthrough);

        if (assigned)
        {
            action.Enabled = enabled;
        }
        else if (enabled)
        {
            // Back to the controller's own key. The entry is removed rather than set to
            // Passthrough so an untouched control leaves nothing behind in the file.
            _profile.Modes[_mode].Remove(button.Id);
        }
        else
        {
            _profile.SetBinding(_mode, button.Id, Assignment.None());
        }
    }
}
