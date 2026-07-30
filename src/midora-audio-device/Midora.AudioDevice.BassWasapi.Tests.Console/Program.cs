#pragma warning disable CS8321

using Midora.AudioDevice.BassWasapi.Tests.Console;
using Midora.NativeInterops.Bass;
using Midora.NativeInterops.BassWasapi;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

SineWave[] sineWaves = MakeTriangle(440, 1d);
//SineWave[] sineWaves = [
//    new(440, 48000, 1000),
//    new(560, 48000, 1000),
//    new(680, 48000, 1000),
//];

GCHandle sineWaveHandle = GCHandle.Alloc(sineWaves, GCHandleType.Normal);

unsafe
{
    string bassPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Midora", "Native", "BASS", "win-x64");

    string bassDllPath = Path.Combine(bassPath, "bass.dll");
    string bassMidiDllPath = Path.Combine(bassPath, "bassmidi.dll");
    string bassWasapiDllPath = Path.Combine(bassPath, "basswasapi.dll");

    NativeLibrary.Load(bassDllPath);
    NativeLibrary.Load(bassMidiDllPath);
    NativeLibrary.Load(bassWasapiDllPath);

    void* sineWaveHandlePtr = (void*)GCHandle.ToIntPtr(sineWaveHandle);

    if (0 == BASSWASAPI.Init(-1, 48000, 2, BASSWASAPI.BASS_WASAPI_EVENT | BASSWASAPI.BASS_WASAPI_SAMPLES, 2048, 0, &BassWasapiProc, sineWaveHandlePtr))
    {
        BassException.TryThrow();
    }
}

if (0 == BASSWASAPI.Start())
{
    BassException.TryThrow();
}

await Task.WhenAll(sineWaves.Select(w => w.WaitUntilCompletedAsync()));

if (0 == BASSWASAPI.Stop(1))
{
    BassException.TryThrow();
}

static SineWave[] MakeTriangle(double fundamentalHz, double trianglePeak, uint sampleRate = 48000, double durationSeconds = 1000)
{
    double nyquist = sampleRate * 0.5;
    double scale = 8.0 * trianglePeak / (double.Pi * double.Pi);

    int sineCount = (int)Math.Ceiling(nyquist / fundamentalHz) / 2;

    SineWave[] sineWaves = new SineWave[sineCount];

    for (int i = 0; i < sineCount; i++)
    {
        int n = 2 * i + 1; // 1, 3, 5, 7...

        double sign = (i & 1) == 0 ? 1.0 : -1.0;

        sineWaves[i] = new(
            n * fundamentalHz,
            sampleRate,
            durationSeconds,
            sign * scale / ((double)n * n),
            useCos: false
        );
    }

    return sineWaves;
}

[UnmanagedCallersOnly]
static unsafe uint BassWasapiProc(void* buffer, uint length, void* user)
{
    try
    {
        if (user == null)
        {
            return 0;
        }

        GCHandle handle = GCHandle.FromIntPtr((nint)user);
        if (handle.Target is not SineWave[] sineWaves)
        {
            return 0;
        }

        int expectingSampleCount = (int)length / 8;
        float* sampleBuffer = stackalloc float[expectingSampleCount];
        new Span<int>(sampleBuffer, expectingSampleCount).Clear();
        uint maxActualSampleCount = 0;

        for (int w = 0; w < sineWaves.Length; w++)
        {
            SineWave sineWave = sineWaves[w];
            
            uint actualSampleCount = sineWave.TakeAdd(sampleBuffer, (uint)expectingSampleCount);

            if (maxActualSampleCount < actualSampleCount)
            {
                maxActualSampleCount = actualSampleCount;
            }
        }

        for (uint i = 0; i < maxActualSampleCount; i++)
        {
            //sampleBuffer[i] /= sineWaves.Length;
            ulong sample = ((uint*)sampleBuffer)[i];
            *((ulong*)buffer + i) = sample | (sample << 32);
        }

        return maxActualSampleCount * 8;
    }
    catch
    {
        // eat
        return 0;
    }
    finally
    {

    }
}

file class BassException : Exception
{
    private BassException(string message) : base(message) { }

    public static void TryThrow()
    {
        int errorCode = BASS.ErrorGetCode();

        if (errorCode != 0)
        {
            throw new BassException($"Bass error code = {errorCode}");
        }
    }
}
