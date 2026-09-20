using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Avalonia port of <c>Midora.Desktop.InstrumentCatalogDialog</c>. The WPF window edits an
/// <c>InstrumentCatalogState</c> draft against the application SoundFont list; this port keeps the
/// full layout, placeholder catalog rows and the same editing/validation rules, but has no access to
/// the Midora.Application catalog domain or the application SoundFont list. Profiles, banks,
/// programs, overrides, SF2 scan rows and import rows are local placeholder data.
/// </summary>
public partial class InstrumentCatalogDialog : Window
{
    private const int MaximumDisplayNameLength = 256;

    private static readonly string[] GeneralMidiNames =
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

    private readonly ObservableCollection<InstrumentCatalogProfileRow> _profiles = [];
    private readonly ObservableCollection<InstrumentCatalogOverrideRow> _overrides = [];
    private readonly ObservableCollection<InstrumentCatalogScanBankRow> _scanBanks = [];
    private readonly ObservableCollection<InstrumentCatalogImportConflictRow> _importConflicts = [];
    private readonly ObservableCollection<InstrumentCatalogImportTargetOption> _importTargets = [];

    private bool _initializing = true;
    private bool _suppressNavigation;
    private bool _addingBank;
    private bool _addingProgram;
    private bool _addingOverride;
    private InstrumentCatalogProfileRow? _activeProfile;
    private InstrumentCatalogBankRow? _activeBank;
    private InstrumentCatalogProgramRow? _activeProgram;
    private InstrumentCatalogOverrideRow? _activeOverride;
    private InstrumentCatalogProfileRow? _rescanProfile;
    private TabItem? _activeCatalogTab;

    public InstrumentCatalogDialog()
    {
        InitializeComponent();

        _suppressNavigation = true;
        SeedPlaceholderCatalog();
        GeneralMidiEnabledBox.IsChecked = true;
        ProfileList.ItemsSource = _profiles;
        OverrideList.ItemsSource = _overrides;
        ScanBankList.ItemsSource = _scanBanks;
        ImportConflictList.ItemsSource = _importConflicts;
        ImportTargetBox.ItemsSource = _importTargets;
        ProfileList.SelectedIndex = _profiles.Count > 0 ? 0 : -1;
        OverrideList.SelectedIndex = _overrides.Count > 0 ? 0 : -1;
        _suppressNavigation = false;
        LoadProfileEditor(SelectedProfile);
        if (_overrides.Count > 0)
        {
            LoadOverrideEditor(SelectedOverride);
        }
        else
        {
            BeginNewOverride();
        }

        _activeCatalogTab = CatalogTabs.SelectedItem as TabItem;
        _initializing = false;
        RefreshSelectedProgramResolution();
    }

    /// <summary>Avalonia has no DialogResult: callers read <see cref="Confirmed"/> after the dialog closes.</summary>
    public bool Confirmed { get; private set; }

    private InstrumentCatalogProfileRow? SelectedProfile => ProfileList.SelectedItem as InstrumentCatalogProfileRow;

    private InstrumentCatalogBankRow? SelectedBank => BankList.SelectedItem as InstrumentCatalogBankRow;

    private InstrumentCatalogProgramRow? SelectedProgram => ProgramList.SelectedItem as InstrumentCatalogProgramRow;

    private InstrumentCatalogOverrideRow? SelectedOverride => OverrideList.SelectedItem as InstrumentCatalogOverrideRow;

    private void SeedPlaceholderCatalog()
    {
        InstrumentCatalogProfileRow imported = new(
            "imported-fluidr3",
            "FluidR3_GM",
            enabled: true,
            sourceLabel: "Imported: FluidR3_GM");
        InstrumentCatalogBankRow melodic = new(0, 0, "General MIDI")
        {
            Programs =
            {
                new InstrumentCatalogProgramRow(0, "Acoustic Grand Piano"),
                new InstrumentCatalogProgramRow(1, "Bright Acoustic Piano"),
                new InstrumentCatalogProgramRow(8, "Celesta"),
                new InstrumentCatalogProgramRow(48, "String Ensemble 1")
            }
        };
        InstrumentCatalogBankRow percussion = new(128, 0, "Standard Kit")
        {
            Programs =
            {
                new InstrumentCatalogProgramRow(0, "Standard"),
                new InstrumentCatalogProgramRow(8, "Room")
            }
        };
        imported.Banks.Add(melodic);
        imported.Banks.Add(percussion);

        InstrumentCatalogProfileRow user = new(
            "user-stage",
            "Stage Set",
            enabled: true,
            sourceLabel: "User Profile");
        user.Banks.Add(new InstrumentCatalogBankRow(0, 1, "Stage Pianos")
        {
            Programs =
            {
                new InstrumentCatalogProgramRow(0, "Concert Grand"),
                new InstrumentCatalogProgramRow(1, "Bright Stage")
            }
        });
        _profiles.Add(imported);
        _profiles.Add(user);

        _overrides.Add(new InstrumentCatalogOverrideRow(0, 0, 0, null, "Piano (Override)"));
        _overrides.Add(new InstrumentCatalogOverrideRow(0, 1, 0, "Stage Pianos", null));
    }

    private void OnNewProfileClick(object? sender, RoutedEventArgs e)
    {
        if (!TryLeavePendingCatalogEditors(bank: true, program: true, over: false, "creating another Profile"))
        {
            return;
        }

        HideValidation();
        InstrumentCatalogProfileRow profile = new(
            Guid.NewGuid().ToString("N"),
            GetUniqueProfileName("New Profile"),
            enabled: true,
            sourceLabel: "User Profile");
        _profiles.Add(profile);
        SelectProfileForEditing(profile);
        ProfileList.ScrollIntoView(profile);
        RefreshSelectedProgramResolution();
    }

