using Midora.AudioDevice;
using Midora.AudioDevice.BassWasapi.Internals;
using Midora.Midi;
using Midora.NativeInterops.Bass;
using Midora.NativeInterops.BassMidi;
using System.Runtime.InteropServices;

namespace Midora.Audio.Bass.Tests.Console;

file sealed unsafe class TestBassMidiAudioRenderSource : IAudioRenderSource
{
    private readonly uint _bassStreamHandle;
    private readonly uint _soundfontHandle;

    private readonly MidiMessage* _midiMessageBuffer;
    private readonly nint _midiMessageBufferSize;

    private readonly byte* _midiPackBuffer;

    public TestBassMidiAudioRenderSource()
    {
        _bassStreamHandle = BASSMIDI.StreamCreate(
            16,
            BASS.BASS_SAMPLE_FLOAT | BASS.BASS_STREAM_DECODE | BASSMIDI.BASS_MIDI_NOFX | BASSMIDI.BASS_MIDI_NOTEOFF1,
            48000
        );

        if (0 == _bassStreamHandle)
        {
            throw new BassException();
        }

        nint sondfontNamePtr = Marshal.StringToHGlobalUni(@"D:\Soundfonts\sf2\sDetrimental Concert Grand Piano.sf2");

        _soundfontHandle = BASSMIDI.FontInit(
            (void*)sondfontNamePtr,
            BASS.BASS_UNICODE | BASSMIDI.BASS_MIDI_FONT_MMAP
        );

        if (0 == _soundfontHandle)
        {
            throw new BassException();
        }

        Marshal.FreeHGlobal(sondfontNamePtr);

        var font = new BASSMIDI.BASS_MIDI_FONT { font = _soundfontHandle, preset = -1, bank = 0 };

        if (0 == BASSMIDI.StreamSetFonts(_bassStreamHandle, &font, 1))
        {
            throw new BassException();
        }

        _midiMessageBufferSize = 8192;
        nuint requiredLength = checked((nuint)(sizeof(MidiMessage) * _midiMessageBufferSize));
        _midiMessageBuffer = (MidiMessage*)NativeMemory.Alloc(requiredLength);
        _midiPackBuffer = (byte*)NativeMemory.Alloc(requiredLength);
    }

    public void Play()
    {
        _midiMessageBuffer[0] = MidiMessage.ControlChange(0, 73, 90);
        SendMidiMessages(1);

        Thread.Sleep(100);

        _midiMessageBuffer[0] = MidiMessage.NoteOn(0, 60, 75);
        SendMidiMessages(1);

        Thread.Sleep(200);

        _midiMessageBuffer[0] = MidiMessage.NoteOn(0, 64, 85);
        SendMidiMessages(1);

        Thread.Sleep(200);

        _midiMessageBuffer[0] = MidiMessage.NoteOn(0, 67, 95);
        SendMidiMessages(1);

        Thread.Sleep(500);

        _midiMessageBuffer[0] = MidiMessage.PitchWheelChange(0, 9000);
        SendMidiMessages(1);

        Thread.Sleep(500);

        _midiMessageBuffer[0] = MidiMessage.PitchWheelChange(0, 12000);
        SendMidiMessages(1);

        Thread.Sleep(500);

        _midiMessageBuffer[0] = MidiMessage.PitchWheelChange(0, 16000);
        SendMidiMessages(1);

        Thread.Sleep(500);

        _midiMessageBuffer[0] = MidiMessage.PitchWheelChange(0, 4000);
        SendMidiMessages(1);

        Thread.Sleep(500);

        _midiMessageBuffer[0] = MidiMessage.PitchWheelChange(0, 1000);
        SendMidiMessages(1);

        Thread.Sleep(500);

        _midiMessageBuffer[0] = MidiMessage.PitchWheelChange(0, 8192);
        SendMidiMessages(1);

        Thread.Sleep(500);

        _midiMessageBuffer[0] = MidiMessage.ControlChange(0, 74, 10);
        SendMidiMessages(1);

        Thread.Sleep(500);

        _midiMessageBuffer[0] = MidiMessage.ControlChange(0, 74, 0);
        SendMidiMessages(1);

        Thread.Sleep(500);

        _midiMessageBuffer[0] = MidiMessage.NoteOff(0, 60);
        _midiMessageBuffer[1] = MidiMessage.NoteOff(0, 64);
        _midiMessageBuffer[2] = MidiMessage.NoteOff(0, 67);
        SendMidiMessages(3);

        Thread.Sleep(6000);
    }

    private void SendMidiMessages(int messageCount)
    {
        int packedMessageLength = 0;

        for (int i = 0; i < messageCount; i++)
        {
            packedMessageLength += (_midiMessageBuffer + i)->WriteTo(_midiPackBuffer + packedMessageLength);
        }

        _ = BASSMIDI.StreamEvents(
                _bassStreamHandle,
                BASSMIDI.BASS_MIDI_EVENTS_RAW | BASSMIDI.BASS_MIDI_EVENTS_NORSTATUS,
                _midiPackBuffer,
                (uint)packedMessageLength
            );
    }

    int IAudioRenderSource.Render(void* destination, int requiredBytes)
    {
        return (int)BASS.ChannelGetData(_bassStreamHandle, destination, (uint)requiredBytes);
    }
}

public static class Program
{
    public static unsafe void Main()
    {
        LoadBassLib();

        if (0 == BASS.Init(0, 48000, 0, null, null))
        {
            throw new BassException();
        }

        TestBassMidiAudioRenderSource renderSource = new();

        BassWasapiOutputDeviceFactory deviceFactory = new(new());

        IAudioOutputDevice outputDevice = deviceFactory.Open(
            deviceFactory.GetDevices().FirstOrDefault() ?? throw new MidoraAudioDeviceException("No audio output devices."),
            renderSource
        );

        outputDevice.Start();

        renderSource.Play();
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
}
