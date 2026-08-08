using System.Text.Json;
using System.Text.Json.Serialization;

namespace Midora.Application;

public sealed record ApplicationPreferencesLoadResult(
    ApplicationPreferences Preferences,
    ApplicationPreferenceNotice? Notice);

public sealed record ApplicationPreferencesSaveResult(
    bool Succeeded,
    ApplicationPreferenceNotice? Notice);

public sealed class ApplicationPreferencesStore
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumFileBytes = 1024 * 1024;
    private readonly string _filePath;

    public ApplicationPreferencesStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? GetDefaultFilePath());
    }

    public string FilePath => _filePath;

    public static string GetDefaultFilePath()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "The current Windows user's Local Application Data directory is unavailable.");
        }
        return Path.Combine(localApplicationData, "Midora", "preferences-v1.json");
    }

    public ApplicationPreferencesLoadResult Load()
    {
        try
        {
            byte[] json;
            using (FileStream stream = new(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan))
            {
                if (stream.Length > MaximumFileBytes)
                {
                    throw new InvalidDataException(
                        $"The Application Preferences file exceeds {MaximumFileBytes} bytes.");
                }
                json = new byte[checked((int)stream.Length)];
                stream.ReadExactly(json);
            }
            ApplicationPreferencesJsonV1 dto = JsonSerializer.Deserialize(
                json,
                ApplicationPreferencesJsonContextV1.Default.ApplicationPreferencesJsonV1)
                ?? throw new InvalidDataException("The Application Preferences JSON is null.");
            if (dto.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported Application Preferences schema version {dto.SchemaVersion}.");
            }
            ApplicationRecentDirectoriesJsonV1 recentDirectories = dto.RecentDirectories
                ?? throw new InvalidDataException(
                    "Application Preferences recentDirectories is required.");

            ApplicationPreferences preferences = new(
                new RealtimeAudioPreferences(
                    dto.PlaybackOutputDeviceId,
                    dto.RenderAheadMilliseconds,
                    dto.DeviceBufferRequestMilliseconds,
                    dto.RealtimeMaximumSampleVoicesPerUnitStream),
                new AudioCachePreferences(
                    dto.AudioCacheRootPath,
                    dto.MaximumReusableAudioCacheBytes).Normalize(),
                new ApplicationRecentDirectories(
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.OpenProject),
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.SaveAndSaveCopy),
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.SoundFont),
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.MidiExport),
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.AudioRender)));
            preferences.Validate();
            return new(preferences, null);
        }
        catch (FileNotFoundException)
        {
            return new(ApplicationPreferences.Default, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(ApplicationPreferences.Default, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            return new(
                ApplicationPreferences.Default,
                new ApplicationPreferenceNotice(
                    "PreferenceReadFailed",
                    "Application Preferences could not be read; safe defaults are in use.",
                    exception));
        }
    }

    public ApplicationPreferencesSaveResult Save(ApplicationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException(
                    "The Application Preferences path has no parent directory.");
            Directory.CreateDirectory(directory);
            ApplicationPreferencesJsonV1 dto = new()
            {
                SchemaVersion = CurrentSchemaVersion,
                PlaybackOutputDeviceId = preferences.RealtimeAudio.PlaybackOutputDeviceId,
                RenderAheadMilliseconds = preferences.RealtimeAudio.RenderAheadMilliseconds,
                DeviceBufferRequestMilliseconds =
                    preferences.RealtimeAudio.DeviceBufferRequestMilliseconds,
                RealtimeMaximumSampleVoicesPerUnitStream =
                    preferences.RealtimeAudio.MaximumSampleVoicesPerUnitStream,
                AudioCacheRootPath = preferences.AudioCache.RootPath,
                MaximumReusableAudioCacheBytes = preferences.AudioCache.MaximumReusableBytes,
                RecentDirectories = new ApplicationRecentDirectoriesJsonV1
                {
                    OpenProject = preferences.RecentDirectories.OpenProject,
                    SaveAndSaveCopy = preferences.RecentDirectories.SaveAndSaveCopy,
                    SoundFont = preferences.RecentDirectories.SoundFont,
                    MidiExport = preferences.RecentDirectories.MidiExport,
                    AudioRender = preferences.RecentDirectories.AudioRender
                }
            };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                dto,
                ApplicationPreferencesJsonContextV1.Default.ApplicationPreferencesJsonV1);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
            temporaryPath = null;
            return new(true, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException)
        {
            return new(
                false,
                new ApplicationPreferenceNotice(
                    "PreferenceWriteFailed",
                    "Application Preferences could not be saved; safe defaults are in use.",
                    exception));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // A failed best-effort cleanup does not replace the original write error.
                }
            }
        }
    }
}

internal sealed class ApplicationPreferencesJsonV1
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; }

    [JsonPropertyOrder(1)]
    public string? PlaybackOutputDeviceId { get; set; }

    [JsonPropertyOrder(2)]
    public int RenderAheadMilliseconds { get; set; }

    [JsonPropertyOrder(3)]
    public int DeviceBufferRequestMilliseconds { get; set; }

    [JsonPropertyOrder(4)]
    public int RealtimeMaximumSampleVoicesPerUnitStream { get; set; }

    [JsonPropertyOrder(5)]
    public required string AudioCacheRootPath { get; set; }

    [JsonPropertyOrder(6)]
    public required long MaximumReusableAudioCacheBytes { get; set; }

    [JsonPropertyOrder(7)]
    public ApplicationRecentDirectoriesJsonV1? RecentDirectories { get; set; }
}

internal sealed class ApplicationRecentDirectoriesJsonV1
{
    [JsonPropertyOrder(0)]
    public string? OpenProject { get; set; }

    [JsonPropertyOrder(1)]
    public string? SaveAndSaveCopy { get; set; }

    [JsonPropertyOrder(2)]
    public string? SoundFont { get; set; }

    [JsonPropertyOrder(3)]
    public string? MidiExport { get; set; }

    [JsonPropertyOrder(4)]
    public string? AudioRender { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ApplicationPreferencesJsonV1))]
internal sealed partial class ApplicationPreferencesJsonContextV1 : JsonSerializerContext;
