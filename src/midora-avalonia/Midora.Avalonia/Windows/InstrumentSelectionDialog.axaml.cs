using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Midora.Avalonia.Windows;

public partial class InstrumentSelectionDialog : Window
{
    private static readonly string[] ProgramNames =
    [
        "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano",
        "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavinet",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone",
        "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ",
        "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
        "Acoustic Guitar (nylon)", "Acoustic Guitar (steel)", "Electric Guitar (jazz)",
        "Electric Guitar (clean)", "Electric Guitar (muted)", "Overdriven Guitar",
        "Distortion Guitar", "Guitar Harmonics", "Acoustic Bass", "Electric Bass (finger)",
        "Electric Bass (pick)", "Fretless Bass", "Slap Bass 1", "Slap Bass 2",
        "Synth Bass 1", "Synth Bass 2", "Violin", "Viola",
        "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings",
        "Orchestral Harp", "Timpani", "String Ensemble 1", "String Ensemble 2",
        "SynthStrings 1", "SynthStrings 2", "Choir Aahs", "Voice Oohs",
        "Synth Voice", "Orchestra Hit", "Trumpet", "Trombone",
        "Tuba", "Muted Trumpet", "French Horn", "Brass Section",
        "SynthBrass 1", "SynthBrass 2", "Soprano Sax", "Alto Sax",
        "Tenor Sax", "Baritone Sax", "Oboe", "English Horn",
        "Bassoon", "Clarinet", "Piccolo", "Flute",
        "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi",
        "Whistle", "Ocarina", "Lead 1 (square)", "Lead 2 (sawtooth)",
        "Lead 3 (calliope)", "Lead 4 (chiff)", "Lead 5 (charang)", "Lead 6 (voice)",
        "Lead 7 (fifths)", "Lead 8 (bass + lead)", "Pad 1 (new age)", "Pad 2 (warm)",
        "Pad 3 (polysynth)", "Pad 4 (choir)", "Pad 5 (bowed)", "Pad 6 (metallic)",
        "Pad 7 (halo)", "Pad 8 (sweep)", "FX 1 (rain)", "FX 2 (soundtrack)",
        "FX 3 (crystal)", "FX 4 (atmosphere)", "FX 5 (brightness)", "FX 6 (goblins)",
        "FX 7 (echoes)", "FX 8 (sci-fi)", "Sitar", "Banjo",
        "Shamisen", "Koto", "Kalimba", "Bag pipe",
        "Fiddle", "Shanai", "Tinkle Bell", "Agogo",
        "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom",
        "Synth Drum", "Reverse Cymbal", "Guitar Fret Noise", "Breath Noise",
        "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter",
        "Applause", "Gunshot"
    ];

    private static readonly (int Msb, int Lsb, string Name)[] PlaceholderBanks =
    [
        (0, 0, "GM 1 Melodic"),
        (0, 1, "Custom Bank"),
        (128, 0, "GM Drums (Standard Kit)")
    ];

    private const int InheritedMsb = 0;
    private const int InheritedLsb = 0;
    private const int InheritedProgram = 0;

    private readonly List<BankRow> _bankRows = [];
    private readonly bool _inheritance;
    private bool _updating = true;
    private (int Msb, int Lsb)? _displayedBank;

    public InstrumentSelectionDialog()
        : this(null, null, null, allowInheritance: true, timelineTick: null)
    {
    }

    public InstrumentSelectionDialog(
        int? bankMsb,
        int? bankLsb,
        int? program,
        bool allowInheritance,
        long? timelineTick)
    {
        InitializeComponent();
        _inheritance = allowInheritance;
        _updating = true;
        MsbOverride.IsVisible = allowInheritance;
        LsbOverride.IsVisible = allowInheritance;
        ProgramOverride.IsVisible = allowInheritance;
        MsbOverride.IsChecked = !allowInheritance || bankMsb.HasValue;
        LsbOverride.IsChecked = !allowInheritance || bankLsb.HasValue;
        ProgramOverride.IsChecked = !allowInheritance || program.HasValue;
        MsbBox.Text = (bankMsb ?? InheritedMsb).ToString(CultureInfo.InvariantCulture);
        LsbBox.Text = (bankLsb ?? InheritedLsb).ToString(CultureInfo.InvariantCulture);
        ProgramBox.Text = (program ?? InheritedProgram).ToString(CultureInfo.InvariantCulture);
        foreach ((int msb, int lsb, string name) in PlaceholderBanks)
        {
            _bankRows.Add(new BankRow(msb, lsb, $"{msb}.{lsb} — {name}"));
        }

        BanksList.ItemsSource = _bankRows.ToArray();
        ModeBox.ItemsSource = new[] { "Melodic", "Percussion" };
        ModeBox.SelectedItem = "Melodic";
        AutoBox.IsChecked = true;
        KeyBox.Text = "60";
        VelocityBox.Text = "100";
        DurationBox.Text = "500";
        UpdateOverrides();
        if (TryValues(out int effectiveMsb, out int effectiveLsb, out int effectiveProgram))
        {
            SyncLists(effectiveMsb, effectiveLsb, effectiveProgram);
        }

        if (timelineTick.HasValue)
        {
            SetTimelineTick(timelineTick.Value);
        }

        _updating = false;
        BuildKeyboard();
    }

