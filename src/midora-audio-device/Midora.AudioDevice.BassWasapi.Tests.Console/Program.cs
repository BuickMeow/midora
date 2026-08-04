#pragma warning disable CS8321

using Midora.AudioDevice.BassWasapi.Internals;
using Midora.NativeInterops.Bass;
using Midora.NativeInterops.BassWasapi;
using System.Runtime.InteropServices;

namespace Midora.AudioDevice.BassWasapi.Tests.Console;

file class TriangleAudioRenderSource : IAudioRenderSource
{
    private readonly SineWave[] _sineWaves;

    public TriangleAudioRenderSource(double fundamentalHz, double trianglePeak, uint sampleRate = 48000, double durationSeconds = 1000)
    {
        _sineWaves = MakeTriangle(fundamentalHz, trianglePeak, sampleRate, durationSeconds);
    }

    public unsafe int Render(void* destination, int requiredBytes)
    {
        int expectingSampleCount = requiredBytes / 8;
        float* sampleBuffer = stackalloc float[expectingSampleCount];
        new Span<int>(sampleBuffer, expectingSampleCount).Clear();
        int maxActualSampleCount = 0;

        for (int w = 0; w < _sineWaves.Length; w++)
        {
            SineWave sineWave = _sineWaves[w];

            int actualSampleCount = (int)sineWave.TakeAdd(sampleBuffer, (uint)expectingSampleCount);

            if (maxActualSampleCount < actualSampleCount)
            {
                maxActualSampleCount = actualSampleCount;
            }
        }

        for (uint i = 0; i < maxActualSampleCount; i++)
        {
            //sampleBuffer[i] /= sineWaves.Length;
            ulong sample = ((uint*)sampleBuffer)[i];
            *((ulong*)destination + i) = sample | (sample << 32);
        }

        return maxActualSampleCount * 8;
    }

    public async Task WaitAsync()
    {
        await Task.WhenAll(_sineWaves.Select(w => w.WaitUntilCompletedAsync()));
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
}

public static class Program
{
    public static async Task Main()
    {
        LoadBassLib();

        TriangleAudioRenderSource renderSource = new(300, 0.5);

        BassWasapiOutputDeviceFactory deviceFactory = new(new());

        IAudioOutputDevice outputDevice = deviceFactory.Open(
            deviceFactory.GetDevices().FirstOrDefault() ?? throw new MidoraAudioDeviceException("No audio output devices."),
            renderSource
        );

        outputDevice.Start();

        await renderSource.WaitAsync();
    }

    static void LoadBassLib()
    {
        string bassPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Midora", "Native", "BASS", "win-x64");

        string bassDllPath = Path.Combine(bassPath, "bass.dll");
        string bassMidiDllPath = Path.Combine(bassPath, "bassmidi.dll");
        string bassWasapiDllPath = Path.Combine(bassPath, "basswasapi.dll");

        NativeLibrary.Load(bassDllPath);
        NativeLibrary.Load(bassMidiDllPath);
        NativeLibrary.Load(bassWasapiDllPath);
    }

    static async Task TestDevice()
    {
        SineWave[] sineWaves = MakeTriangle(440, 1d);
        //SineWave[] sineWaves = [
        //    new(440, 48000, 1000),
        //    new(560, 48000, 1000),
        //    new(680, 48000, 1000),
        //];

        GCHandle sineWaveHandle = GCHandle.Alloc(sineWaves, GCHandleType.Normal);

        unsafe
        {
            void* sineWaveHandlePtr = (void*)GCHandle.ToIntPtr(sineWaveHandle);

            if (0 == BASSWASAPI.Init(-1, 48000, 2, BASSWASAPI.BASS_WASAPI_EVENT | BASSWASAPI.BASS_WASAPI_SAMPLES, 2048, 0, &BassWasapiProc, sineWaveHandlePtr))
            {
                throw new BassException();
            }
        }

        if (0 == BASSWASAPI.Start())
        {
            throw new BassException();
        }

        await Task.WhenAll(sineWaves.Select(w => w.WaitUntilCompletedAsync()));

        if (0 == BASSWASAPI.Stop(1))
        {
            throw new BassException();
        }
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
}
