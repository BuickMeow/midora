using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Midora.Application;
using Midora.Audio;
using Midora.Domain;
using Midora.Session.Audio;

namespace Midora.Avalonia.Windows;

public partial class ApplicationPreferencesDialog : Window
{
    private const decimal BytesPerGibibyte = 1024m * 1024m * 1024m;
    private const string SystemDefaultDeviceLabel = "System Default";

    private readonly ApplicationPreferences _original;

    public ApplicationPreferencesDialog()
        : this(preferences: null, initialPage: 0)
    {
    }

    public ApplicationPreferencesDialog(ApplicationPreferences? preferences, int initialPage = 0)
    {
        _original = preferences ?? ApplicationPreferences.Default;
        InitializeComponent();
        StopCursorBox.ItemsSource = Enum.GetValues<StopCursorChoice>();
        LanguageBox.ItemsSource = new[] { "English" };
        SoundFontListBox.ItemsSource = SoundFonts;
        SoundFonts.CollectionChanged += (_, _) => UpdateSelectedSoundFontCount();
        PopulateFromPreferences(_original);
        PreferencesTabs.SelectedIndex = Math.Clamp(initialPage, 0, 2);
        DataContext = this;
        _ = PopulateDevicesAsync();
    }

    public ObservableCollection<SoundFontRow> SoundFonts { get; } = [];

    private readonly List<string> _deviceIds = [];

    /// <summary>
    /// Result semantics: null means the dialog was cancelled; a non-null value means the user
    /// confirmed Apply. Avalonia has no DialogResult, so Close() is the signal.
    /// </summary>
    public ApplicationPreferences? Preferences { get; private set; }

    public enum StopCursorChoice
    {
        ReturnToPlaybackStart,
        StayAtStoppedTick
    }

    public sealed class SoundFontRow : INotifyPropertyChanged
    {
        private bool _enabled;
        private bool _hasTarget;
        private string _bankMsbText;
        private string _bankLsbText;
        private string _programText;

        public SoundFontRow(
            string fullPath,
            bool enabled,
            bool hasTarget,
            byte bankMsb,
            byte bankLsb,
            byte program,
            SoundFontEntryId? entryId = null)
        {
            EntryId = entryId;
            FullPath = fullPath;
            FileName = Path.GetFileName(fullPath);
            IsSfz = string.Equals(Path.GetExtension(fullPath), ".sfz", StringComparison.OrdinalIgnoreCase);
            _enabled = enabled;
            _hasTarget = IsSfz || hasTarget;
            _bankMsbText = bankMsb.ToString(CultureInfo.InvariantCulture);
            _bankLsbText = bankLsb.ToString(CultureInfo.InvariantCulture);
            _programText = program.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Stable application SoundFont entry identity; null for a newly added row.</summary>
        public SoundFontEntryId? EntryId { get; }

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

        /// <summary>
        /// Converts the row into a persisted preference. SFZ always requires a target; SF2 keeps
        /// one only when the user enabled Target mapping and all three fields parse.
        /// </summary>
        public ApplicationSoundFontPreference? ToPreference()
        {
            SoundFontTarget? target = null;
            if (HasTarget)
            {
                if (!TryByte(BankMsbText, out byte msb)
                    || !TryByte(BankLsbText, out byte lsb)
                    || !TryByte(ProgramText, out byte program))
                {
                    return null;
                }

                target = new SoundFontTarget(msb, lsb, program);
            }
            else if (IsSfz)
            {
                return null;
            }

            return EntryId is { } entryId
                ? new ApplicationSoundFontPreference(entryId, FullPath, Enabled, target)
                : new ApplicationSoundFontPreference(FullPath, Enabled, target);
        }

        private static bool TryByte(string text, out byte value) =>
            byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value <= 127;

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

    private void PopulateFromPreferences(ApplicationPreferences preferences)
    {
        RealtimeAudioPreferences audio = preferences.RealtimeAudio;
        PlaybackPreferences playback = preferences.Playback;
        PlaybackMasterVolumeBox.Text = playback.MasterVolumeDecibels.ToString(
            CultureInfo.InvariantCulture);
        PlaybackLimiterBox.IsChecked = playback.LimiterEnabled;
        StopCursorBox.SelectedItem = playback.StopCursorBehavior == StopCursorBehavior.StayAtStoppedTick
            ? StopCursorChoice.StayAtStoppedTick
            : StopCursorChoice.ReturnToPlaybackStart;
        LanguageBox.SelectedItem = "English";
        EventLaneLinesBox.IsChecked = preferences.Appearance.ShowEventLaneLines;
        DeviceBox.ItemsSource = new[] { SystemDefaultDeviceLabel };
        DeviceBox.SelectedIndex = 0;
        DeviceStatusText.Text = "Enumerating output devices through the audio worker...";
        RenderAheadBox.Text = audio.RenderAheadMilliseconds.ToString(CultureInfo.InvariantCulture);
        DeviceRequestBox.Text = audio.DeviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture);
        VoicesBox.Text = audio.MaximumSampleVoicesPerUnitStream.ToString(CultureInfo.InvariantCulture);
        CacheQuotaBox.Text = (preferences.AudioCache.MaximumReusableBytes / BytesPerGibibyte)
            .ToString("0.##", CultureInfo.InvariantCulture);
        ResetSoundFonts(preferences.SoundFonts);
    }

