using System.Windows;

namespace DejaVu.App.Localization;

/// <summary>
/// Every word the window shows, in one place.
///
/// The table itself is Strings.en.xaml, a ResourceDictionary. Markup reaches it with
/// DynamicResource and never comes through here at all; this exists for the strings that
/// have to be built in code - a count folded into a sentence, a profile's name inside a
/// question, a menu item assembled at the moment it is shown.
///
/// <para>One language ships and there is no plan for a second. The dictionary stays
/// regardless, because the value was never translation: it is that a sentence the user
/// reads lives in a file of sentences rather than buried in a method that does something
/// else. Changing wording means opening one file and never touching code.</para>
/// </summary>
public static class Strings
{
    /// <summary>
    /// Answers already found, keyed by name.
    ///
    /// Resolving a resource walks the dictionaries merged into the application, and the
    /// menus rebuild their labels every time they open - so the same dozen lookups repeat
    /// constantly for a table that cannot change after startup. Remembering them turns that
    /// walk into one dictionary hit.
    /// </summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal);

    /// <summary>
    /// Puts the table in place.
    ///
    /// Has to happen before anything is drawn, and before the single-instance check, since
    /// the message box that check may raise is itself a string from this table.
    /// </summary>
    public static void Load()
    {
        var table = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Localization/Strings.en.xaml", UriKind.Absolute),
        };

        Application.Current.Resources.MergedDictionaries.Add(table);
        Known.Clear();
    }

    /// <summary>
    /// The sentence stored under a name, with anything after it folded into its {0}, {1}
    /// placeholders.
    ///
    /// A missing name yields the name itself rather than an empty string or an exception:
    /// a label reading "MenuResetEverywhere" is instantly recognizable as a gap in the
    /// table, whereas a blank one looks like a rendering fault and an exception takes the
    /// window down over a typo.
    /// </summary>
    public static string Get(string name, params object?[] values)
    {
        if (!Known.TryGetValue(name, out var text))
        {
            text = Application.Current?.TryFindResource(name) as string ?? name;
            Known[name] = text;
        }

        return values.Length == 0 ? text : string.Format(text, values);
    }
}
