using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DejaVu.App.Localization;
using DejaVu.Core.Model;

namespace DejaVu.App;

/// <summary>
/// The Appearance page: a library of themes, built the same way the macro library is.
///
/// <para><b>Selecting previews, Apply keeps.</b> Every other list in this application acts
/// immediately, and this one deliberately does not. A theme has to be judged at full size on
/// the real window - a swatch tells you nothing about how a shell color sits behind fourteen
/// assignment rows - so the list previews freely and only Apply writes the choice down.
/// Leaving the page without applying puts back whatever was in use.</para>
///
/// <para><b>Presets cannot be edited.</b> They are compiled into the assembly, so there is
/// nowhere to write a change to. Duplicate turns one into an ordinary saved theme, which is
/// also the easiest way to build one: start from the palette closest to what you want.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// One row in the theme list. Wraps rather than binds to <see cref="CustomTheme"/>
    /// directly because presets are not CustomThemes at all, and the list has to hold both.
    /// </summary>
    private sealed class ThemeRow(string id, string name, bool builtIn, CustomTheme? custom)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public bool BuiltIn { get; } = builtIn;
        public CustomTheme? Custom { get; } = custom;

        /// <summary>Marks the theme actually in use, as distinct from the one being previewed.</summary>
        public bool IsApplied { get; set; }
    }

    private readonly ObservableCollection<ThemeRow> _themes = [];

    /// <summary>
    /// The colors a theme chooses. Text is last because it is the odd one: leaving it alone
    /// derives it from Shell, which is what guarantees a readable theme, and setting it is an
    /// override of that guarantee rather than one more required choice.
    /// </summary>
    private enum ThemeSlot { Shell, Pane, Accent, Highlight, Live, Text }

    private ThemeSlot _slot = ThemeSlot.Accent;
    private readonly Dictionary<ThemeSlot, Border> _targets = [];

    /// <summary>
    /// Builds the three slot buttons and wires the picker to whichever is chosen.
    ///
    /// One picker rather than three, following the same model as a paint program: pick the
    /// slot, then pick the color. It keeps all three current colors visible side by side,
    /// which is the comparison that decides them - an accent is chosen against its shell, and
    /// a highlight is chosen to be unmistakable against both.
    /// </summary>
    private void BuildThemeTargets()
    {
        BuildTextChoices();

        foreach (var slot in Enum.GetValues<ThemeSlot>())
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("Line"),
                Margin = new Thickness(0, 0, 8, 0),
            };

            var button = new Border
            {
                Padding = new Thickness(9, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 6),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        swatch,
                        new TextBlock
                        {
                            Text = Strings.Get($"Slot{slot}"),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                Tag = swatch,
            };

            // SetResourceReference, not FindResource. FindResource hands back the brush that
            // happens to be current and the assignment copies it, so these would keep the
            // colors of whichever theme was loaded when the window was built - which is
            // exactly what happened: edit a theme, switch to another, and the slot buttons
            // still wore the old one. A resource reference is the code-behind equivalent of
            // DynamicResource and follows the swap on its own.
            button.SetResourceReference(BackgroundProperty, "Background");
            swatch.SetResourceReference(BorderBrushProperty, "Line");

            button.ToolTip = Tip(Strings.Get($"Slot{slot}Tip"));

            var chosen = slot;
            button.MouseLeftButtonUp += (_, _) => ChooseSlot(chosen);

            _targets[slot] = button;
            PanelTargets.Children.Add(button);
        }

        // Every movement of the picker is a color: the caller wants them all, because the
        // window behind repaints from each one and that is the only way to judge a color.
        Picker.Changed += PickedColor;
    }

    /// <summary>
    /// A tooltip that wraps.
    ///
    /// A plain string handed to the ToolTip property does not: it becomes a single line, and
    /// the style's MaxWidth clips it rather than folding it. Anything longer than a few words
    /// has to bring its own TextBlock, which is what the markup does by hand elsewhere in this
    /// window - and these are all several sentences.
    /// </summary>
    private static ToolTip Tip(string text) => new()
    {
        MaxWidth = 360,
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
    };

    private void ChooseSlot(ThemeSlot slot)
    {
        _slot = slot;
        ShowSlots();
    }

    // =====================================================================
    //  Size
    // =====================================================================

    private void BuildTextChoices()
    {
        // Whole numbers only, and only the ones the layout survives. Typography clamps to the
        // same bounds, so a hand-edited configuration cannot get past this either. Measured
        // rather than guessed: at 16 the longest built-in dropdown entry is 138.6 units in a
        // box with about 144 usable, and the longest control name 79.5 in 84.
        CboFontSize.ItemsSource = Enumerable
            .Range((int)Typography.MinimumSize, (int)(Typography.MaximumSize - Typography.MinimumSize) + 1)
            .ToArray();
    }

    /// <summary>Puts the dropdown on the size the application is actually using.</summary>
    private void ShowTextChoices()
    {
        var size = _config.FontSize > 0 ? _config.FontSize : Typography.SystemSize;

        CboFontSize.SelectedItem = (int)Math.Clamp(
            Math.Round(size), Typography.MinimumSize, Typography.MaximumSize);
    }

    /// <summary>
    /// Changes the text size for the whole application, immediately and permanently.
    ///
    /// Not part of the selected theme, and so not subject to Apply or to abandoning a
    /// preview. It sits on this page because that is where people look for it, but a size
    /// that belonged to a theme meant browsing the library resized the window on every
    /// click: the presets carry no size of their own, so previewing one snapped the text
    /// back to the system default and relaid out the whole page.
    /// </summary>
    private void FontSizeChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CboFontSize.SelectedItem is not int size) return;

        _config.FontSize = size;
        Typography.Apply(size);
        Persist();
    }

    /// <summary>
    /// Repaints the six slot buttons and points the picker at the chosen one.
    ///
    /// Called on every theme change as well as on every click, because the ring marking the
    /// chosen slot is set from the palette and cannot follow a swap by itself.
    /// </summary>
    private void ShowSlots()
    {
        if (LstThemes.SelectedItem is not ThemeRow { Custom: { } custom }) return;

        foreach (var (slot, button) in _targets)
        {
            if (button.Tag is Border swatch)
                swatch.Background = new SolidColorBrush(ColorOf(custom, slot));

            button.BorderBrush = (Brush)FindResource(slot == _slot ? "Highlight" : "Line");
        }

        TxtTargetHelp.Text = Strings.Get($"Slot{_slot}Help");

        Picker.Color = ColorOf(custom, _slot);
    }

    private static Color ColorOf(CustomTheme theme, ThemeSlot slot) => (slot switch
    {
        ThemeSlot.Shell => Palette.Parse(theme.Shell),
        ThemeSlot.Pane => Palette.Parse(theme.Pane),
        ThemeSlot.Accent => Palette.Parse(theme.Accent),
        ThemeSlot.Highlight => Palette.Parse(theme.Highlight),
        ThemeSlot.Live => Palette.Parse(theme.Live),

        // Empty means derived, so the swatch and the picker show what the derivation
        // produced rather than black - which is what the theme is actually using.
        _ => Palette.Parse(theme.Text),
    }) ?? Colors.Black;

    /// <summary>
    /// Writes a picked color into the chosen slot and shows it immediately.
    ///
    /// Immediately, and on every movement of the picker, because a color cannot be judged
    /// from a swatch - a shell is decided by what it does behind fourteen assignment rows,
    /// and an accent by whether the device drawing still reads. Saving on each one is cheap:
    /// the configuration is small and the write is a few kilobytes of JSON.
    /// </summary>
    private void PickedColor(Color color)
    {
        if (_loading || LstThemes.SelectedItem is not ThemeRow { Custom: { } custom }) return;

        switch (_slot)
        {
            case ThemeSlot.Shell: custom.Shell = Palette.ToHex(color); break;
            case ThemeSlot.Pane: custom.Pane = Palette.ToHex(color); break;
            case ThemeSlot.Accent: custom.Accent = Palette.ToHex(color); break;
            case ThemeSlot.Highlight: custom.Highlight = Palette.ToHex(color); break;
            case ThemeSlot.Live: custom.Live = Palette.ToHex(color); break;
            default: custom.Text = Palette.ToHex(color); break;
        }

        Theme.LoadCustom(custom);
        Persist();

        if (_targets[_slot].Tag is Border swatch)
            swatch.Background = new SolidColorBrush(color);
    }

    /// <summary>
    /// Rebuilds the list: the presets first, then the user's own, with the one actually in
    /// use marked.
    /// </summary>
    private void LoadThemes()
    {
        var wasLoading = _loading;
        _loading = true;

        var previous = (LstThemes.SelectedItem as ThemeRow)?.Id;

        _themes.Clear();

        foreach (var preset in Theme.Available)
            _themes.Add(new ThemeRow(preset.Id, preset.Name, builtIn: true, custom: null));

        foreach (var saved in _config.Themes)
            _themes.Add(new ThemeRow(saved.Id, saved.Name, builtIn: false, custom: saved));

        // The applied theme, not the previewed one. Falls back to the default when the stored
        // id names nothing, which is what Theme.Apply would do anyway.
        var applied = _themes.FirstOrDefault(t => t.Id == _config.Theme)
                      ?? _themes.FirstOrDefault(t => t.Id == Theme.Default);

        foreach (var row in _themes) row.IsApplied = row == applied;

        LstThemes.SelectedItem = _themes.FirstOrDefault(t => t.Id == previous)
                                 ?? _themes.FirstOrDefault(t => t.Id == Theme.Current)
                                 ?? applied;

        _loading = wasLoading;
        ShowTheme();
    }

    /// <summary>Fills the right-hand pane from the selected row.</summary>
    private void ShowTheme()
    {
        var wasLoading = _loading;
        _loading = true;

        var row = LstThemes.SelectedItem as ThemeRow;
        var editable = row is { BuiltIn: false, Custom: not null };

        TxtThemeHeading.Text = row?.Name ?? Strings.Get("NoThemeSelected");

        // Outside the editor panel and outside the preview: the size belongs to the
        // application, so it stays live even while a preset - which cannot be edited at all -
        // is selected.
        ShowTextChoices();

        PanelThemeEditor.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        TxtPresetNote.Visibility = row is { BuiltIn: true } ? Visibility.Visible : Visibility.Collapsed;

        // Applying what is already applied does nothing, so the button goes quiet - and the
        // line beneath says why, because a disabled Apply next to a color you have just
        // changed reads as "that change cannot be saved". It can: color edits are written as
        // they are made. Apply answers a different question - which theme the application uses
        // - and there is nothing to answer when the one on screen is already it.
        BtnApplyTheme.IsEnabled = row is { IsApplied: false };

        TxtApplyState.Text = row is null ? ""
            : row.IsApplied ? Strings.Get("ThemeIsInUse")
            : Strings.Get("ThemePreviewing");

        TxtApplyState.Foreground = (Brush)FindResource(
            row is { IsApplied: true } ? "Active" : "TextDim");

        if (editable) ShowSlots();

        _loading = wasLoading;
    }

    /// <summary>Previews the selected theme. Nothing is written until Apply.</summary>
    private void ThemeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        if (LstThemes.SelectedItem is ThemeRow row)
        {
            if (row.Custom is { } custom) Theme.LoadCustom(custom);
            else Theme.Load(row.Id);
        }

        ShowTheme();
    }

    /// <summary>Keeps the previewed theme: the only place the choice is written down.</summary>
    private void ApplyThemeClick(object sender, RoutedEventArgs e)
    {
        if (LstThemes.SelectedItem is not ThemeRow row) return;

        _config.Theme = row.Id;
        foreach (var other in _themes) other.IsApplied = other == row;

        Persist();

        // The marker is on the row rather than in the template's binding path, so the list has
        // to be told. Cheap enough at this size, and simpler than making every row observable
        // for one flag that changes once.
        LstThemes.Items.Refresh();

        ShowTheme();
    }

    private void NewThemeClick(object sender, RoutedEventArgs e)
    {
        var name = AskName(Strings.Get("NewThemeQuestion"),
            UniqueThemeName(Strings.Get("DefaultThemeName")),
            n => _themes.Any(t => SameName(t.Name, n)));

        if (name is null) return;

        // Seeded from the theme being previewed rather than from nothing: the usual reason to
        // press New is that the current one is nearly right.
        var seed = (LstThemes.SelectedItem as ThemeRow)?.Custom;

        var created = new CustomTheme
        {
            Name = name,
            Shell = seed?.Shell ?? Palette.ToHex(((SolidColorBrush)FindResource("Background")).Color),
            Pane = seed?.Pane ?? Palette.ToHex(((SolidColorBrush)FindResource("Surface")).Color),
            Accent = seed?.Accent ?? Palette.ToHex(((SolidColorBrush)FindResource("Accent")).Color),
            Highlight = seed?.Highlight ?? Palette.ToHex(((SolidColorBrush)FindResource("Highlight")).Color),
            Live = seed?.Live ?? Palette.ToHex(((SolidColorBrush)FindResource("Live")).Color),

            // Text follows the same rule as the colors: start from what is on screen, which
            // for a preset is the color its shell derived.
            Text = seed?.Text ?? Palette.ToHex(((SolidColorBrush)FindResource("Text")).Color),
        };

        _config.Themes.Add(created);
        Persist();
        LoadThemes();

        SelectTheme(created.Id);
    }

    private void DuplicateTheme()
    {
        if (LstThemes.SelectedItem is not ThemeRow row) return;

        // A preset has no CustomTheme behind it, so duplicating one means reading the colors
        // back out of the palette it merged. That is also the only way a preset can be
        // customized at all, which is why Duplicate is offered for presets and Delete is not.
        var copy = row.Custom?.Clone() ?? PresetAsCustom(row);

        copy.Name = UniqueThemeName(Strings.Get("ThemeCopyName", row.Name));

        _config.Themes.Add(copy);
        Persist();
        LoadThemes();

        SelectTheme(copy.Id);
    }

    /// <summary>
    /// Turns a preset into an editable copy by reading the palette it has merged.
    ///
    /// Exact, and simply so: a theme is now its background, its accent and its highlight, and
    /// all three are brushes sitting in the resources right now. Duplicating a preset therefore
    /// reproduces it rather than approximating it - which matters, because duplicating is the
    /// only way to get at a preset's colors and the usual way to start a theme.
    /// </summary>
    private CustomTheme PresetAsCustom(ThemeRow row) => new()
    {
        Name = row.Name,

        // Read back the same way the colors are: whatever is in the resources right now is
        // what this preset produced, so the copy starts as the thing it was copied from -
        // text included, which for a preset is the color its shell derived.
        Text = Palette.ToHex(((SolidColorBrush)FindResource("Text")).Color),

        Shell = Palette.ToHex(((SolidColorBrush)FindResource("Background")).Color),
        Pane = Palette.ToHex(((SolidColorBrush)FindResource("Surface")).Color),
        Accent = Palette.ToHex(((SolidColorBrush)FindResource("Accent")).Color),
        Highlight = Palette.ToHex(((SolidColorBrush)FindResource("Highlight")).Color),
        Live = Palette.ToHex(((SolidColorBrush)FindResource("Live")).Color),
    };

    private void DeleteTheme()
    {
        if (LstThemes.SelectedItem is not ThemeRow { Custom: { } custom } row) return;

        var question = row.IsApplied
            ? Strings.Get("DeleteAppliedTheme", row.Name)
            : Strings.Get("DeleteThemeQuestion", row.Name);

        if (MessageBox.Show(question, Strings.Get("AppTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _config.Themes.Remove(custom);

        // Deleting the theme in use has to leave something applied, or the next launch would
        // fall back silently and the window would change color for no visible reason.
        if (row.IsApplied)
        {
            _config.Theme = Theme.Default;
            Theme.Load(Theme.Default);
        }

        Persist();
        LoadThemes();
    }

    private void SelectTheme(string id)
    {
        LstThemes.SelectedItem = _themes.FirstOrDefault(t => t.Id == id);
        LstThemes.ScrollIntoView(LstThemes.SelectedItem);
    }

    /// <summary>Keeps names distinct, so the list cannot fill with identical entries.</summary>
    private string UniqueThemeName(string wanted)
    {
        if (_themes.All(t => !string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
            && _config.Themes.All(t => !string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            return wanted;

        for (var n = 2; ; n++)
        {
            var candidate = $"{wanted} {n}";

            if (_themes.All(t => !string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase))
                && _config.Themes.All(t => !string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    /// <summary>
    /// Puts back the applied theme when the user leaves the page having only previewed.
    ///
    /// Without this, browsing the list would quietly change the application's appearance for
    /// good - the preview would become the setting simply because nobody pressed Apply, which
    /// makes Apply meaningless.
    /// </summary>
    private void AbandonThemePreview()
    {
        if (Theme.Current == _config.Theme) return;

        Theme.Apply(_config.Theme, _config.Themes);
        LoadThemes();
    }
}