    private async void OnRenameProfileClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select a Profile to rename.");
            return;
        }

        string? name = await TextInputDialog.ShowAsync(this, "Rename Catalog Profile", "Profile name", profile.DisplayName);
        if (name is null)
        {
            return;
        }

        try
        {
            profile.DisplayName = NormalizeRequiredName(name, "Profile name");
            HideValidation();
        }
        catch (ArgumentException exception)
        {
            ShowValidation(exception.Message);
        }
    }

    private void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select a Profile to delete.");
            return;
        }

        if (!TryLeavePendingCatalogEditors(bank: true, program: true, over: false, "deleting this Profile"))
        {
            return;
        }

        int index = _profiles.IndexOf(profile);
        InstrumentCatalogProfileRow? next;
        _suppressNavigation = true;
        try
        {
            _profiles.RemoveAt(index);
            ProfileList.SelectedIndex = _profiles.Count == 0 ? -1 : Math.Min(index, _profiles.Count - 1);
            next = SelectedProfile;
        }
        finally
        {
            _suppressNavigation = false;
        }

        LoadProfileEditor(next);
        HideValidation();
    }

    private void OnMoveProfileUpClick(object? sender, RoutedEventArgs e) => MoveSelectedProfile(-1);

    private void OnMoveProfileDownClick(object? sender, RoutedEventArgs e) => MoveSelectedProfile(1);

    private void MoveSelectedProfile(int delta)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select a Profile to move.");
            return;
        }

        int source = _profiles.IndexOf(profile);
        int target = source + delta;
        if (target < 0 || target >= _profiles.Count)
        {
            return;
        }

        _profiles.Move(source, target);
        ProfileList.SelectedItem = profile;
        HideValidation();
    }

    private void OnCatalogResolutionInputsChanged(object? sender, RoutedEventArgs e) => RefreshSelectedProgramResolution();

    private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation)
        {
            return;
        }

        InstrumentCatalogProfileRow? requested = SelectedProfile;
        if (ReferenceEquals(requested, _activeProfile))
        {
            return;
        }

        if (!TryLeavePendingCatalogEditors(bank: true, program: true, over: false, "selecting another Profile"))
        {
            RestoreSelection(ProfileList, _activeProfile);
            return;
        }

        LoadProfileEditor(requested);
    }

    private void LoadProfileEditor(InstrumentCatalogProfileRow? profile)
    {
        _activeProfile = profile;
        _suppressNavigation = true;
        try
        {
            BankList.ItemsSource = profile?.Banks;
            BankList.SelectedIndex = profile is { Banks.Count: > 0 } ? 0 : -1;
        }
        finally
        {
            _suppressNavigation = false;
        }

        if (SelectedBank is { } bank)
        {
            LoadBankEditor(bank);
        }
        else
        {
            BeginNewBank();
        }

        RefreshSelectedProgramResolution();
    }

    private void OnNewBankClick(object? sender, RoutedEventArgs e)
    {
        if (TryLeavePendingCatalogEditors(bank: true, program: true, over: false, "starting a new Bank"))
        {
            BeginNewBank();
            HideValidation();
        }
    }

    private void BeginNewBank()
    {
        _activeBank = null;
        _addingBank = true;
        RestoreSelection(BankList, null);
        BankMsbBox.Text = "0";
        BankLsbBox.Text = "0";
        BankNameBox.Text = string.Empty;
        ApplyBankButton.Content = "Add Bank";
        bool wasSuppressed = _suppressNavigation;
        _suppressNavigation = true;
        try
        {
            ProgramList.ItemsSource = null;
        }
        finally
        {
            _suppressNavigation = wasSuppressed;
        }

        BeginNewProgram();
    }

    private void OnBankSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation)
        {
            return;
        }

        InstrumentCatalogBankRow? requested = SelectedBank;
        if (ReferenceEquals(requested, _activeBank) && (!_addingBank || requested is null))
        {
            return;
        }

        if (!TryLeavePendingCatalogEditors(bank: true, program: true, over: false, "selecting another Bank"))
        {
            RestoreSelection(BankList, _activeBank);
            return;
        }

        LoadBankEditor(requested);
    }

    private void LoadBankEditor(InstrumentCatalogBankRow? bank)
    {
        if (bank is null)
        {
            BeginNewBank();
            return;
        }

        _activeBank = bank;
        _addingBank = false;
        BankMsbBox.Text = bank.BankMsb.ToString(CultureInfo.InvariantCulture);
        BankLsbBox.Text = bank.BankLsb.ToString(CultureInfo.InvariantCulture);
        BankNameBox.Text = bank.DisplayName ?? string.Empty;
        ApplyBankButton.Content = "Apply Bank";
        _suppressNavigation = true;
        try
        {
            ProgramList.ItemsSource = bank.Programs;
            ProgramList.SelectedIndex = bank.Programs.Count > 0 ? 0 : -1;
        }
        finally
        {
            _suppressNavigation = false;
        }

        if (SelectedProgram is { } program)
        {
            LoadProgramEditor(program);
        }
        else
        {
            BeginNewProgram();
        }

        RefreshSelectedProgramResolution();
    }

    private void OnApplyBankClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select or create a Profile before adding a Bank.");
            return;
        }

        if (!TryMidiByte(BankMsbBox, "Bank MSB", out byte msb)
            || !TryMidiByte(BankLsbBox, "Bank LSB", out byte lsb))
        {
            return;
        }

        string? name;
        try
        {
            name = NormalizeOptionalName(BankNameBox.Text);
        }
        catch (ArgumentException exception)
        {
            ShowValidation(exception.Message);
            return;
        }

        bool wasAdding = _addingBank;
        InstrumentCatalogBankRow? edited = wasAdding ? null : _activeBank;
        if (profile.Banks.Any(value => !ReferenceEquals(value, edited)
            && value.BankMsb == msb
            && value.BankLsb == lsb))
        {
            ShowValidation($"Bank MSB {msb} / LSB {lsb} already exists in this Profile.");
            return;
        }

        if (edited is null)
        {
            edited = new InstrumentCatalogBankRow(msb, lsb, name);
            profile.Banks.Add(edited);
        }
        else
        {
            edited.Update(msb, lsb, name);
        }

        Reposition(
            profile.Banks,
            edited,
            static (left, right) =>
            {
                int comparison = left.BankMsb.CompareTo(right.BankMsb);
                return comparison != 0 ? comparison : left.BankLsb.CompareTo(right.BankLsb);
            });
        _activeBank = edited;
        _addingBank = false;
        RestoreSelection(BankList, edited);
        BankMsbBox.Text = edited.BankMsb.ToString(CultureInfo.InvariantCulture);
        BankLsbBox.Text = edited.BankLsb.ToString(CultureInfo.InvariantCulture);
        BankNameBox.Text = edited.DisplayName ?? string.Empty;
        ApplyBankButton.Content = "Apply Bank";
        if (wasAdding)
        {
            ProgramList.ItemsSource = edited.Programs;
        }

        BankList.ScrollIntoView(edited);
        RefreshSelectedProgramResolution();
        HideValidation();
    }

    private void OnDeleteBankClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile || SelectedBank is not { } bank)
        {
            ShowValidation("Select a Bank to delete.");
            return;
        }

        if (!TryLeavePendingCatalogEditors(bank: true, program: true, over: false, "deleting this Bank"))
        {
            return;
        }

        int index = profile.Banks.IndexOf(bank);
        InstrumentCatalogBankRow? next;
        _suppressNavigation = true;
        try
        {
            profile.Banks.RemoveAt(index);
            BankList.SelectedIndex = profile.Banks.Count == 0 ? -1 : Math.Min(index, profile.Banks.Count - 1);
            next = SelectedBank;
        }
        finally
        {
            _suppressNavigation = false;
        }

        LoadBankEditor(next);
        HideValidation();
    }

    private void OnNewProgramClick(object? sender, RoutedEventArgs e)
    {
        if (TryLeavePendingCatalogEditors(bank: false, program: true, over: false, "starting a new Program"))
        {
            BeginNewProgram();
            HideValidation();
        }
    }

    private void BeginNewProgram()
    {
        _activeProgram = null;
        _addingProgram = true;
        RestoreSelection(ProgramList, null);
        ProgramNumberBox.Text = "0";
        ProgramNameBox.Text = string.Empty;
        ApplyProgramButton.Content = "Add Program";
        RefreshSelectedProgramResolution();
    }

    private void OnProgramSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation)
        {
            return;
        }

        InstrumentCatalogProgramRow? requested = SelectedProgram;
        if (ReferenceEquals(requested, _activeProgram) && (!_addingProgram || requested is null))
        {
            return;
        }

        if (!TryLeavePendingCatalogEditors(bank: false, program: true, over: false, "selecting another Program"))
        {
            RestoreSelection(ProgramList, _activeProgram);
            return;
        }

        LoadProgramEditor(requested);
    }

    private void LoadProgramEditor(InstrumentCatalogProgramRow? program)
    {
        if (program is null)
        {
            BeginNewProgram();
            return;
        }

        _activeProgram = program;
        _addingProgram = false;
        ProgramNumberBox.Text = program.Program.ToString(CultureInfo.InvariantCulture);
        ProgramNameBox.Text = program.DisplayName;
        ApplyProgramButton.Content = "Apply Program";
        RefreshSelectedProgramResolution();
    }

    private void OnApplyProgramClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedBank is not { } bank)
        {
            ShowValidation("Select or add a Bank before adding a Program.");
            return;
        }

        if (!TryMidiByte(ProgramNumberBox, "Program", out byte programNumber))
        {
            return;
        }

        string name;
        try
        {
            name = NormalizeRequiredName(ProgramNameBox.Text, "Program display name");
        }
        catch (ArgumentException exception)
        {
            ShowValidation(exception.Message);
            return;
        }

        InstrumentCatalogProgramRow? edited = _addingProgram ? null : _activeProgram;
        if (bank.Programs.Any(value => !ReferenceEquals(value, edited) && value.Program == programNumber))
        {
            ShowValidation($"Program {programNumber} already exists in this Bank.");
            return;
        }

        if (edited is null)
        {
            edited = new InstrumentCatalogProgramRow(programNumber, name);
            bank.Programs.Add(edited);
        }
        else
        {
            edited.Update(programNumber, name);
        }

        Reposition(bank.Programs, edited, static (left, right) => left.Program.CompareTo(right.Program));
        _activeProgram = edited;
        _addingProgram = false;
        RestoreSelection(ProgramList, edited);
        ProgramNumberBox.Text = edited.Program.ToString(CultureInfo.InvariantCulture);
        ProgramNameBox.Text = edited.DisplayName;
        ApplyProgramButton.Content = "Apply Program";
        ProgramList.ScrollIntoView(edited);
        RefreshSelectedProgramResolution();
        HideValidation();
    }

    private void OnDeleteProgramClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedBank is not { } bank || SelectedProgram is not { } program)
        {
            ShowValidation("Select a Program to delete.");
            return;
        }

        if (!TryLeavePendingCatalogEditors(bank: false, program: true, over: false, "deleting this Program"))
        {
            return;
        }

        int index = bank.Programs.IndexOf(program);
        InstrumentCatalogProgramRow? next;
        _suppressNavigation = true;
        try
        {
            bank.Programs.RemoveAt(index);
            ProgramList.SelectedIndex = bank.Programs.Count == 0 ? -1 : Math.Min(index, bank.Programs.Count - 1);
            next = SelectedProgram;
        }
        finally
        {
            _suppressNavigation = false;
        }

        LoadProgramEditor(next);
        HideValidation();
    }

    private void OnNewOverrideClick(object? sender, RoutedEventArgs e)
    {
        if (TryLeavePendingCatalogEditors(bank: false, program: false, over: true, "starting a new User Override"))
        {
            BeginNewOverride();
            HideValidation();
        }
    }

    private void BeginNewOverride()
    {
        _activeOverride = null;
        _addingOverride = true;
        RestoreSelection(OverrideList, null);
        OverrideMsbBox.Text = "0";
        OverrideLsbBox.Text = "0";
        OverrideProgramBox.Text = "0";
        OverrideBankNameBox.Text = string.Empty;
        OverrideProgramNameBox.Text = string.Empty;
    }

    private void OnOverrideSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation)
        {
            return;
        }

        InstrumentCatalogOverrideRow? requested = SelectedOverride;
        if (ReferenceEquals(requested, _activeOverride) && (!_addingOverride || requested is null))
        {
            return;
        }

        if (!TryLeavePendingCatalogEditors(bank: false, program: false, over: true, "selecting another User Override"))
        {
            RestoreSelection(OverrideList, _activeOverride);
            return;
        }

        LoadOverrideEditor(requested);
    }

    private void LoadOverrideEditor(InstrumentCatalogOverrideRow? catalogOverride)
    {
        if (catalogOverride is null)
        {
            BeginNewOverride();
            return;
        }

        _activeOverride = catalogOverride;
        _addingOverride = false;
        OverrideMsbBox.Text = catalogOverride.BankMsb.ToString(CultureInfo.InvariantCulture);
        OverrideLsbBox.Text = catalogOverride.BankLsb.ToString(CultureInfo.InvariantCulture);
        OverrideProgramBox.Text = catalogOverride.Program.ToString(CultureInfo.InvariantCulture);
        OverrideBankNameBox.Text = catalogOverride.BankDisplayName ?? string.Empty;
        OverrideProgramNameBox.Text = catalogOverride.ProgramDisplayName ?? string.Empty;
    }

    private void OnApplyOverrideClick(object? sender, RoutedEventArgs e)
    {
        if (!TryMidiByte(OverrideMsbBox, "Bank MSB", out byte msb)
            || !TryMidiByte(OverrideLsbBox, "Bank LSB", out byte lsb)
            || !TryMidiByte(OverrideProgramBox, "Program", out byte program))
        {
            return;
        }

        string? bankName;
        string? programName;
        try
        {
            bankName = NormalizeOptionalName(OverrideBankNameBox.Text);
            programName = NormalizeOptionalName(OverrideProgramNameBox.Text);
            if (bankName is null && programName is null)
            {
                throw new ArgumentException("A catalog override must provide a bank name, a program name, or both.");
            }
        }
        catch (ArgumentException exception)
        {
            ShowValidation(exception.Message);
            return;
        }

        InstrumentCatalogOverrideRow? edited = _addingOverride ? null : _activeOverride;
        if (_overrides.Any(value => !ReferenceEquals(value, edited)
            && value.BankMsb == msb
            && value.BankLsb == lsb
            && value.Program == program))
        {
            ShowValidation("An override already exists for that exact Bank and Program address.");
            return;
        }

        string? conflictingBankName = _overrides
            .Where(value => !ReferenceEquals(value, edited)
                && value.BankMsb == msb
                && value.BankLsb == lsb
                && value.BankDisplayName is not null)
            .Select(value => value.BankDisplayName)
            .FirstOrDefault(value => !string.Equals(value, bankName, StringComparison.Ordinal));
        if (bankName is not null && conflictingBankName is not null)
        {
            ShowValidation($"This Bank is already named '{conflictingBankName}' by another override.");
            return;
        }

        if (edited is null)
        {
            edited = new InstrumentCatalogOverrideRow(msb, lsb, program, bankName, programName);
            _overrides.Add(edited);
        }
        else
        {
            edited.Update(msb, lsb, program, bankName, programName);
        }

        Reposition(
            _overrides,
            edited,
            static (left, right) =>
            {
                int comparison = left.BankMsb.CompareTo(right.BankMsb);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = left.BankLsb.CompareTo(right.BankLsb);
                return comparison != 0 ? comparison : left.Program.CompareTo(right.Program);
            });
        _activeOverride = edited;
        _addingOverride = false;
        RestoreSelection(OverrideList, edited);
        OverrideMsbBox.Text = edited.BankMsb.ToString(CultureInfo.InvariantCulture);
        OverrideLsbBox.Text = edited.BankLsb.ToString(CultureInfo.InvariantCulture);
        OverrideProgramBox.Text = edited.Program.ToString(CultureInfo.InvariantCulture);
        OverrideBankNameBox.Text = edited.BankDisplayName ?? string.Empty;
        OverrideProgramNameBox.Text = edited.ProgramDisplayName ?? string.Empty;
        OverrideList.ScrollIntoView(edited);
        RefreshSelectedProgramResolution();
        HideValidation();
    }

    private void OnDeleteOverrideClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedOverride is not { } catalogOverride)
        {
            ShowValidation("Select an override to delete.");
            return;
        }

        if (!TryLeavePendingCatalogEditors(bank: false, program: false, over: true, "deleting this User Override"))
        {
            return;
        }

        int index = _overrides.IndexOf(catalogOverride);
        InstrumentCatalogOverrideRow? next;
        _suppressNavigation = true;
        try
        {
            _overrides.RemoveAt(index);
            OverrideList.SelectedIndex = _overrides.Count == 0 ? -1 : Math.Min(index, _overrides.Count - 1);
            next = SelectedOverride;
        }
        finally
        {
            _suppressNavigation = false;
        }

        LoadOverrideEditor(next);
        RefreshSelectedProgramResolution();
        HideValidation();
    }

    private void RefreshSelectedProgramResolution()
    {
        if (_initializing || ResolvedProgramText is null)
        {
            return;
        }

        if (SelectedBank is not { } bank || SelectedProgram is not { } program)
        {
            ResolvedProgramText.Text = "Select a Program to preview its effective name and source.";
            return;
        }

        ResolvedProgramText.Text = ResolveProgramDisplay(bank.BankMsb, bank.BankLsb, program.Program);
    }

    private string ResolveProgramDisplay(byte bankMsb, byte bankLsb, byte program)
    {
        InstrumentCatalogOverrideRow? userOverride = _overrides.FirstOrDefault(
            value => value.BankMsb == bankMsb && value.BankLsb == bankLsb && value.Program == program);
        if (userOverride?.ProgramDisplayName is { } overrideName)
        {
            return $"{overrideName} (User Override)";
        }

        foreach (InstrumentCatalogProfileRow profile in _profiles.Where(value => value.Enabled))
        {
            InstrumentCatalogBankRow? bank = profile.Banks.FirstOrDefault(
                value => value.BankMsb == bankMsb && value.BankLsb == bankLsb);
            InstrumentCatalogProgramRow? entry = bank?.Programs.FirstOrDefault(value => value.Program == program);
            if (entry is not null)
            {
                return $"{entry.DisplayName} ({profile.SourceLabel})";
            }
        }

        if (GeneralMidiEnabledBox.IsChecked == true && bankMsb == 0 && bankLsb == 0)
        {
            return $"{GeneralMidiNames[program]} (General MIDI)";
        }

        return $"Bank MSB {bankMsb} / LSB {bankLsb} / Program {program}";
    }

    private async void OnExportProfileClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select a Profile to export.");
            return;
        }

        string fileName = SanitizeExportFileName(profile.DisplayName) + ".midora-catalog.json";
        await MessageDialog.ShowAsync(
            this,
            $"Placeholder export: '{fileName}' would be written. The Avalonia port has no catalog exchange service.",
            "Export Instrument Catalog Profile",
            MessageDialogButtons.Ok,
            MessageDialogIcon.Information);
        HideValidation();
    }

    private void OnImportProfileClick(object? sender, RoutedEventArgs e)
    {
        if (!TryLeavePendingCatalogEditors(bank: true, program: true, over: false, "importing a Catalog Profile"))
        {
            return;
        }

        OpenImportOverlay();
    }

    private void OpenImportOverlay()
    {
        _importConflicts.Clear();
        _importConflicts.Add(new InstrumentCatalogImportConflictRow("Bank 0.0 / Program 0: 'Acoustic Grand Piano' → 'Piano Grand'"));
        _importConflicts.Add(new InstrumentCatalogImportConflictRow("Bank 0.0 / Program 48: 'String Ensemble 1' → 'Strings'"));
        _importConflicts.Add(new InstrumentCatalogImportConflictRow("Bank 0.0: 'General MIDI' → 'GM Melodic'"));
        _importTargets.Clear();
        _importTargets.Add(new InstrumentCatalogImportTargetOption(null, "New Profile"));
        foreach (InstrumentCatalogProfileRow profile in _profiles)
        {
            _importTargets.Add(new InstrumentCatalogImportTargetOption(profile, profile.DisplayName));
        }

        ImportSourceText.Text =
            "Placeholder import source: 'FluidR3_GM.midora-catalog.json'. The WPF dialog picks the file with a file dialog and previews merge conflicts before committing.";
        ImportTargetBox.SelectedIndex = 0;
        UpdateImportPreview();
        SetOverlayState(ImportOverlay, visible: true);
    }

    private void OnImportTargetChanged(object? sender, SelectionChangedEventArgs e) => UpdateImportPreview();

    private void UpdateImportPreview()
    {
        InstrumentCatalogImportTargetOption? target = ImportTargetBox.SelectedItem as InstrumentCatalogImportTargetOption;
        ImportSummaryText.Text = _importConflicts.Count == 0
            ? "No conflicting names. The imported Profile is added unchanged."
            : $"{_importConflicts.Count} conflicting name(s) will be renamed in the existing catalog. Target: {target?.DisplayName ?? "New Profile"}.";
        ReplaceImportedProfileButton.IsEnabled = target?.Profile is not null;
    }

    private void OnReplaceImportedProfileClick(object? sender, RoutedEventArgs e)
    {
        InstrumentCatalogImportTargetOption? target = ImportTargetBox.SelectedItem as InstrumentCatalogImportTargetOption;
        InstrumentCatalogProfileRow candidate = CreateImportedCandidate();
        if (target?.Profile is { } existing)
        {
            existing.ReplaceBanksFrom(candidate);
            ProfileList.SelectedItem = existing;
            LoadProfileEditor(existing);
        }
        else
        {
            _profiles.Add(candidate);
            ProfileList.SelectedItem = candidate;
            LoadProfileEditor(candidate);
        }

        CloseImportOverlay();
        HideValidation();
    }

    private void OnMergeImportedProfileClick(object? sender, RoutedEventArgs e)
    {
        InstrumentCatalogImportTargetOption? target = ImportTargetBox.SelectedItem as InstrumentCatalogImportTargetOption;
        InstrumentCatalogProfileRow candidate = CreateImportedCandidate();
        if (target?.Profile is { } existing)
        {
            existing.DisplayName = GetUniqueProfileName($"{candidate.DisplayName} (Merged)");
            existing.ReplaceBanksFrom(candidate);
            ProfileList.SelectedItem = existing;
            LoadProfileEditor(existing);
        }
        else
        {
            _profiles.Add(candidate);
            ProfileList.SelectedItem = candidate;
            LoadProfileEditor(candidate);
        }

        CloseImportOverlay();
        HideValidation();
    }

    private static InstrumentCatalogProfileRow CreateImportedCandidate()
    {
        InstrumentCatalogProfileRow profile = new(
            Guid.NewGuid().ToString("N"),
            "FluidR3_GM",
            enabled: true,
            sourceLabel: "Imported: FluidR3_GM");
        profile.Banks.Add(new InstrumentCatalogBankRow(0, 0, "GM Melodic")
        {
            Programs =
            {
                new InstrumentCatalogProgramRow(0, "Piano Grand"),
                new InstrumentCatalogProgramRow(48, "Strings")
            }
        });
        return profile;
    }

    private void OnCancelImportClick(object? sender, RoutedEventArgs e) => CloseImportOverlay();

    private void CloseImportOverlay()
    {
        SetOverlayState(ImportOverlay, visible: false);
        _importConflicts.Clear();
        _importTargets.Clear();
    }

    private void OnScanSf2Click(object? sender, RoutedEventArgs e) => OpenScanOverlay(null);

    private void OnRescanSf2Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile || !profile.SourceLabel.StartsWith("Imported:", StringComparison.Ordinal))
        {
            ShowValidation("Select an imported SF2 Profile to rescan.");
            return;
        }

        OpenScanOverlay(profile);
    }

    private void OpenScanOverlay(InstrumentCatalogProfileRow? rescanTarget)
    {
        _rescanProfile = rescanTarget;
        _scanBanks.Clear();
        _scanBanks.Add(new InstrumentCatalogScanBankRow(0, 128) { TargetMsbText = "0", TargetLsbText = "0" });
        _scanBanks.Add(new InstrumentCatalogScanBankRow(1, 2) { TargetMsbText = "0", TargetLsbText = "1" });
        ScanSourceText.Text = rescanTarget is null
            ? "Placeholder scan source: Application Preferences SoundFont list entry #1 (FluidR3_GM.sf2). The WPF dialog streams the SF2 phdr metadata and never reads samples."
            : $"Rescanning '{rescanTarget.DisplayName}' from Application Preferences SoundFont list entry #1 (FluidR3_GM.sf2).";
        ScanProfileNameBox.Text = rescanTarget?.DisplayName ?? GetUniqueProfileName("FluidR3_GM");
        ScanStatusText.Text = "Scanned 130 presets in 2 raw banks.";
        ApplyScanButton.IsDefault = true;
        SetOverlayState(ScanOverlay, visible: true);
        ScanProfileNameBox.Focus();
    }

    private void OnApplyScanClick(object? sender, RoutedEventArgs e)
    {
        string profileName;
        try
        {
            profileName = NormalizeRequiredName(ScanProfileNameBox.Text, "Profile name");
        }
        catch (ArgumentException exception)
        {
            ScanStatusText.Text = exception.Message;
            return;
        }

        List<(ushort RawBank, byte Msb, byte Lsb, int PresetCount)> projections = [];
        foreach (InstrumentCatalogScanBankRow bank in _scanBanks)
        {
            if (!TryParseMidiByte(bank.TargetMsbText, out byte msb)
                || !TryParseMidiByte(bank.TargetLsbText, out byte lsb))
            {
                ScanStatusText.Text = $"Raw bank {bank.RawBank} requires target Bank MSB and LSB values from 0 through 127.";
                return;
            }

            projections.Add((bank.RawBank, msb, lsb, bank.PresetCount));
        }

        InstrumentCatalogProfileRow replacement = new(
            _rescanProfile?.ProfileId ?? Guid.NewGuid().ToString("N"),
            profileName,
            _rescanProfile?.Enabled ?? true,
            "Imported: " + profileName);
        foreach ((ushort rawBank, byte msb, byte lsb, int presetCount) in projections)
        {
            InstrumentCatalogBankRow bank = new(msb, lsb, $"Raw bank {rawBank}");
            for (int index = 0; index < Math.Min(presetCount, 6); index++)
            {
                string programName = msb == 0 && lsb == 0
                    ? GeneralMidiNames[index]
                    : $"Raw {rawBank} preset {index}";
                bank.Programs.Add(new InstrumentCatalogProgramRow((byte)index, programName));
            }

            replacement.Banks.Add(bank);
        }

        if (_rescanProfile is { } existing)
        {
            int index = _profiles.IndexOf(existing);
            if (index >= 0)
            {
                _profiles[index] = replacement;
            }
        }
        else
        {
            _profiles.Add(replacement);
        }

        CloseScanOverlay();
        RestoreSelection(ProfileList, replacement);
        LoadProfileEditor(replacement);
        ProfileList.ScrollIntoView(replacement);
        HideValidation();
    }

    private void OnCancelScanClick(object? sender, RoutedEventArgs e) => CloseScanOverlay();

    private void CloseScanOverlay()
    {
        SetOverlayState(ScanOverlay, visible: false);
        _scanBanks.Clear();
        _rescanProfile = null;
        ApplyScanButton.IsDefault = false;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        HideValidation();
        if (HasPendingCatalogItemEdits())
        {
            ShowValidation(
                "Apply or restore the pending Bank, Program, or User Override field edits before saving the Catalog.");
            return;
        }

        if (_profiles.Count == 0)
        {
            ShowValidation("Create or import at least one Catalog Profile before saving the Catalog.");
            return;
        }

        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ImportOverlay.IsVisible)
            {
                CloseImportOverlay();
                e.Handled = true;
                return;
            }

            if (ScanOverlay.IsVisible)
            {
                CloseScanOverlay();
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    private void SetOverlayState(Control overlay, bool visible)
    {
        overlay.IsVisible = visible;
        bool editorEnabled = !visible;
        CatalogTitleBar.IsEnabled = editorEnabled;
        CatalogCloseButton.IsEnabled = editorEnabled;
        CatalogHeader.IsEnabled = editorEnabled;
        CatalogTabs.IsEnabled = editorEnabled;
        CatalogFooter.IsEnabled = editorEnabled;
        ValidationBorder.IsEnabled = editorEnabled;
        CatalogOkButton.IsDefault = editorEnabled;
        if (!visible)
        {
            CatalogTabs.Focus();
        }
    }

    private void OnCatalogTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _suppressNavigation || !ReferenceEquals(sender, CatalogTabs))
        {
            return;
        }

        TabItem? requested = CatalogTabs.SelectedItem as TabItem;
        if (ReferenceEquals(requested, _activeCatalogTab))
        {
            return;
        }

        if (!TryLeavePendingCatalogEditors(bank: true, program: true, over: true, "switching Catalog tabs"))
        {
            RestoreCatalogTab();
            return;
        }

        _activeCatalogTab = requested;
    }

    private bool HasPendingCatalogItemEdits() => HasPendingBankEdits() || HasPendingProgramEdits() || HasPendingOverrideEdits();

    private bool HasPendingBankEdits() => _addingBank
        ? !string.Equals(BankMsbBox.Text, "0", StringComparison.Ordinal)
            || !string.Equals(BankLsbBox.Text, "0", StringComparison.Ordinal)
            || (BankNameBox.Text ?? string.Empty).Length != 0
        : _activeBank is not null
            && (!string.Equals(
                    BankMsbBox.Text,
                    _activeBank.BankMsb.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                || !string.Equals(
                    BankLsbBox.Text,
                    _activeBank.BankLsb.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                || !string.Equals(BankNameBox.Text, _activeBank.DisplayName ?? string.Empty, StringComparison.Ordinal));

    private bool HasPendingProgramEdits() => _addingProgram
        ? !string.Equals(ProgramNumberBox.Text, "0", StringComparison.Ordinal)
            || (ProgramNameBox.Text ?? string.Empty).Length != 0
        : _activeProgram is not null
            && (!string.Equals(
                    ProgramNumberBox.Text,
                    _activeProgram.Program.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                || !string.Equals(ProgramNameBox.Text, _activeProgram.DisplayName, StringComparison.Ordinal));

    private bool HasPendingOverrideEdits() => _addingOverride
        ? !string.Equals(OverrideMsbBox.Text, "0", StringComparison.Ordinal)
            || !string.Equals(OverrideLsbBox.Text, "0", StringComparison.Ordinal)
            || !string.Equals(OverrideProgramBox.Text, "0", StringComparison.Ordinal)
            || (OverrideBankNameBox.Text ?? string.Empty).Length != 0
            || (OverrideProgramNameBox.Text ?? string.Empty).Length != 0
        : _activeOverride is not null
            && (!string.Equals(
                    OverrideMsbBox.Text,
                    _activeOverride.BankMsb.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                || !string.Equals(
                    OverrideLsbBox.Text,
                    _activeOverride.BankLsb.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                || !string.Equals(
                    OverrideProgramBox.Text,
                    _activeOverride.Program.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                || !string.Equals(OverrideBankNameBox.Text, _activeOverride.BankDisplayName ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(OverrideProgramNameBox.Text, _activeOverride.ProgramDisplayName ?? string.Empty, StringComparison.Ordinal));

    private bool TryLeavePendingCatalogEditors(bool bank, bool program, bool over, string action)
    {
        if (bank && HasPendingBankEdits())
        {
            return BlockCatalogNavigation("Bank", action, BankMsbBox);
        }

        if (program && HasPendingProgramEdits())
        {
            return BlockCatalogNavigation("Program", action, ProgramNumberBox);
        }

        if (over && HasPendingOverrideEdits())
        {
            return BlockCatalogNavigation("User Override", action, OverrideMsbBox);
        }

        return true;
    }

    private bool BlockCatalogNavigation(string editorName, string action, TextBox focusTarget)
    {
        ShowValidation($"Apply or restore the pending {editorName} fields before {action}.");
        focusTarget.Focus();
        focusTarget.SelectAll();
        return false;
    }

    private void SelectProfileForEditing(InstrumentCatalogProfileRow? profile)
    {
        RestoreSelection(ProfileList, profile);
        LoadProfileEditor(profile);
    }

    private void RestoreSelection(ListBox list, object? item)
    {
        bool wasSuppressed = _suppressNavigation;
        _suppressNavigation = true;
        try
        {
            list.SelectedItem = item;
        }
        finally
        {
            _suppressNavigation = wasSuppressed;
        }
    }

    private void RestoreCatalogTab()
    {
        bool wasSuppressed = _suppressNavigation;
        _suppressNavigation = true;
        try
        {
            CatalogTabs.SelectedItem = _activeCatalogTab;
        }
        finally
        {
            _suppressNavigation = wasSuppressed;
        }
    }

    private bool TryMidiByte(TextBox box, string label, out byte value)
    {
        if (TryParseMidiByte(box.Text, out value))
        {
            return true;
        }

        value = 0;
        ShowValidation($"{label} must be an integer from 0 through 127.");
        return false;
    }

    private static bool TryParseMidiByte(string? text, out byte value) =>
        byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= 127;

    private static string NormalizeRequiredName(string? value, string label)
    {
        string result = (value ?? string.Empty).Trim().Normalize();
        if (result.Length == 0 || result.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentException($"{label} must contain 1 through {MaximumDisplayNameLength} characters.");
        }

        if (result.Any(char.IsControl))
        {
            throw new ArgumentException($"{label} cannot contain control characters.");
        }

        return result;
    }

    private static string? NormalizeOptionalName(string? value)
    {
        string result = (value ?? string.Empty).Trim().Normalize();
        return result.Length == 0 ? null : NormalizeRequiredName(result, "Display name");
    }

    private string GetUniqueProfileName(string baseName)
    {
        string result = baseName;
        int suffix = 2;
        HashSet<string> names = _profiles.Select(value => value.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains(result))
        {
            result = $"{baseName} {suffix++}";
        }

        return result;
    }

    private static string SanitizeExportFileName(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return result.Length == 0 ? "Instrument Catalog" : result;
    }

    private static void Reposition<T>(ObservableCollection<T> values, T edited, Comparison<T> comparison)
        where T : class
    {
        int source = values.IndexOf(edited);
        if (source < 0)
        {
            throw new InvalidOperationException("The edited catalog item is not in its collection.");
        }

        int target = values.Count(value => !ReferenceEquals(value, edited) && comparison(value, edited) < 0);
        if (source != target)
        {
            values.Move(source, target);
        }
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationBorder.IsVisible = true;
    }

    private void HideValidation()
    {
        ValidationText.Text = string.Empty;
        ValidationBorder.IsVisible = false;
    }
}

public abstract class InstrumentCatalogRowBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class InstrumentCatalogProfileRow : InstrumentCatalogRowBase
{
    private string _displayName;
    private bool _enabled;

    public InstrumentCatalogProfileRow(string profileId, string displayName, bool enabled, string sourceLabel)
    {
        ProfileId = profileId;
        _displayName = displayName;
        _enabled = enabled;
        SourceLabel = sourceLabel;
    }

    public string ProfileId { get; }

    public string SourceLabel { get; }

    public ObservableCollection<InstrumentCatalogBankRow> Banks { get; } = [];

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (string.Equals(_displayName, value, StringComparison.Ordinal))
            {
                return;
            }

            _displayName = value;
            Raise(nameof(DisplayName));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Raise(nameof(Enabled));
        }
    }

    public void ReplaceBanksFrom(InstrumentCatalogProfileRow source)
    {
        Banks.Clear();
        foreach (InstrumentCatalogBankRow bank in source.Banks)
        {
            InstrumentCatalogBankRow copy = new(bank.BankMsb, bank.BankLsb, bank.DisplayName);
            foreach (InstrumentCatalogProgramRow program in bank.Programs)
            {
                copy.Programs.Add(new InstrumentCatalogProgramRow(program.Program, program.DisplayName));
            }

            Banks.Add(copy);
        }
    }
}

public sealed class InstrumentCatalogBankRow : InstrumentCatalogRowBase
{
    public InstrumentCatalogBankRow(byte bankMsb, byte bankLsb, string? displayName)
    {
        BankMsb = bankMsb;
        BankLsb = bankLsb;
        DisplayName = displayName;
    }

    public byte BankMsb { get; private set; }

    public byte BankLsb { get; private set; }

    public string? DisplayName { get; private set; }

    public ObservableCollection<InstrumentCatalogProgramRow> Programs { get; init; } = [];

    public string DisplayText =>
        $"MSB {BankMsb} / LSB {BankLsb} · {DisplayName ?? "Unnamed Bank"} · {Programs.Count:N0} program(s)";

    public void Update(byte bankMsb, byte bankLsb, string? displayName)
    {
        BankMsb = bankMsb;
        BankLsb = bankLsb;
        DisplayName = displayName;
        Raise(nameof(DisplayText));
    }
}

public sealed class InstrumentCatalogProgramRow : InstrumentCatalogRowBase
{
    public InstrumentCatalogProgramRow(byte program, string displayName)
    {
        Program = program;
        DisplayName = displayName;
    }

    public byte Program { get; private set; }

    public string DisplayName { get; private set; }

    public string DisplayText => $"Program {Program} · {DisplayName}";

    public void Update(byte program, string displayName)
    {
        Program = program;
        DisplayName = displayName;
        Raise(nameof(DisplayText));
    }
}

public sealed class InstrumentCatalogOverrideRow : InstrumentCatalogRowBase
{
    public InstrumentCatalogOverrideRow(byte bankMsb, byte bankLsb, byte program, string? bankDisplayName, string? programDisplayName)
    {
        BankMsb = bankMsb;
        BankLsb = bankLsb;
        Program = program;
        BankDisplayName = bankDisplayName;
        ProgramDisplayName = programDisplayName;
    }

    public byte BankMsb { get; private set; }

    public byte BankLsb { get; private set; }

    public byte Program { get; private set; }

    public string? BankDisplayName { get; private set; }

    public string? ProgramDisplayName { get; private set; }

    public string DisplayText =>
        $"MSB {BankMsb} / LSB {BankLsb} / Program {Program} · {ProgramDisplayName ?? BankDisplayName ?? "Unnamed"}";

    public void Update(byte bankMsb, byte bankLsb, byte program, string? bankDisplayName, string? programDisplayName)
    {
        BankMsb = bankMsb;
        BankLsb = bankLsb;
        Program = program;
        BankDisplayName = bankDisplayName;
        ProgramDisplayName = programDisplayName;
        Raise(nameof(DisplayText));
    }
}

public sealed class InstrumentCatalogScanBankRow
{
    public InstrumentCatalogScanBankRow(ushort rawBank, int presetCount)
    {
        RawBank = rawBank;
        PresetCount = presetCount;
    }

    public ushort RawBank { get; }

    public int PresetCount { get; }

    public string PresetCountText => PresetCount == 1 ? "1 preset" : $"{PresetCount:N0} presets";

    public string TargetMsbText { get; set; } = string.Empty;

    public string TargetLsbText { get; set; } = string.Empty;
}

public sealed record InstrumentCatalogImportConflictRow(string DisplayText);

public sealed record InstrumentCatalogImportTargetOption(InstrumentCatalogProfileRow? Profile, string DisplayName);
