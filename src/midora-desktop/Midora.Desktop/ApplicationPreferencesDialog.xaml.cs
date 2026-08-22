using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Midora.Application;

namespace Midora.Desktop;

public partial class ApplicationPreferencesDialog : Window
{
    private const decimal BytesPerGibibyte = 1024m * 1024m * 1024m;
    private readonly ApplicationPreferences _initial;

    public ApplicationPreferencesDialog(ApplicationPreferences initial)
    {
        _initial = initial ?? throw new ArgumentNullException(nameof(initial));
        InitializeComponent();
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
        DefaultSoundFontBox.Text = preferences.DefaultEmbeddedSoundFontPath ?? string.Empty;
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

    private void OnBrowseDefaultSoundFontClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Select Default Embedded SoundFont",
            Filter = "SoundFont 2 (*.sf2)|*.sf2|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = File.Exists(DefaultSoundFontBox.Text)
                ? Path.GetDirectoryName(DefaultSoundFontBox.Text)
                : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            DefaultSoundFontBox.Text = dialog.FileName;
        }
    }

    private void OnClearDefaultSoundFontClick(object sender, RoutedEventArgs e) =>
        DefaultSoundFontBox.Text = string.Empty;

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
                DefaultEmbeddedSoundFontPath = string.IsNullOrWhiteSpace(DefaultSoundFontBox.Text)
                    ? null
                    : Path.GetFullPath(DefaultSoundFontBox.Text)
            };
            if (Result.DefaultEmbeddedSoundFontPath is string defaultSoundFont
                && !File.Exists(defaultSoundFont))
            {
                ShowValidation("The default embedded SoundFont file does not exist.");
                return;
            }
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
}
