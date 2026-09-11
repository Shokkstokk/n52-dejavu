using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using DejaVu.App.Localization;

namespace DejaVu.App;

/// <summary>
/// Asks for one value and gets out of the way.
///
/// Used wherever the answer is simply a string and a dropdown will not do: naming a profile,
/// a macro or a theme, and the two macro steps that carry a value of their own - a wait in
/// milliseconds, and a line of text to type. Anything richer than that has earned its own
/// window; anything simpler is a menu.
/// </summary>
public static class InputDialog
{
    private static readonly Thickness Edge = new(18);

    /// <summary>
    /// Puts the question up and waits.
    ///
    /// <para>Null means the prompt was abandoned, and that is deliberately not the same
    /// answer as an empty string: clearing a text step to nothing is a decision, closing the
    /// window is a refusal to make one, and a caller that cannot tell them apart will happily
    /// wipe the value someone was only looking at.</para>
    /// </summary>
    /// <param name="owner">Centered on this, and modal to it.</param>
    /// <param name="question">Shown above the box. Wraps to as many lines as it needs.</param>
    /// <param name="starting">Pre-filled and pre-selected, ready to be typed over.</param>
    /// <param name="lines">True for an answer that may run to several lines.</param>
    public static string? Ask(Window owner, string question, string starting, bool lines = false)
    {
        var prompt = new TextBlock
        {
            Text = question,
            TextWrapping = TextWrapping.Wrap,
        };

        var answer = new TextBox
        {
            Text = starting,
            Margin = new Thickness(0, 10, 0, 14),
            AcceptsReturn = lines,
            TextWrapping = lines ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = lines ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            MinHeight = lines ? 96 : 0,
        };

        // Enter means "done" for a one-line answer, and has to mean "new line" for the other -
        // a text step that could never contain a line break would be a strange limitation to
        // impose from a dialog box.
        var keep = new Button
        {
            Content = Strings.Get("OkLabel"),
            MinWidth = 90,
            IsDefault = !lines,
        };

        var abandon = new Button
        {
            Content = Strings.Get("CancelLabel"),
            MinWidth = 90,
            Margin = new Thickness(0, 0, 6, 0),
            IsCancel = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { abandon, keep },
        };

        // Three rows rather than a stack: the question and the buttons take the height they
        // need and the box takes the rest, which is what lets a multi-line prompt grow
        // without the buttons drifting off the bottom.
        var body = new Grid { Margin = Edge };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(prompt, 0);
        Grid.SetRow(answer, 1);
        Grid.SetRow(buttons, 2);
        body.Children.Add(prompt);
        body.Children.Add(answer);
        body.Children.Add(buttons);

        var window = new Window
        {
            Title = Strings.Get("AppTitle"),
            Owner = owner,
            Content = body,
            // Wide when it is holding prose, narrow when it is holding a number. The same
            // prompt asks for a name, a delay in milliseconds and the text an assignment
            // types, and one width cannot suit all three: 720 for "gg wp everyone" is
            // comfortable, and for "100" it is faintly ridiculous.
            Width = lines ? 720 : 360,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.Resources["Background"],
        };

        // A window of its own inherits none of the editor's type, so it has to ask. References
        // rather than copies, or it would keep whichever theme happened to be loaded when the
        // dialog was built.
        window.SetResourceReference(Control.FontFamilyProperty, "AppFontFamily");
        window.SetResourceReference(Control.FontSizeProperty, "FontBody");

        keep.Click += (_, _) => window.DialogResult = true;

        // Selected, not merely focused. The caller hands over the current value and the usual
        // intention is to replace it, so typing should overwrite rather than append.
        window.Loaded += (_, _) =>
        {
            answer.Focus();
            answer.SelectAll();
        };

        return window.ShowDialog() == true ? answer.Text : null;
    }
}