    public int BankMsb { get; private set; }
    public int BankLsb { get; private set; }
    public int Program { get; private set; }
    public bool AutomaticPreview { get; private set; }
    public int PreviewKey { get; private set; } = 60;
    public int PreviewVelocity { get; private set; } = 100;
    public int PreviewDurationMilliseconds { get; private set; } = 500;
    public string PreviewMode { get; private set; } = "Melodic";
    public long? TimelineTick { get; private set; }
    public bool Confirmed { get; private set; }

    public void SetTimelineTick(long tick)
    {
        TimelineTick = tick;
        TimelineTickBox.Text = tick.ToString(CultureInfo.InvariantCulture);
        TimelineTickRow.IsVisible = true;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MsbBox.Focus();
        KeyboardScroll.Offset = new Vector(1000, 0);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ModeBox.IsDropDownOpen)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
            return;
        }

        base.OnKeyDown(e);
    }

    private static bool TryNumber(TextBox box, int minimum, int maximum, out int value) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
        && value >= minimum
        && value <= maximum;

    private bool TryValues(out int msb, out int lsb, out int program)
    {
        if (!TryNumber(MsbBox, 0, 127, out int rawMsb)
            || !TryNumber(LsbBox, 0, 127, out int rawLsb)
            || !TryNumber(ProgramBox, 0, 127, out int rawProgram))
        {
            msb = 0;
            lsb = 0;
            program = 0;
            return false;
        }

        msb = _inheritance && MsbOverride.IsChecked != true ? InheritedMsb : rawMsb;
        lsb = _inheritance && LsbOverride.IsChecked != true ? InheritedLsb : rawLsb;
        program = _inheritance && ProgramOverride.IsChecked != true ? InheritedProgram : rawProgram;
        return true;
    }

    private void UpdateOverrides()
    {
        MsbBox.IsEnabled = !_inheritance || MsbOverride.IsChecked == true;
        LsbBox.IsEnabled = !_inheritance || LsbOverride.IsChecked == true;
        ProgramBox.IsEnabled = !_inheritance || ProgramOverride.IsChecked == true;
        if (!MsbBox.IsEnabled)
        {
            MsbBox.Text = InheritedMsb.ToString(CultureInfo.InvariantCulture);
        }

        if (!LsbBox.IsEnabled)
        {
            LsbBox.Text = InheritedLsb.ToString(CultureInfo.InvariantCulture);
        }

        if (!ProgramBox.IsEnabled)
        {
            ProgramBox.Text = InheritedProgram.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void SyncLists(int bankMsb, int bankLsb, int program)
    {
        bool wasUpdating = _updating;
        _updating = true;
        try
        {
            if (_displayedBank != (bankMsb, bankLsb))
            {
                ProgramsList.ItemsSource = BuildPrograms();
                _displayedBank = (bankMsb, bankLsb);
            }

            EnsureBankRow(bankMsb, bankLsb);
            BanksList.SelectedItem = _bankRows.FirstOrDefault(
                row => row.Msb == bankMsb && row.Lsb == bankLsb);
            ProgramsList.SelectedIndex = Math.Clamp(program, 0, 127);
            if (ProgramsList.SelectedItem is { } selected)
            {
                ProgramsList.ScrollIntoView(selected);
            }
        }
        finally
        {
            _updating = wasUpdating;
        }
    }

    private void EnsureBankRow(int bankMsb, int bankLsb)
    {
        if (_bankRows.Any(row => row.Msb == bankMsb && row.Lsb == bankLsb))
        {
            return;
        }

        _bankRows.Add(new BankRow(bankMsb, bankLsb, $"{bankMsb}.{bankLsb} — Bank {bankMsb}.{bankLsb}"));
        BanksList.ItemsSource = _bankRows.ToArray();
    }

    private static string[] BuildPrograms() => Enumerable.Range(0, 128)
        .Select(program => $"{program} — {ProgramNames[program]}")
        .ToArray();

    private void OnValueChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        if (TryValues(out int msb, out int lsb, out int program))
        {
            SyncLists(msb, lsb, program);
            SetStatus("Preview");
        }
        else
        {
            SetStatus("Bank MSB, Bank LSB and Program must be integers from 0 to 127.", true);
        }
    }

    private void OnOverrideChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        UpdateOverrides();
        _updating = false;
        if (TryValues(out int msb, out int lsb, out int program))
        {
            SyncLists(msb, lsb, program);
        }

        SetStatus("Preview");
    }

    private void OnBankSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || BanksList.SelectedItem is not BankRow bank)
        {
            return;
        }

        _updating = true;
        MsbOverride.IsChecked = true;
        LsbOverride.IsChecked = true;
        MsbBox.Text = bank.Msb.ToString(CultureInfo.InvariantCulture);
        LsbBox.Text = bank.Lsb.ToString(CultureInfo.InvariantCulture);
        UpdateOverrides();
        _updating = false;
        if (TryValues(out int msb, out int lsb, out int program))
        {
            SyncLists(msb, lsb, program);
        }

        SetStatus("Preview");
    }

    private void OnProgramSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || ProgramsList.SelectedIndex < 0)
        {
            return;
        }

        _updating = true;
        MsbOverride.IsChecked = true;
        LsbOverride.IsChecked = true;
        ProgramOverride.IsChecked = true;
        ProgramBox.Text = ProgramsList.SelectedIndex.ToString(CultureInfo.InvariantCulture);
        UpdateOverrides();
        _updating = false;
        SetStatus("Preview");
    }

    private bool ReadPreviewSettings()
    {
        if (!TryNumber(KeyBox, 0, 127, out int key)
            || !TryNumber(VelocityBox, 1, 127, out int velocity)
            || !TryNumber(DurationBox, 1, int.MaxValue, out int duration))
        {
            return false;
        }

        AutomaticPreview = AutoBox.IsChecked == true;
        PreviewKey = key;
        PreviewVelocity = velocity;
        PreviewDurationMilliseconds = duration;
        PreviewMode = ModeBox.SelectedItem as string ?? "Melodic";
        return true;
    }

    private void OnPreviewToggle(object? sender, RoutedEventArgs e) => UpdatePreviewSettings();

    private void OnPreviewTextChanged(object? sender, TextChangedEventArgs e) => UpdatePreviewSettings();

    private void OnPreviewSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreviewSettings();

    private void UpdatePreviewSettings()
    {
        if (_updating)
        {
            return;
        }

        bool valid = ReadPreviewSettings();
        SetStatus(valid ? "Preview" : "Preview settings must use integers.", !valid);
    }

    private void OnPreviewKeyPressed(int note, PointerPressedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        KeyBox.Text = note.ToString(CultureInfo.InvariantCulture);
        VelocityBox.Text = PreviewVelocity.ToString(CultureInfo.InvariantCulture);
        _updating = false;
        SetStatus($"Preview · note {note}");
    }

    private void OnPreviewKeyReleased(int note) => SetStatus("Preview");

    private void OnOkClick(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Cancel();

    private void Accept()
    {
        if (!TryValues(out int msb, out int lsb, out int program))
        {
            SetStatus("Bank and Program must be integers from 0 to 127.", true);
            return;
        }

        if (TimelineTickRow.IsVisible)
        {
            if (!long.TryParse(TimelineTickBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long tick)
                || tick == long.MaxValue)
            {
                SetStatus("Tick must be a non-negative integer below Int64.MaxValue.", true);
                return;
            }

            TimelineTick = tick;
        }

        BankMsb = msb;
        BankLsb = lsb;
        Program = program;
        ReadPreviewSettings();
        // Avalonia has no DialogResult: callers read Confirmed after ShowDialog completes.
        Confirmed = true;
        Close();
    }

    private void Cancel()
    {
        Confirmed = false;
        Close();
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = ResolveBrush(
            error ? "Brush.Red.Hover" : "Brush.Text.Tertiary",
            error ? "#F2555A" : "#747E8C");
    }

    private void BuildKeyboard()
    {
        IBrush white = ResolveBrush("Brush.PianoKey.White", "#D4D8DD");
        IBrush black = ResolveBrush("Brush.PianoKey.Black", "#15181D");
        IBrush outline = ResolveBrush("Brush.Border.Strong", "#3A424F");
        for (int note = 0; note < 128; note++)
        {
            bool isBlack = note % 12 is 1 or 3 or 6 or 8 or 10;
            var key = new Rectangle
            {
                Width = isBlack ? 12 : 20,
                Height = isBlack ? 56 : 90,
                Fill = isBlack ? black : white,
                Stroke = outline,
                StrokeThickness = 1
            };
            Canvas.SetLeft(key, isBlack ? (note * 20) - 6 : note * 20);
            Canvas.SetTop(key, 0);
            int noteNumber = note;
            key.PointerPressed += (_, e) => OnPreviewKeyPressed(noteNumber, e);
            key.PointerReleased += (_, _) => OnPreviewKeyReleased(noteNumber);
            PreviewKeyboard.Children.Add(key);
        }
    }

    private IBrush ResolveBrush(string key, string fallback) =>
        this.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : Brush.Parse(fallback);

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private sealed record BankRow(int Msb, int Lsb, string Label)
    {
        public override string ToString() => Label;
    }
}
