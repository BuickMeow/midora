using System.Runtime.InteropServices;

namespace Midora.Audio.Bass;

internal static class NativeBassEnvironment
{
    private static readonly Lock Sync = new();

    private static readonly List<nint> LoadedHandles = [];

    private static bool _loaded = false;

    public static void EnsureNativeLibrariesLoaded()
    {
        lock (Sync)
        {
            if (_loaded)
            {
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!Windows.TryResolveNativeLibraries(out string bassPath, out string bassMidiPath))
                {
                    throw new FileNotFoundException("Counld not resolve bass libraries.");
                }

                LoadedHandles.Add(NativeLibrary.Load(bassPath));
                LoadedHandles.Add(NativeLibrary.Load(bassMidiPath));
                _loaded = true;
            }
            else
            {
                throw new PlatformNotSupportedException($"Midora.Audio.Bass does not support {RuntimeInformation.OSDescription}.");
            }
        }        
    }

    private static class Windows
    {   
        public static bool TryResolveNativeLibraries(out string bassPath, out string bassMidiPath)
        {
            foreach (var directory in CandidateDirectories())
            {
                var bass = Path.Combine(directory, "bass.dll");
                var bassMidi = Path.Combine(directory, "bassmidi.dll");
                if (File.Exists(bass) && File.Exists(bassMidi))
                {
                    bassPath = Path.GetFullPath(bass);
                    bassMidiPath = Path.GetFullPath(bassMidi);
                    return true;
                }
            }

            bassPath = string.Empty;
            bassMidiPath = string.Empty;
            return false;
        }

        private static IEnumerable<string> CandidateDirectories()
        {
            var configured = Environment.GetEnvironmentVariable("MIDORA_BASS_NATIVE_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                yield return configured;
            }

            yield return AppContext.BaseDirectory;
            yield return Path.Combine(AppContext.BaseDirectory, "BASS", "win-x64");
            yield return Path.Combine(AppContext.BaseDirectory, "Native", "BASS", "win-x64");

            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localApplicationData))
            {
                yield return Path.Combine(localApplicationData, "Midora", "Native", "BASS", "win-x64");
            }
        }
    }
}
