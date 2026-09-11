using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DejaVu.Core.Model;
using DejaVu.App.Localization;

namespace DejaVu.App;

/// <summary>
/// The macro library and its tab.
///
/// Macros are built here and nowhere else. Binding one is a separate act, done on the assign
/// page, and it stores nothing but a reference - so a macro edited here changes everywhere it
/// is used, and the same sequence on six buttons is one thing rather than six copies that
/// drift apart.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<MacroDefinition> _macros = [];

    /// <summary>The macro being edited on the macros tab. Unrelated to what is bound.</summary>
    private MacroDefinition? _macro;

    private void LoadMacros()
    {
        _macros.Clear();
        foreach (var macro in _config.Macros) _macros.Add(macro);

        LstBoundMacro.ItemsSource = _macros;
        RefreshMacroAvailability();
    }

    /// <summary>
    /// An empty library cannot be bound to, so the chooser says so instead of showing an
    /// empty box that looks broken.
    /// </summary>
    private void RefreshMacroAvailability()
    {
        var any = _macros.Count > 0;
        LstBoundMacro.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        TxtNoMacros.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    // =====================================================================
    //  The library
    // =====================================================================

    private void MacroSelected(object sender, SelectionChangedEventArgs e)
    {
        _macro = LstMacros.SelectedItem as MacroDefinition;

        _loading = true;
        TxtMacroHeading.Text = _macro?.Name ?? Strings.Get("NoMacroSelected");
        _steps.Clear();
        foreach (var step in _macro?.Steps ?? []) _steps.Add(step);
        _loading = false;

        // The steps follow the selection. New is in the left pane and stays live whatever is
        // selected, so an empty library is not a dead end.
        PanelMacroEditor.IsEnabled = _macro is not null;
    }

    /// <summary>
    /// Creates a macro, named before it exists.
    ///
    /// Asking first rather than dropping a "New macro" into the list and pointing at a name
    /// box: the box is gone, and a library of things called New macro 2, New macro 3 was
    /// what that arrangement actually produced.
    /// </summary>
    private void NewMacroClick(object sender, RoutedEventArgs e)
    {
        var name = AskName(Strings.Get("NewMacroQuestion"),
            UniqueMacroName(Strings.Get("DefaultMacroName")),
            n => _macros.Any(m => SameName(m.Name, n)));

        if (name is null) return;

        var macro = new MacroDefinition { Name = name };
        _macros.Add(macro);
        CommitMacros();
        LstMacros.SelectedItem = macro;
        LstMacros.ScrollIntoView(macro);
    }

    private void DuplicateMacro()
    {
        if (_macro is null) return;

        var copy = _macro.Clone();
        copy.Name = UniqueMacroName(Strings.Get("CopyOf", _macro.Name));
        _macros.Add(copy);
        CommitMacros();
        LstMacros.SelectedItem = copy;
        LstMacros.ScrollIntoView(copy);
    }

    /// <summary>
    /// Deletes a macro, after saying how many bindings will stop working.
    ///
    /// The bindings are left pointing at the missing macro rather than being hunted down and
    /// cleared: a binding that silently reverted to sending its own key would be a nastier
    /// surprise mid-game than one that does nothing, and re-creating a macro with the same
    /// name will not revive them either way - the reference is by id.
    /// </summary>
    private void DeleteMacro()
    {
        if (_macro is null) return;

        var uses = CountUses(_macro.Id);
        var question = uses == 0
            ? Strings.Get("ConfirmDeleteMacro", _macro.Name)
            : Strings.Get("ConfirmDeleteMacroInUse", _macro.Name, uses);

        if (MessageBox.Show(question, "n52 DejaVu", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        _macros.Remove(_macro);
        CommitMacros();

        LstMacros.SelectedItem = _macros.FirstOrDefault();
        RefreshTiles();
    }

    /// <summary>How many bindings, across every profile and keymap, play this macro.</summary>
    private int CountUses(string id) => _config.Profiles
        .SelectMany(p => p.Modes.SelectMany(m => m.Values))
        .Count(a => a.Kind == ActionKind.Macro && a.MacroId == id);

    private string UniqueMacroName(string wanted)
    {
        // Compared the way the rename prompt compares them, or New would seed a name the
        // prompt then refuses as a duplicate.
        if (_macros.All(m => !SameName(m.Name, wanted))) return wanted;

        for (var n = 2; ; n++)
        {
            var candidate = $"{wanted} {n}";
            if (_macros.All(m => !SameName(m.Name, candidate))) return candidate;
        }
    }

    /// <summary>
    /// Writes the library back and saves.
    ///
    /// Separate from <see cref="Commit"/> because the library belongs to the configuration
    /// rather than to whichever binding happens to be selected - editing a macro must not
    /// depend on a control being chosen on another tab.
    /// </summary>
    private void CommitMacros()
    {
        if (_loading) return;

        if (_macro is not null) _macro.Steps = [.. _steps];
        _config.Macros = [.. _macros];

        ReportSave(_store.Save(_config));
        _accessor.Replace(_config);
        RefreshMacroAvailability();
    }

    // =====================================================================
    //  Binding one
    // =====================================================================

    private void BoundMacroChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LstBoundMacro.SelectedItem is not MacroDefinition macro) return;

        var action = CurrentAction();
        if (action.MacroId == macro.Id) return;

        action.MacroId = macro.Id;
        Commit();
        RefreshTiles();
    }

    /// <summary>The macro a binding plays, if it still exists.</summary>
    private MacroDefinition? BoundMacro(Assignment action)
        => action.Kind == ActionKind.Macro ? _config.FindMacro(action.MacroId) : null;
}
