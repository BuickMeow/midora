using Midora.Audio.Bass;
using Midora.Midi;

unsafe
{
    NativeBassEnvironment.EnsureNativeLibrariesLoaded();

    int bassStreamHandle = BassMidiApi.CreateStream(16, BassFlags.Float | BassFlags.Decode | BassFlags.MidiNoFx | BassFlags.MidiNoteOff1 | BassFlags.SincInterpolation, 48000);

    int soundfontHandle = BassMidiApi.FontInit(@"D:\Soundfonts\sf2\sDetrimental Concert Grand Piano.sf2", FontInitFlags.Unicode | FontInitFlags.MemoryMap);

    _ = BassMidiApi.StreamSetFonts(bassStreamHandle, [new MidiFont { Handle = soundfontHandle, Preset = -1, Bank = 0 }], 1);

    Span<MidiMessage> midiMessages = stackalloc MidiMessage[32];
    byte* rawMidiMessageBuffer = stackalloc byte[32];
    int rawMessageLength = 0;

    midiMessages[0] = MidiMessage.NoteOn(0, 60, 75);
    rawMessageLength = (int)PackMidiMessages(midiMessages[0..0], rawMidiMessageBuffer);

    SendMessagesToStream();

    midiMessages[0] = MidiMessage.NoteOn(0, 64, 85);
    rawMessageLength = (int)PackMidiMessages(midiMessages[0..0], rawMidiMessageBuffer);

    Thread.Sleep(200);
    SendMessagesToStream();

    midiMessages[0] = MidiMessage.NoteOn(0, 67, 95);
    rawMessageLength = (int)PackMidiMessages(midiMessages[0..0], rawMidiMessageBuffer);

    Thread.Sleep(200);
    SendMessagesToStream();

    midiMessages[0] = MidiMessage.NoteOff(0, 60);
    midiMessages[1] = MidiMessage.NoteOff(0, 64);
    midiMessages[2] = MidiMessage.NoteOff(0, 67);
    rawMessageLength = (int)PackMidiMessages(midiMessages[0..2], rawMidiMessageBuffer);

    Thread.Sleep(2000);
    SendMessagesToStream();

    float[] audioBuffer = new float[48000];
    var requestedBytes = checked(48000 * 2 * sizeof(float));

    _ = BassApi.ChannelGetData(bassStreamHandle, audioBuffer, requestedBytes);

    ;

    /**********************************************/

    int SendMessagesToStream()
    {
        return BassMidiApi.StreamEvents(bassStreamHandle, MidiEventsMode.Raw | MidiEventsMode.NoRunningStatus, (nint)rawMidiMessageBuffer, rawMessageLength);
    }

    static long PackMidiMessages(ReadOnlySpan<MidiMessage> messages, byte* destination)
    {
        long bytesWritten = 0;

        for (int i = 0; i < messages.Length; i++)
        {
            bytesWritten += messages[i].WriteTo(destination + bytesWritten);
        }

        return bytesWritten;
    }
}
