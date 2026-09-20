using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class ApplicationPreferencesDialog : Window
{
    private const decimal BytesPerGibibyte = 1024m * 1024m * 1024m;

    public ApplicationPreferencesDialog()
        : this(0)
    {
    }

    public ApplicationPreferencesDialog(int initialPage = 0)
    {
        InitializeComponent();
        StopCursorBox.ItemsSource = Enum.GetValues<StopCursorChoice>();
        LanguageBox.ItemsSource = new[] { "English" };
        SoundFontListBox.ItemsSource = SoundFonts;
        SoundFonts.CollectionChanged += (_, _) => UpdateSelectedSoundFontCount();
        PopulateDefaults();
        PreferencesTabs.SelectedIndex = Math.Clamp(initialPage, 0, 2);
        DataContext = this;
    }

    public ObservableCollection<SoundFontRow> SoundFonts { get; } = [];

    /// <summary>
    /// Result semantics: null means the dialog was cancelled; a non-null value means
    /// the user confirmed Apply. Avalonia has no DialogResult, so Close() is the signal.
    /// </summary>
    public PreferencesDraft? Result { get; private set; }

    public enum StopCursorChoice
    {
        ReturnToPlaybackStart,
        StayAtStoppedTick
    }

    public sealed record SoundFontSummary(
        string FullPath,
        bool Enabled,
        bool HasTarget,
        string BankMsbText,
        string BankLsbText,
        string ProgramText);

    public sealed record PreferencesDraft(
        string? PlaybackOutputDeviceId,
        int RenderAheadMilliseconds,
        int DeviceBufferRequestMilliseconds,
        int MaximumSampleVoicesPerUnitStream,
        decimal MaximumReusableCacheGibibytes,
        double PlaybackMasterVolumeDecibels,
        bool LimiterEnabled,
        StopCursorChoice StopCursorBehavior,
        string Language,
        bool ShowEventLaneLines,
        IReadOnlyList<SoundFontSummary> ConfiguredSoundFonts);

    public sealed class SoundFontRow : INotifyPropertyChanged
    {
        private bool _enabled;
        private bool _hasTarget;
        private string _bankMsbText;
        private string _bankLsbText;
        private string _programText;

        public SoundFontRow(string fullPath, bool enabled, bool hasTarget, byte bankMsb, byte bankLsb, byte program)
        {
            FullPath = fullPath;
            FileName = Path.GetFileName(fullPath);
            IsSfz = string.Equals(Path.GetExtension(fullPath), ".sfz", StringComparison.OrdinalIgnoreCase);
            _enabled = enabled;
            _hasTarget = IsSfz || hasTarget;
            _bankMsbText = bankMsb.ToString(CultureInfo.InvariantCulture);
            _bankLsbText = bankLsb.ToString(CultureInfo.InvariantCulture);
            _programText = program.ToString(CultureInfo.InvariantCulture);
        }

        public string FullPath { get; }

        public string FileName { get; }

        public bool IsSfz { get; }

        public bool TargetOptional => !IsSfz;

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

        public bool HasTarget
        {
            get => _hasTarget;
            set
            {
                bool normalized = IsSfz || value;
                if (_hasTarget == normalized)
                {
                    return;
                }

                _hasTarget = normalized;
                Raise(nameof(HasTarget));
                Raise(nameof(ResolvedTargetText));
            }
        }

        public string BankMsbText
        {
            get => _bankMsbText;
            set => SetText(ref _bankMsbText, value, nameof(BankMsbText));
        }

        public string BankLsbText
        {
            get => _bankLsbText;
            set => SetText(ref _bankLsbText, value, nameof(BankLsbText));
        }

        public string ProgramText
        {
            get => _programText;
            set => SetText(ref _programText, value, nameof(ProgramText));
        }

        public string ResolvedTargetText => !HasTarget
            ? "All original presets; no single target address."
            : $"Bank {_bankMsbText}:{_bankLsbText}, Program {_programText} (catalog not scanned)";

        public SoundFontSummary ToSummary() =>
            new(FullPath, Enabled, HasTarget, BankMsbText, BankLsbText, ProgramText);

        private void SetText(ref string field, string? value, string propertyName)
        {
            value ??= string.Empty;
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value;
            Raise(propertyName);
            Raise(nameof(ResolvedTargetText));
        }

        private void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void PopulateDefaults()
    {
        PlaybackMasterVolumeBox.Text = (-0.1).ToString(CultureInfo.InvariantCulture);
        PlaybackLimiterBox.IsChecked = true;
        StopCursorBox.SelectedItem = StopCursorChoice.ReturnToPlaybackStart;
        LanguageBox.SelectedItem = "English";
        EventLaneLinesBox.IsChecked = true;
        DeviceBox.ItemsSource = new[] { "System Default", "Built-in Audio — 48,000 Hz · 2 ch" };
        DeviceBox.SelectedIndex = 0;
        DeviceStatusText.Text =
            "Device enumeration requires the formal audio worker; placeholder endpoints are shown.";
        RenderAheadBox.Text = 100.ToString(CultureInfo.InvariantCulture);
        DeviceRequestBox.Text = 50.ToString(CultureInfo.InvariantCulture);
        VoicesBox.Text = 500.ToString(CultureInfo.InvariantCulture);
        CacheQuotaBox.Text = "16";
        ResetSoundFonts();
    }

    private void ResetSoundFonts()
    {
        foreach (SoundFontRow row in SoundFonts)
        {
            row.PropertyChanged -= OnSoundFontRowPropertyChanged;
        }

        SoundFonts.Clear();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddSoundFontRow(new SoundFontRow(
            Path.Combine(userProfile, "SoundFonts", "GeneralUser GS.sf2"),
            enabled: true,
            hasTarget: false,
            bankMsb: 0,
            bankLsb: 0,
            program: 0));
        AddSoundFontRow(new SoundFontRow(
            Path.Combine(userProfile, "SoundFonts", "MuseScore_General.sf2"),
            enabled: true,
            hasTarget: true,
            bankMsb: 0,
            bankLsb: 0,
            program: 0));
        AddSoundFontRow(new SoundFontRow(
            Path.Combine(userProfile, "SoundFonts", "Salamander Grand Piano.sfz"),
            enabled: false,
            hasTarget: true,
            bankMsb: 0,
            bankLsb: 0,
            program: 0));
        UpdateSelectedSoundFontCount();
    }

    private void AddSoundFontRow(SoundFontRow row)
    {
        row.PropertyChanged += OnSoundFontRowPropertyChanged;
        SoundFonts.Add(row);
    }

    private void OnSoundFontRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SoundFontRow.Enabled), StringComparison.Ordinal))
        {
            UpdateSelectedSoundFontCount();
        }
    }

    private void UpdateSelectedSoundFontCount()
    {
        int count = SoundFonts.Count(item => item.Enabled);
        SoundFontCountText.Text = count == 1
            ? "1 SoundFont selected"
            : $"{count} SoundFonts selected";
    }

    private void OnAddSoundFontsClick(object? sender, RoutedEventArgs e)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var row = new SoundFontRow(
            Path.Combine(userProfile, "SoundFonts", $"Added SoundFont {SoundFonts.Count + 1}.sf2"),
            enabled: true,
            hasTarget: false,
            bankMsb: 0,
            bankLsb: 0,
            program: 0);
        AddSoundFontRow(row);
        SoundFontListBox.SelectedItem = row;
    }

    private void OnRemoveSoundFontClick(object? sender, RoutedEventArgs e)
    {
        if (SoundFontListBox.SelectedItem is not SoundFontRow selected)
        {
            return;
        }

        int index = SoundFonts.IndexOf(selected);
        selected.PropertyChanged -= OnSoundFontRowPropertyChanged;
        SoundFonts.RemoveAt(index);
        if (SoundFonts.Count != 0)
        {
            SoundFontListBox.SelectedIndex = Math.Min(index, SoundFonts.Count - 1);
        }
    }

    private void OnMoveSoundFontUpClick(object? sender, RoutedEventArgs e) => MoveSelectedSoundFont(-1);

    private void OnMoveSoundFontDownClick(object? sender, RoutedEventArgs e) => MoveSelectedSoundFont(1);

    private void MoveSelectedSoundFont(int delta)
    {
        if (SoundFontListBox.SelectedItem is not SoundFontRow selected)
        {
            return;
        }

        int source = SoundFonts.IndexOf(selected);
        int target = source + delta;
        if (target < 0 || target >= SoundFonts.Count)
        {
            return;
        }

        SoundFonts.Move(source, target);
        SoundFontListBox.SelectedItem = selected;
    }

    private void OnRestoreDefaultsClick(object? sender, RoutedEventArgs e)
    {
        PopulateDefaults();
        DeviceStatusText.Text = "Defaults restored in the form. Select Apply to persist them.";
        HideValidation();
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        HideValidation();
        if (!TryInt(RenderAheadBox.Text, "Render-Ahead", 20, 2_000, out int renderAhead)
            || !TryInt(DeviceRequestBox.Text, "Device Buffer Request", 5, 200, out int deviceRequest)
            || !TryInt(
                VoicesBox.Text,
                "Realtime Maximum Sample Voices per Unit Stream",
                1,
                16_777_216,
                out int voices))
        {
            return;
        }

        if (!double.TryParse(
                PlaybackMasterVolumeBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double masterVolumeDecibels)
            || !double.IsFinite(masterVolumeDecibels)
            || masterVolumeDecibels is < -float.MaxValue or > 0)
        {
            ShowValidation("Playback Master Volume must be a finite dB value no greater than 0.");
            _ = PlaybackMasterVolumeBox.Focus();
            return;
        }

        if (StopCursorBox.SelectedItem is not StopCursorChoice stopCursorBehavior)
        {
            ShowValidation("Select a Stop Cursor Behavior.");
            _ = StopCursorBox.Focus();
            return;
        }

        if (!decimal.TryParse(
                CacheQuotaBox.Text,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal quotaGib)
            || quotaGib < 0
            || quotaGib > long.MaxValue / BytesPerGibibyte)
        {
            ShowValidation(
                "Maximum Reusable Audio Cache must be a non-negative GiB value within the Int64 byte range.");
            _ = CacheQuotaBox.Focus();
            return;
        }

        Result = new PreferencesDraft(
            DeviceBox.SelectedIndex <= 0 ? null : DeviceBox.SelectedItem as string,
            renderAhead,
            deviceRequest,
            voices,
            quotaGib,
            masterVolumeDecibels,
            PlaybackLimiterBox.IsChecked == true,
            stopCursorBehavior,
            LanguageBox.SelectedItem as string ?? "English",
            EventLaneLinesBox.IsChecked == true,
            SoundFonts.Select(row => row.ToSummary()).ToArray());
        Close();
    }

    private bool TryInt(string? text, string label, int minimum, int maximum, out int result)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result >= minimum
            && result <= maximum)
        {
            return true;
        }

        ShowValidation($"{label} must be an integer from {minimum:N0} through {maximum:N0}.");
        return false;
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

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
