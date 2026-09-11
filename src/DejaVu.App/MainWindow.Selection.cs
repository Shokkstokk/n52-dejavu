using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DejaVu.Core.Model;
using DejaVu.App.Localization;

namespace DejaVu.App;

/// <summary>
/// Which controls are selected, and what can be done to several of them at once.
///
/// There is one notion of selection here, not two. A properties panel cannot describe five
/// controls, so rather than run an "editing this one" state alongside a "marked these five"
/// state - which would need a second highlight color and two models to keep in step - the
/// selection is simply a set, and the settings strip shows the editor when that set holds
/// exactly one. Two or more turns the same strip into the bulk pane. This is how every
/// properties panel on Windows behaves and it costs nothing to match.
///
/// <para>Right-click no longer clears a row on its own. One stray click while bringing the
/// window to the front used to destroy an assignment and write the loss to disk, with
/// nothing on screen to say which one had gone - unrecoverable in the worst way, because
/// you cannot restore what you never saw disappear. It opens a menu now, and the bulk pane
/// names every control it is about to act on, which is what makes a confirmation dialog
/// unnecessary rather than merely unpopular.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>The selection, in the order the rows were added to it.</summary>
    private readonly List<PadInput> _marked = [];

    private readonly HashSet<string> _markedIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a Shift range measures from. Moved by every click that is not one.</summary>
    private PadInput? _anchor;

    /// <summary>
    /// The control being edited: the selection when it holds exactly one, and nothing
    /// otherwise. Everything that edits a single assignment reads this, so a multiple
    /// selection cannot be half-edited by a path that was written before it existed.
    /// </summary>
    private PadInput? Selected => _marked.Count == 1 ? _marked[0] : null;

    private bool IsMarked(string id) => _markedIds.Contains(id);

    /// <summary>
    /// The rows in the order they are read, left list then right.
    ///
    /// Shift ranges need an ordering, and this is the only one the user can see. Layout
    /// order alone would not do: the two panels interleave in it, so a range would take in
    /// rows from the other column that were never between the endpoints on screen.
    /// </summary>
    private static readonly string[] RowOrder =
    [
        .. PadLayout.Buttons.Where(b => b.Group == ButtonGroup.Keypad).Select(b => b.Id),
        .. PadLayout.Buttons.Where(b => b.Group != ButtonGroup.Keypad).Select(b => b.Id),
    ];

    private static string NameOf(PadInput button) => button.Group == ButtonGroup.Keypad
        ? Strings.Get("KeyNumber", button.Label)
        : LabelOf(button);

    // =====================================================================
    //  Clicking
    // =====================================================================

    /// <summary>
    /// A left-click on a row, with whatever modifiers were held. File Explorer's rules:
    /// plain replaces, Ctrl adds or removes one, Shift takes the range from the anchor.
    /// </summary>
    private void RowClicked(PadInput button)
    {
        var modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Shift) && _anchor is not null)
        {
            // The anchor stays where it was, so dragging the far end of a range around with
            // repeated Shift-clicks grows and shrinks it rather than walking it up the list.
            SetSelection(Range(_anchor, button), _anchor);
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            var next = new List<PadInput>(_marked);

            if (IsMarked(button.Id)) next.RemoveAll(b => b.Id == button.Id);
            else next.Add(button);

            SetSelection(next, button);
            return;
        }

        Select(button);
    }

    /// <summary>
    /// A right-click on a row. Explorer's rule again: inside the selection the menu applies
    /// to all of it, outside it the selection collapses to the row you actually clicked.
    /// That second half is what stops "I meant to reset one" becoming twelve.
    /// </summary>
    private void RowRightClicked(PadInput button, Border container)
    {
        if (!IsMarked(button.Id)) Select(button);

        // Every item on this menu changes a binding, and the controller default has none to
        // change. No menu at all rather than a menu of dead items - the strip above already
        // says why, and it is on screen the moment a row is clicked.
        if (_profile is { IsControllerDefault: true }) return;

        container.ContextMenu = BuildRowMenu();
        container.ContextMenu.PlacementTarget = container;
        container.ContextMenu.IsOpen = true;
    }

    /// <summary>The controls between two rows inclusive, in reading order.</summary>
    private static IEnumerable<PadInput> Range(PadInput from, PadInput to)
    {
        var first = Array.IndexOf(RowOrder, from.Id);
        var last = Array.IndexOf(RowOrder, to.Id);
        if (first < 0 || last < 0) return [to];

        if (first > last) (first, last) = (last, first);

        return RowOrder[first..(last + 1)]
            .Select(PadLayout.FindById)
            .Where(b => b is not null)
            .Select(b => b!);
    }

    /// <summary>Replaces the selection outright and repaints everything that shows it.</summary>
    private void SetSelection(IEnumerable<PadInput> buttons, PadInput? anchor)
    {
        _marked.Clear();
        _markedIds.Clear();

        foreach (var button in buttons)
            if (_markedIds.Add(button.Id))
                _marked.Add(button);

        _anchor = anchor ?? _marked.LastOrDefault();

        ShowSelection();
    }

    /// <summary>
    /// Puts the settings strip into the state the current selection calls for: the editor
    /// for one control, the bulk pane for several, and shut for none.
    /// </summary>
    private void ShowSelection()
    {
        // The controller default is read-only, so the strip explains itself instead of
        // offering an editor whose every control would be dead. Controls can still be
        // selected and read: seeing what a clean pad does is the point of the profile.
        if (_profile is { IsControllerDefault: true })
        {
            PanelBulk.Visibility = Visibility.Collapsed;
            PanelEditor.Visibility = Visibility.Collapsed;
            TxtLockedProfile.Visibility = Visibility.Visible;
            TxtSettingsPrefix.Visibility = Visibility.Visible;

            TxtSelectedInput.Text = Selected is { } only
                ? NameOf(only)
                : Strings.Get("NoInputSelected");
            TxtSignal.Text = string.Empty;

            PanelDetail.Visibility = Visibility.Visible;
            RefreshTiles();
            return;
        }

        TxtLockedProfile.Visibility = Visibility.Collapsed;

        var several = _marked.Count > 1;

        PanelBulk.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        PanelEditor.Visibility = several ? Visibility.Collapsed : Visibility.Visible;
        TxtSettingsPrefix.Visibility = several ? Visibility.Collapsed : Visibility.Visible;

        if (several)
        {
            TxtSelectedInput.Text = Strings.Get("SelectionCount", _marked.Count);
            TxtSignal.Text = string.Empty;

            // Named, not just counted. Reading what is about to be reset before pressing the
            // button is the whole reason this can be done without a confirmation dialog.
            TxtBulkNames.Text = string.Join(", ", _marked.Select(NameOf));

            PanelDetail.Visibility = Visibility.Visible;
        }
        else if (Selected is { } button)
        {
            TxtSelectedInput.Text = NameOf(button);
            TxtSignal.Text = button.IsCombination
                ? Strings.Get("DiagonalLabel", string.Join(" + ", button.Components!.Select(Describe)))
                : Strings.Get("SignalPrefix", button.Signal);

            PanelEditor.IsEnabled = true;

            // Clicking a control on the drawing has to reach its row, which may be scrolled
            // out of sight in a list of fourteen.
            if (_rows.TryGetValue(button.Id, out var row)) row.Container.BringIntoView();

            // Sets the strip's own visibility from the action kind, so this is left to it.
            LoadAction();
        }
        else
        {
            PanelEditor.IsEnabled = false;
            PanelDetail.Visibility = Visibility.Collapsed;
        }

        RefreshTiles();
    }

    // =====================================================================
    //  The menu
    // =====================================================================

    /// <summary>
    /// The same two actions at any selection size, which is the point: a menu that offered
    /// fewer items for several rows than for one would be a rule to learn for no benefit.
    ///
    /// Enable and Disable collapse into one toggling item for a single row, because its
    /// state is known and the other of the pair would be a visible no-op. A mixed selection
    /// has no such answer, so several rows get both spelled out - matching the pane.
    /// </summary>
    private ContextMenu BuildRowMenu()
    {
        var menu = new ContextMenu();
        var count = _marked.Count;

        if (count > 1)
        {
            menu.Items.Add(Item(Strings.Get("MenuEnableMany", count), () => SetSelectionEnabled(true)));
            menu.Items.Add(Item(Strings.Get("MenuDisableMany", count), () => SetSelectionEnabled(false)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Strings.Get("MenuResetMany", count), ResetSelection));
            return menu;
        }

        var live = Selected is not null && IsLive(Selected);
        menu.Items.Add(Item(Strings.Get(live ? "MenuDisable" : "MenuEnable"),
            () => SetSelectionEnabled(!live)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Strings.Get("MenuReset"), ResetSelection));

        // The way out of the safety net. Setting a keymap switch in keymap 1 writes "step to
        // the next keymap" onto that control in the other seven, which is what stops a switch
        // stranding anyone - but clearing it one keymap at a time afterwards is eight trips
        // through this menu, and seven of them in keymaps the user never meant to visit.
        //
        // Offered on the same terms the net is laid: keymap 1, and a keymap action of any
        // kind. Single selection only - "which of these five did it mean" is not a question a
        // destructive menu item should raise.
        if (_mode == 0 && Selected is not null &&
            _profile?.GetBinding(_mode, Selected.Id) is { Kind: ActionKind.Mode })
        {
            menu.Items.Add(Item(Strings.Get("MenuResetEverywhere"), ResetSelectionEverywhere));
        }

        return menu;

        static MenuItem Item(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            return item;
        }
    }

    /// <summary>Whether a control does anything at all, which is what the checkbox reports.</summary>
    private bool IsLive(PadInput button)
    {
        var action = _profile?.GetBinding(_mode, button.Id) ?? Assignment.Passthrough();
        var assigned = action.Kind is not (ActionKind.None or ActionKind.Passthrough);

        return action.Kind != ActionKind.None && (!assigned || action.Enabled);
    }

    // =====================================================================
    //  Acting on the selection
    // =====================================================================

    private void BulkEnableClick(object sender, RoutedEventArgs e) => SetSelectionEnabled(true);

    private void BulkDisableClick(object sender, RoutedEventArgs e) => SetSelectionEnabled(false);

    private void BulkResetClick(object sender, RoutedEventArgs e) => ResetSelection();

    /// <summary>
    /// Switches every selected control on or off. Written once and saved once: routing this
    /// through the per-row handler would persist the configuration twenty-five times over
    /// for one click, and repaint the lists after each.
    /// </summary>
    private void SetSelectionEnabled(bool enabled)
    {
        if (_profile is null || _marked.Count == 0) return;

        foreach (var button in _marked) ApplyEnabled(button, enabled);

        Persist();
        ShowSelection();
    }

    /// <summary>Returns every selected control to what the hardware sends by itself.</summary>
    private void ResetSelection()
    {
        if (_profile is null || _marked.Count == 0) return;

        foreach (var button in _marked)
        {
            _profile.Modes[_mode].Remove(button.Id);
        }

        Persist();
        ShowSelection();
    }

    /// <summary>
    /// Returns the selected control to the hardware's own signal in every keymap at once,
    /// undoing the net that a keymap switch in keymap 1 lays across the other seven.
    /// </summary>
    private void ResetSelectionEverywhere()
    {
        if (_profile is null || Selected is null) return;

        foreach (var map in _profile.Modes) map.Remove(Selected.Id);

        Persist();
        ShowSelection();
    }
}
