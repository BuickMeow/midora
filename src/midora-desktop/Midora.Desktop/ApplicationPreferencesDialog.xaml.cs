using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Midora.Application;

namespace Midora.Desktop;

public partial class ApplicationPreferencesDialog : Window
{
    private const decimal BytesPerGibibyte = 1024m * 1024m * 1024m;
    private readonly ApplicationPreferences _initial;
    private readonly ObservableCollection<SoundFontDraftItem> _soundFonts = [];

    public ApplicationPreferencesDialog(ApplicationPreferences initial)
    {
        _initial = initial ?? throw new ArgumentNullException(nameof(initial));
        InitializeComponent();
        SoundFontListBox.ItemsSource = _soundFonts;
        Populate(initial);
        Loaded += OnLoaded;
    }

    public ApplicationPreferences? Result { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        string? selectedId = (DeviceBox.SelectedItem as DeviceChoice)?.Id;
        DeviceStatusText.Text = "Reading enabled output devices from the formal audio worker…";
        try
        {
            IReadOnlyList<FormalAudioOutputDevice> devices =
                await FormalAudioOutputDeviceEnumerator.EnumerateAsync();
            List<DeviceChoice> choices = [DeviceChoice.SystemDefault];
            choices.AddRange(devices.Select(device => new DeviceChoice(
                device.Id,
                $"{device.Name}{(device.IsSystemDefault ? " — System Default" : string.Empty)} · {device.SampleRate:N0} Hz · {device.ChannelCount} ch")));
            if (selectedId is not null && choices.All(choice => choice.Id != selectedId))
            {
                choices.Add(new(selectedId, "Stored endpoint (currently unavailable)"));
            }
            DeviceBox.ItemsSource = choices;
            DeviceBox.SelectedItem = choices.First(choice => choice.Id == selectedId);
            DeviceStatusText.Text = devices.Count == 0
                ? "No enabled output device was reported. System Default remains selected but playback will be unavailable."
                : $"{devices.Count} enabled output device(s). Input, loopback, disabled, unplugged, and not-present endpoints are excluded by the worker.";
        }
        catch (Exception exception)
        {
            DeviceStatusText.Text = $"Device enumeration is unavailable: {exception.Message}";
        }
    }

