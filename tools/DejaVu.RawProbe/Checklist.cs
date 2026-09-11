using DejaVu.Core.Model;

namespace DejaVu.RawProbe;

/// <summary>
/// Checks a physical n52 against <see cref="PadLayout"/>.
///
/// Not a log viewer. The question this tool exists to answer is narrow - "does the unit in
/// front of me emit what the layout claims it does" - and a scrolling dump of everything
/// answers it badly: you end up reading hex and comparing it to a table by eye, which is the
/// error-prone half of the job. So the layout IS the interface here. Every input the model
/// declares gets a row, each row starts unconfirmed, and pressing the control fills it in.
/// Work down the pad and the answer is the state of the list when you finish: everything
/// green means the model matches this unit, and anything still gray is the interesting part.
///
/// <para>Signals the layout does not claim are collected separately rather than discarded.
/// A pad that reports something extra is exactly the discovery worth making, and it would be
/// invisible in a checklist that only knew how to tick boxes.</para>
/// </summary>
internal sealed class Checklist : Form
{
    /// <summary>
    /// Entry point.
    ///
    /// Single-threaded apartment because this is a window, and Raw Input is delivered to a
    /// window as a message - so the apartment the tool runs in is not incidental to it.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Checklist());
    }

    private const string Hardware = "VID_050D";
    private const string Model = "PID_0815";

    private readonly ListView _inputs = new();
    private readonly TextBox _unknown = new();
    private readonly Label _tally = new();
    private readonly CheckBox _thisPadOnly = new();

    /// <summary>Row per layout entry, keyed by the id, so a hit is a dictionary lookup.</summary>
    private readonly Dictionary<string, ListViewItem> _rows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each declared signal maps back to. Diagonals are excluded deliberately.</summary>
    private readonly Dictionary<PadSignal, PadInput> _expected = [];

    private readonly HashSet<string> _confirmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _report = Path.Combine(AppContext.BaseDirectory, "rawprobe-log.txt");

    public Checklist()
    {
        Text = "n52 layout check";
        Width = 900;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;

        _inputs.View = View.Details;
        _inputs.FullRowSelect = true;
        _inputs.GridLines = false;
        _inputs.Dock = DockStyle.Fill;
        _inputs.Columns.Add("Input", 190);
        _inputs.Columns.Add("Interface", 80);
        _inputs.Columns.Add("Layout says", 200);
        _inputs.Columns.Add("Observed", 200);
        _inputs.Columns.Add("", 120);

        _unknown.Multiline = true;
        _unknown.ReadOnly = true;
        _unknown.ScrollBars = ScrollBars.Vertical;
        _unknown.WordWrap = false;
        _unknown.Dock = DockStyle.Bottom;
        _unknown.Height = 150;
        _unknown.Font = new Font("Consolas", 9f);

        _tally.Dock = DockStyle.Bottom;
        _tally.Height = 26;
        _tally.Padding = new Padding(8, 5, 8, 0);

        _thisPadOnly.Text = "Only this pad (VID_050D / PID_0815)";
        _thisPadOnly.Checked = true;
        _thisPadOnly.AutoSize = true;
        _thisPadOnly.Margin = new Padding(4, 6, 16, 0);

        var reset = new Button { Text = "Start over", AutoSize = true };
        var save = new Button { Text = "Write report", AutoSize = true };
        reset.Click += (_, _) => Restart();
        save.Click += (_, _) => WriteReport();

        var strip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 4, 8, 4) };
        strip.Controls.AddRange([_thisPadOnly, reset, save]);

        var help = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(10, 8, 10, 8),
            Text = "Press every control on the pad once. Each row fills in as its signal arrives.\r\n"
                 + "Rows still gray at the end are the ones this unit does not report as the layout expects.",
        };

        Controls.AddRange([_inputs, _unknown, _tally, strip, help]);

        Build();

        Load += (_, _) =>
        {
            try
            {
                Listener.Listen(Handle);
            }
            catch (Exception ex)
            {
                Note("Could not start listening: " + ex.Message);
            }
        };
    }

    /// <summary>
    /// Lays out one row per input the model declares.
    ///
    /// Diagonals are listed but never expected to arrive on their own: the pad has no switch
    /// for a corner, it closes the two arrows either side, so the engine composes them. Showing
    /// them keeps the list honest about what the editor offers; marking them as unconfirmable
    /// stops them reading as failures.
    /// </summary>
    private void Build()
    {
        foreach (var button in PadLayout.Buttons)
        {
            var row = new ListViewItem(button.Label ?? button.Id);
            row.SubItems.Add(button.Signal.Kind == SignalKind.Keyboard ? "MI_00" : "MI_01");
            row.SubItems.Add(button.IsCombination
                ? "two arrows together"
                : button.Signal.ToString());
            row.SubItems.Add("");
            row.SubItems.Add(button.IsCombination ? "composed" : "waiting");
            row.ForeColor = button.IsCombination ? SystemColors.GrayText : SystemColors.ControlText;

            _rows[button.Id] = row;
            _inputs.Items.Add(row);

            if (!button.IsCombination) _expected[button.Signal] = button;
        }

        Tally();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Listener.WmInput && Listener.Decode(m.LParam) is { } seen) Record(seen);
        base.WndProc(ref m);
    }

    private void Record(Observation seen)
    {
        var mine = seen.Path.Contains(Hardware, StringComparison.OrdinalIgnoreCase)
                   && seen.Path.Contains(Model, StringComparison.OrdinalIgnoreCase);

        if (_thisPadOnly.Checked && !mine) return;

        // Releases confirm nothing a press has not already confirmed, and listing them would
        // double every line in the unmatched pane for no added information.
        if (seen.Released) return;

        if (seen.Signal is { } signal && _expected.TryGetValue(signal, out var button))
        {
            var row = _rows[button.Id];
            row.SubItems[3].Text = seen.Detail;

            if (_confirmed.Add(button.Id))
            {
                row.SubItems[4].Text = "confirmed";
                row.ForeColor = Color.FromArgb(0x1B, 0x7F, 0x3B);
                Tally();
            }

            // Arriving on the wrong interface still counts as reaching us, but it means the
            // model's idea of the topology is wrong for this unit, which is worth saying.
            var expectedFace = row.SubItems[1].Text;
            if (!string.Equals(expectedFace, seen.Interface, StringComparison.OrdinalIgnoreCase))
            {
                row.SubItems[4].Text = $"on {seen.Interface}, not {expectedFace}";
                row.ForeColor = Color.FromArgb(0xB0, 0x54, 0x00);
            }

            return;
        }

        Note($"{seen.Interface}  {seen.Detail}");
    }

    private void Tally()
    {
        var real = PadLayout.Buttons.Count(b => !b.IsCombination);
        _tally.Text = $"{_confirmed.Count} of {real} confirmed"
                    + (_confirmed.Count == real ? "  —  this unit matches PadLayout" : "");
    }

    private void Restart()
    {
        _confirmed.Clear();
        _unknown.Clear();

        foreach (var button in PadLayout.Buttons)
        {
            var row = _rows[button.Id];
            row.SubItems[3].Text = "";
            row.SubItems[4].Text = button.IsCombination ? "composed" : "waiting";
            row.ForeColor = button.IsCombination ? SystemColors.GrayText : SystemColors.ControlText;
        }

        Tally();
    }

    private void Note(string line)
    {
        // The unmatched pane is for surprises, and a surprise repeated four hundred times is
        // still one surprise. Capped rather than trimmed: whatever appeared first is the part
        // worth keeping.
        if (_unknown.Lines.Length > 500) return;
        _unknown.AppendText(line + Environment.NewLine);
    }

    /// <summary>
    /// Writes the result somewhere it can be read back or pasted into an issue.
    ///
    /// Written on request rather than continuously. The earlier design appended every event to
    /// disk as it arrived, which for a held key meant a file growing at thirty lines a second
    /// and a report nobody could read.
    /// </summary>
    private void WriteReport()
    {
        var lines = new List<string>
        {
            $"n52 layout check  {DateTime.Now:yyyy-MM-dd HH:mm}",
            _tally.Text,
            new('-', 78),
        };

        foreach (ListViewItem row in _inputs.Items)
        {
            lines.Add(string.Format("{0,-22} {1,-7} {2,-24} {3,-24} {4}",
                row.Text,
                row.SubItems[1].Text,
                row.SubItems[2].Text,
                row.SubItems[3].Text,
                row.SubItems[4].Text));
        }

        if (_unknown.TextLength > 0)
        {
            lines.Add("");
            lines.Add("Signals the layout does not claim:");
            lines.AddRange(_unknown.Lines);
        }

        try
        {
            File.WriteAllLines(_report, lines);
            MessageBox.Show(this, _report, "Report written");
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Could not write the report");
        }
    }
}
