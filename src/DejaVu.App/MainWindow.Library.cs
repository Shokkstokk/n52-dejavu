using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using DejaVu.App.Localization;

namespace DejaVu.App;

/// <summary>
/// The gesture the three library pages share: profiles, macros and themes.
///
/// All three are the same shape - a list of named things on the left, and what the selected
/// one is made of on the right - so all three behave the same way. The left pane names the
/// library and offers one button, <b>New</b>. Everything that acts on an entry that already
/// exists lives on that entry's own right-click menu.
///
/// <para><b>Why the name boxes went.</b> Each page used to carry an always-editable Name
/// field wired to TextChanged, so every keystroke renamed the entry and wrote it to disk,
/// with no confirmation and nothing to undo it. A stray character while the window was being
/// brought to the front was enough - the same accident as the right-click that used to clear
/// an assignment, and it is closed the same way: the destructive act now needs a deliberate
/// gesture and its own dialog. Renaming also validates now, which the box never did; it would
/// accept a blank name or a duplicate of another entry's quite happily.</para>
///
/// <para><b>Right-click selects first, then acts.</b> WPF's ListBoxItem takes selection from
/// the left button only, so a menu raised over one row would otherwise act on whichever row
/// was selected before - the worst possible outcome for a menu that can delete. Explorer's
/// rule instead: the row under the pointer becomes the selection, and the menu is about
/// that.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Names are compared the way a person would compare them, not the way a machine would.</summary>
    private static bool SameName(string left, string right)
        => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The list row a mouse event landed on, or null for the empty space below them.</summary>
    private static ListBoxItem? RowUnder(ListBox list, RoutedEventArgs e)
        => e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(list, source) as ListBoxItem
            : null;

    private static MenuItem Entry(string header, Action run)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => run();
        return item;
    }

    private static void Popup(ListBoxItem row, ContextMenu menu)
    {
        row.ContextMenu = menu;
        menu.PlacementTarget = row;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Asks for a name, and asks again rather than failing quietly.
    ///
    /// Two answers cannot be used: nothing at all, which leaves an entry that cannot be
    /// picked out of the list, and a name another entry already has, which leaves two the
    /// user cannot tell apart. Both come back to the same dialog with the reason on it and
    /// what was typed still in the box, because the fix is usually one character.
    /// </summary>
    private string? AskName(string question, string current, Func<string, bool> taken)
    {
        var asked = question;

        while (true)
        {
            var answer = InputDialog.Ask(this, asked, current);
            if (answer is null) return null;

            current = answer;
            var name = answer.Trim();

            if (name.Length == 0) { asked = Strings.Get("NameBlank", question); continue; }
            if (taken(name)) { asked = Strings.Get("NameTaken", question); continue; }

            return name;
        }
    }

    /// <summary>F2 renames, which is what F2 does everywhere else on Windows.</summary>
    private void LibraryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;

        if (ReferenceEquals(sender, LstProfiles)) RenameProfile();
        else if (ReferenceEquals(sender, LstMacros)) RenameMacro();
        else if (ReferenceEquals(sender, LstThemes)) RenameTheme();
        else return;

        e.Handled = true;
    }

    // =====================================================================
    //  Profiles
    // =====================================================================

    private void ProfileListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(LstProfiles, e) is not { } row) return;

        row.IsSelected = true;
        e.Handled = true;

        var locked = _profile?.IsControllerDefault ?? false;

        var menu = new ContextMenu();

        // The controller default gets neither Rename nor Delete - that is what makes it the
        // one profile whose contents are known without looking. Duplicate stays, and is the
        // intended way to start a layout from a clean pad.
        if (!locked) menu.Items.Add(Entry(Strings.Get("MenuRenameEntry"), RenameProfile));
        menu.Items.Add(Entry(Strings.Get("MenuDuplicateEntry"), DuplicateProfile));

        // Offered on every profile including the locked one - "leave the pad alone unless a
        // game claims it" is a reasonable thing to want, and it is what a fresh install does.
        if (_profile is { } chosen && _config.DefaultProfileId != chosen.Id)
            menu.Items.Add(Entry(Strings.Get("MenuUseAsFallback"), UseProfileAsFallback));

        if (!locked)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry(Strings.Get("MenuDeleteEntry"), DeleteProfile));
        }

        Popup(row, menu);
    }

    /// <summary>Points the engine's "nothing matched" fallback at the selected profile.</summary>
    private void UseProfileAsFallback()
    {
        if (_profile is not { } profile) return;

        _config.DefaultProfileId = profile.Id;
        RefreshFallbackMarker();
        Persist();
    }

    /// <summary>Mirrors <see cref="Configuration.DefaultProfileId"/> onto the rows that show it.</summary>
    private void RefreshFallbackMarker()
    {
        foreach (var profile in _profiles) profile.IsFallback = profile.Id == _config.DefaultProfileId;
    }

    private void RenameProfile()
    {
        // Guarded here as well as on the menu, because F2 does not go through the menu.
        if (_profile is not { IsControllerDefault: false } profile) return;

        var name = AskName(Strings.Get("RenameProfileQuestion"), profile.Name,
            n => _profiles.Any(p => p != profile && SameName(p.Name, n)));

        if (name is null) return;

        // Profile raises its own name-changed notification, so the list and the toolbar box
        // both follow without being told.
        profile.Name = name;
        TxtProfileHeading.Text = name;
        Persist();
    }

    // =====================================================================
    //  Macros
    // =====================================================================

    private void MacroListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(LstMacros, e) is not { } row) return;

        row.IsSelected = true;
        e.Handled = true;

        var menu = new ContextMenu();
        menu.Items.Add(Entry(Strings.Get("MenuRenameEntry"), RenameMacro));
        menu.Items.Add(Entry(Strings.Get("MenuDuplicateEntry"), DuplicateMacro));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry(Strings.Get("MenuDeleteEntry"), DeleteMacro));

        Popup(row, menu);
    }

    private void RenameMacro()
    {
        if (_macro is not { } macro) return;

        var name = AskName(Strings.Get("RenameMacroQuestion"), macro.Name,
            n => _macros.Any(m => m != macro && SameName(m.Name, n)));

        if (name is null) return;

        macro.Name = name;
        TxtMacroHeading.Text = name;
        CommitMacros();

        // Rows show the bound macro by name, so a rename has to reach them.
        RefreshTiles();
    }

    // =====================================================================
    //  Themes
    // =====================================================================

    private void ThemeListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(LstThemes, e) is not { } row) return;

        row.IsSelected = true;
        e.Handled = true;

        var menu = new ContextMenu();

        // A preset is compiled into the assembly: it cannot be renamed and it cannot be
        // deleted, and duplicating it is the only way to get at its colors. That used to be
        // signaled by a grayed-out name box, which said nothing about why.
        if (row.Content is ThemeRow { BuiltIn: false })
        {
            menu.Items.Add(Entry(Strings.Get("MenuRenameEntry"), RenameTheme));
            menu.Items.Add(Entry(Strings.Get("MenuDuplicateEntry"), DuplicateTheme));
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry(Strings.Get("MenuDeleteEntry"), DeleteTheme));
        }
        else
        {
            menu.Items.Add(Entry(Strings.Get("MenuDuplicateEntry"), DuplicateTheme));
        }

        Popup(row, menu);
    }

    private void RenameTheme()
    {
        if (LstThemes.SelectedItem is not ThemeRow { Custom: { } custom } row) return;

        var name = AskName(Strings.Get("RenameThemeQuestion"), row.Name,
            n => _themes.Any(t => t != row && SameName(t.Name, n)));

        if (name is null) return;

        custom.Name = name;
        row.Name = name;
        TxtThemeHeading.Text = name;

        // ThemeRow is a plain object rather than an observable one - the marker it carries
        // changes once in a session - so the list has to be told.
        LstThemes.Items.Refresh();
        Persist();
    }
}