    private void Populate(ApplicationPreferences preferences)
    {
        RealtimeAudioPreferences realtime = preferences.RealtimeAudio;
        DeviceChoice current = realtime.PlaybackOutputDeviceId is null
            ? DeviceChoice.SystemDefault
            : new(realtime.PlaybackOutputDeviceId, "Stored endpoint");
        DeviceBox.ItemsSource = new[] { current };
        DeviceBox.SelectedItem = current;
        RenderAheadBox.Text = realtime.RenderAheadMilliseconds.ToString(CultureInfo.InvariantCulture);
        DeviceRequestBox.Text = realtime.DeviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture);
        VoicesBox.Text = realtime.MaximumSampleVoicesPerUnitStream.ToString(CultureInfo.InvariantCulture);
        CacheRootBox.Text = preferences.AudioCache.RootPath;
        CacheQuotaBox.Text = (preferences.AudioCache.MaximumReusableBytes / BytesPerGibibyte)
            .ToString("0.###", CultureInfo.InvariantCulture);
        _soundFonts.Clear();
        foreach (ApplicationSoundFontPreference soundFont in preferences.SoundFonts)
        {
            _soundFonts.Add(new(soundFont.Path, soundFont.Enabled));
        }
    }

    private void OnBrowseCacheClick(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select Local Audio Cache Root",
            InitialDirectory = Directory.Exists(CacheRootBox.Text) ? CacheRootBox.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            CacheRootBox.Text = dialog.FolderName;
        }
    }

    private void OnAddSoundFontsClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Add SoundFonts",
            Filter = "SoundFont 2 (*.sf2)|*.sf2|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
            InitialDirectory = _soundFonts.Count == 0
                ? null
                : Path.GetDirectoryName(_soundFonts[^1].Path)
        };
        if (dialog.ShowDialog(this) == true)
        {
            foreach (string selected in dialog.FileNames)
            {
                string path = Path.GetFullPath(selected);
                if (_soundFonts.Any(value => string.Equals(
                        value.Path,
                        path,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                _soundFonts.Add(new(path, enabled: true));
            }
            SoundFontListBox.SelectedItem = _soundFonts.LastOrDefault();
        }
    }

    private void OnRemoveSoundFontClick(object sender, RoutedEventArgs e)
    {
        if (SoundFontListBox.SelectedItem is not SoundFontDraftItem selected)
        {
            return;
        }
        int index = _soundFonts.IndexOf(selected);
        _soundFonts.RemoveAt(index);
        if (_soundFonts.Count != 0)
        {
            SoundFontListBox.SelectedIndex = Math.Min(index, _soundFonts.Count - 1);
        }
    }

    private void OnMoveSoundFontUpClick(object sender, RoutedEventArgs e) =>
        MoveSelectedSoundFont(-1);

    private void OnMoveSoundFontDownClick(object sender, RoutedEventArgs e) =>
        MoveSelectedSoundFont(1);

    private void MoveSelectedSoundFont(int delta)
    {
        if (SoundFontListBox.SelectedItem is not SoundFontDraftItem selected)
        {
            return;
        }
        int source = _soundFonts.IndexOf(selected);
        int target = source + delta;
        if (target < 0 || target >= _soundFonts.Count)
        {
            return;
        }
        _soundFonts.Move(source, target);
        SoundFontListBox.SelectedItem = selected;
    }

    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        Populate(ApplicationPreferences.Default);
        DeviceStatusText.Text = "Defaults restored in the form. Select Apply to persist them.";
        HideValidation();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        HideValidation();
        if (!TryInt(RenderAheadBox.Text, "Render-Ahead", 20, 2_000, out int renderAhead)
            || !TryInt(DeviceRequestBox.Text, "Device Buffer Request", 5, 200, out int deviceRequest)
            || !TryInt(VoicesBox.Text, "Realtime Maximum Sample Voices per Unit Stream", 1, 16_777_216, out int voices))
        {
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
            ShowValidation("Maximum Reusable Audio Cache must be a non-negative GiB value within the Int64 byte range.");
            CacheQuotaBox.Focus();
            return;
        }

        try
        {
            long quotaBytes = decimal.ToInt64(decimal.Round(
                quotaGib * BytesPerGibibyte,
                0,
                MidpointRounding.AwayFromZero));
            Result = _initial with
            {
                RealtimeAudio = new(
                    (DeviceBox.SelectedItem as DeviceChoice)?.Id,
                    renderAhead,
                    deviceRequest,
                    voices),
                AudioCache = new AudioCachePreferences(CacheRootBox.Text, quotaBytes).Normalize(),
                SoundFonts = _soundFonts
                    .Select(value => new ApplicationSoundFontPreference(
                        value.Path,
                        value.Enabled).Normalize())
                    .ToArray()
            };
            Result.Validate();
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            ShowValidation(exception.Message);
        }
    }

    private bool TryInt(string text, string label, int minimum, int maximum, out int result)
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
        ValidationBorder.Visibility = Visibility.Visible;
    }

    private void HideValidation()
    {
        ValidationText.Text = string.Empty;
        ValidationBorder.Visibility = Visibility.Collapsed;
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record DeviceChoice(string? Id, string DisplayName)
    {
        public static DeviceChoice SystemDefault { get; } = new(null, "System Default");
    }

    private sealed class SoundFontDraftItem : INotifyPropertyChanged
    {
        private bool _enabled;

        public SoundFontDraftItem(string path, bool enabled)
        {
            Path = System.IO.Path.GetFullPath(path);
            _enabled = enabled;
        }

        public string Path { get; }
        public string FileName => System.IO.Path.GetFileName(Path);
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