    private async Task PopulateDevicesAsync()
    {
        try
        {
            IReadOnlyList<FormalAudioOutputDevice> devices =
                await FormalAudioOutputDeviceEnumerator.EnumerateAsync();
            List<string> labels = [SystemDefaultDeviceLabel];
            string? selectedId = _original.RealtimeAudio.PlaybackOutputDeviceId;
            int selectedIndex = 0;
            _deviceIds.Clear();
            foreach (FormalAudioOutputDevice device in devices)
            {
                _deviceIds.Add(device.Id);
                labels.Add($"{device.Name} — {device.SampleRate:N0} Hz · {device.ChannelCount} ch");
                if (string.Equals(device.Id, selectedId, StringComparison.Ordinal))
                {
                    selectedIndex = labels.Count - 1;
                }
            }

            DeviceBox.ItemsSource = labels;
            DeviceBox.SelectedIndex = selectedIndex;
            DeviceStatusText.Text = devices.Count == 0
                ? "The audio worker reported no enabled output devices."
                : "Realtime audio is generated at the selected device's actual sample rate.";
        }
        catch (Exception exception)
        {
            DeviceStatusText.Text = "Output devices are unavailable: " + exception.Message;
        }
    }

    private void ResetSoundFonts(IReadOnlyList<ApplicationSoundFontPreference> preferences)
    {
        foreach (SoundFontRow row in SoundFonts)
        {
            row.PropertyChanged -= OnSoundFontRowPropertyChanged;
        }

        SoundFonts.Clear();
        foreach (ApplicationSoundFontPreference preference in preferences)
        {
            AddSoundFontRow(new SoundFontRow(
                preference.Path,
                preference.Enabled,
                preference.Target is not null,
                preference.Target?.BankMsb ?? 0,
                preference.Target?.BankLsb ?? 0,
                preference.Target?.Program ?? 0,
                preference.EntryId));
        }

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

    private async void OnAddSoundFontsClick(object? sender, RoutedEventArgs e)
    {
        FilePickerOpenOptions options = new()
        {
            Title = "Add SoundFonts",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("SoundFonts")
                {
                    Patterns = ["*.sf2", "*.sfz"],
                    AppleUniformTypeIdentifiers = ["public.data"],
                    MimeTypes = ["application/octet-stream"]
                },
                new FilePickerFileType("SoundFont 2") { Patterns = ["*.sf2"] },
                new FilePickerFileType("SFZ Instrument") { Patterns = ["*.sfz"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        };
        string? lastDirectory = SoundFonts.Count == 0
            ? null
            : Path.GetDirectoryName(SoundFonts[^1].FullPath);
        if (!string.IsNullOrEmpty(lastDirectory) && Directory.Exists(lastDirectory))
        {
            options.SuggestedStartLocation =
                await StorageProvider.TryGetFolderFromPathAsync(lastDirectory);
        }

        IReadOnlyList<IStorageFile> selected = await StorageProvider.OpenFilePickerAsync(options);
        SoundFontRow? lastAdded = null;
        foreach (IStorageFile file in selected)
        {
            if (file.TryGetLocalPath() is not { } path || string.IsNullOrEmpty(path))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(path);
            if (SoundFonts.Any(row => string.Equals(
                    row.FullPath,
                    fullPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            lastAdded = new SoundFontRow(
                fullPath,
                enabled: true,
                hasTarget: false,
                bankMsb: 0,
                bankLsb: 0,
                program: 0);
            AddSoundFontRow(lastAdded);
        }

        if (lastAdded is not null)
        {
            SoundFontListBox.SelectedItem = lastAdded;
            UpdateSelectedSoundFontCount();
        }
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
        PopulateFromPreferences(ApplicationPreferences.Default);
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

        List<ApplicationSoundFontPreference> soundFonts = [];
        foreach (SoundFontRow row in SoundFonts)
        {
            if (row.ToPreference() is not { } preference)
            {
                ShowValidation(
                    $"SoundFont '{row.FileName}' needs a complete Bank MSB/LSB/Program target in 0-127.");
                _ = SoundFontListBox.Focus();
                return;
            }

            soundFonts.Add(preference.Normalize());
        }

        string? deviceId = DeviceBox.SelectedIndex <= 0
            ? null
            : DeviceIdForSelection(DeviceBox.SelectedIndex);
        long maximumReusableBytes = checked((long)(quotaGib * BytesPerGibibyte));
        Preferences = _original with
        {
            RealtimeAudio = new RealtimeAudioPreferences(
                deviceId,
                renderAhead,
                deviceRequest,
                voices),
            AudioCache = _original.AudioCache with { MaximumReusableBytes = maximumReusableBytes },
            SoundFonts = soundFonts,
            Playback = new PlaybackPreferences(
                masterVolumeDecibels,
                PlaybackLimiterBox.IsChecked == true,
                stopCursorBehavior == StopCursorChoice.StayAtStoppedTick
                    ? StopCursorBehavior.StayAtStoppedTick
                    : StopCursorBehavior.ReturnToPlaybackStart),
            Appearance = _original.Appearance with
            {
                ShowEventLaneLines = EventLaneLinesBox.IsChecked == true
            }
        };
        Close();
    }

    private string? DeviceIdForSelection(int selectedIndex) =>
        selectedIndex > 0 && selectedIndex <= _deviceIds.Count
            ? _deviceIds[selectedIndex - 1]
            : null;

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
