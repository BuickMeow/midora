using Midora.Midi;

namespace Midora.Audio;

public static class MidiRenderPlanSplicer
{
    public static MidiRenderPlan SpliceHeldGateEndAtProducerFrontier(
        MidiRenderPlan causalPrefix,
        MidiRenderPlan continuation,
        long producerFrontierFrame)
    {
        MidiRenderPlan spliced = SpliceAtProducerFrontier(
            causalPrefix,
            continuation,
            producerFrontierFrame);
        MidiPortRenderPlan[] ports = new MidiPortRenderPlan[spliced.Ports.Length];
        for (int portIndex = 0; portIndex < ports.Length; portIndex++)
        {
            ScheduledMidiMessage[] causalEvents = causalPrefix.Ports[portIndex].Events.ToArray();
            int causalPrefixCount = FindFirstEventAtOrAfter(
                causalEvents,
                producerFrontierFrame);
            int[,] activeNotes = new int[16, 128];
            for (int eventIndex = 0; eventIndex < causalPrefixCount; eventIndex++)
            {
                ScheduledMidiMessage scheduled = causalEvents[eventIndex];
                MidiMessage message = scheduled.Message;
                if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
                {
                    activeNotes[message.ChannelNumber, message.Byte1]++;
                }
                else if (message.MessageType == MidiMessageType.NoteOff
                    || message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0)
                {
                    ref int count = ref activeNotes[message.ChannelNumber, message.Byte1];
                    if (count != 0)
                    {
                        count--;
                    }
                }
            }

            int cleanupCount = 0;
            foreach (int count in activeNotes)
            {
                cleanupCount = checked(cleanupCount + count);
            }
            ScheduledMidiMessage[] current = spliced.Ports[portIndex].Events.ToArray();
            ScheduledMidiMessage[] withCleanup = new ScheduledMidiMessage[
                current.Length + cleanupCount];
            Array.Copy(current, 0, withCleanup, 0, causalPrefixCount);
            int destinationIndex = causalPrefixCount;
            for (byte channel = 0; channel < 16; channel++)
            {
                for (byte pitch = 0; pitch < 128; pitch++)
                {
                    for (int instance = 0; instance < activeNotes[channel, pitch]; instance++)
                    {
                        withCleanup[destinationIndex++] = new(
                            producerFrontierFrame,
                            MidiMessage.NoteOff(channel, pitch, 0));
                    }
                }
            }
            Array.Copy(
                current,
                causalPrefixCount,
                withCleanup,
                destinationIndex,
                current.Length - causalPrefixCount);
            ports[portIndex] = new(
                spliced.Ports[portIndex].ZeroBasedPortNumber,
                withCleanup);
        }

        return new MidiRenderPlan(
            spliced.SampleRate,
            spliced.TotalFrameCount,
            ports,
            spliced.SourceIds,
            spliced.InitiallyDisabledSourceIndices);
    }

    public static MidiRenderPlan SpliceAtProducerFrontier(
        MidiRenderPlan causalPrefix,
        MidiRenderPlan continuation,
        long producerFrontierFrame)
    {
        ArgumentNullException.ThrowIfNull(causalPrefix);
        ArgumentNullException.ThrowIfNull(continuation);
        if (producerFrontierFrame < 0
            || producerFrontierFrame > causalPrefix.TotalFrameCount
            || producerFrontierFrame > continuation.TotalFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(producerFrontierFrame));
        }
        if (causalPrefix.SampleRate != continuation.SampleRate)
        {
            throw new ArgumentException("Held-preview plans must use the same sample rate.", nameof(continuation));
        }
        if (!causalPrefix.SourceIds.SequenceEqual(continuation.SourceIds)
            || !causalPrefix.InitiallyDisabledSourceIndices.SequenceEqual(
                continuation.InitiallyDisabledSourceIndices))
        {
            throw new ArgumentException(
                "Held-preview continuation source tables must be identical.",
                nameof(continuation));
        }

        ReadOnlySpan<MidiPortRenderPlan> prefixPorts = causalPrefix.Ports;
        ReadOnlySpan<MidiPortRenderPlan> continuationPorts = continuation.Ports;
        if (prefixPorts.Length != continuationPorts.Length)
        {
            throw new ArgumentException(
                "Held-preview continuation Port sets must be identical.",
                nameof(continuation));
        }

        MidiPortRenderPlan[] ports = new MidiPortRenderPlan[prefixPorts.Length];
        for (int portIndex = 0; portIndex < prefixPorts.Length; portIndex++)
        {
            MidiPortRenderPlan prefixPort = prefixPorts[portIndex];
            MidiPortRenderPlan continuationPort = continuationPorts[portIndex];
            if (prefixPort.ZeroBasedPortNumber != continuationPort.ZeroBasedPortNumber)
            {
                throw new ArgumentException(
                    "Held-preview continuation Port sets must be identical and ordered.",
                    nameof(continuation));
            }

            ScheduledMidiMessage[] prefixEvents = prefixPort.Events.ToArray();
            ScheduledMidiMessage[] continuationEvents = continuationPort.Events.ToArray();
            int prefixCount = FindFirstEventAtOrAfter(prefixEvents, producerFrontierFrame);
            int continuationStart = FindFirstEventAtOrAfter(
                continuationEvents,
                producerFrontierFrame);
            ScheduledMidiMessage[] combined = new ScheduledMidiMessage[
                prefixCount + continuationEvents.Length - continuationStart];
            Array.Copy(prefixEvents, 0, combined, 0, prefixCount);
            Array.Copy(
                continuationEvents,
                continuationStart,
                combined,
                prefixCount,
                continuationEvents.Length - continuationStart);
            ports[portIndex] = new(prefixPort.ZeroBasedPortNumber, combined);
        }

        return new MidiRenderPlan(
            causalPrefix.SampleRate,
            continuation.TotalFrameCount,
            ports,
            causalPrefix.SourceIds,
            causalPrefix.InitiallyDisabledSourceIndices);
    }

    public static int FindFirstEventAtOrAfter(
        ReadOnlySpan<ScheduledMidiMessage> events,
        long sampleFrame)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (events[middle].SampleFrame < sampleFrame)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }
}
