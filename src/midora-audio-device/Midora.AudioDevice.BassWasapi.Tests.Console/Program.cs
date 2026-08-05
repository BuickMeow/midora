using Midora.AudioDevice.BassWasapi.Internals;
using Midora.AudioDevice.BassWasapi.Settings;
using System.Runtime.InteropServices;

namespace Midora.AudioDevice.BassWasapi.Tests.Console;

public static class Program
{
    public static int Main()
    {
        try
        {
            LoadBassLibraries();
            BassWasapiOutputDeviceFactory factory = new(
                new BassWasapiAudioOutputDeviceSettings());
            IReadOnlyList<AudioOutputDeviceInfo> devices = factory.GetDevices();

            global::System.Console.WriteLine(
                $"BASSWASAPI API=0x{factory.ApiVersion:x8}；enabled output devices={devices.Count}；terminal error={factory.LastEnumerationErrorCode}");
            for (int i = 0; i < devices.Count; i++)
            {
                AudioOutputDeviceInfo item = devices[i];
                global::System.Console.WriteLine(
                    $"[{i}] default={item.IsSystemDefault}；{item.Name}；{item.AudioFormat}；{item.Id}");
            }

            return devices.Count == 0 ? 1 : 0;
        }
        catch (Exception exception)
        {
            global::System.Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void LoadBassLibraries()
    {
        string bassPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Midora",
            "Native",
            "BASS",
            "win-x64");
        _ = NativeLibrary.Load(Path.Combine(bassPath, "bass.dll"));
        _ = NativeLibrary.Load(Path.Combine(bassPath, "basswasapi.dll"));
    }
}
