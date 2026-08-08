using Midora.Midi;
using System.Security.Cryptography;

namespace Midora.Audio;

public static class MidiRenderPlanFile
{
    private const uint Magic = 0x5041444d;
    private const int Version = 4;
    private const int ChecksumByteCount = 32;
    private const int MaximumFileByteCount = 256 * 1024 * 1024;
    private const int MaximumEventCount = 16 * 1024 * 1024;
    private const int FixedPayloadByteCount = 36;
    private const int SourceIdByteCount = sizeof(long);
    private const int PortHeaderByteCount = 8;
    private const int UnitFragmentHeaderByteCount = 148;
    private const int EventByteCount = 16;

    public static void Write(string filePath, MidiRenderPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(plan);
        ValidateWritablePlanBounds(plan);

        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(plan.SampleRate);
            writer.Write(plan.TotalFrameCount);
            writer.Write(plan.Ports.Length);
            writer.Write(plan.SourceIds.Length);
            foreach (long sourceId in plan.SourceIds)
            {
                writer.Write(sourceId);
            }
            writer.Write(plan.InitiallyDisabledSourceIndices.Length);
            foreach (int sourceIndex in plan.InitiallyDisabledSourceIndices)
            {
                writer.Write(sourceIndex);
            }

            foreach (MidiPortRenderPlan port in plan.Ports)
            {
                ReadOnlySpan<ScheduledMidiMessage> events = port.Events;
                writer.Write(port.ZeroBasedPortNumber);
                writer.Write((byte)0);
                writer.Write((ushort)0);
                writer.Write(events.Length);
                foreach (ScheduledMidiMessage item in events)
                {
                    writer.Write(item.SampleFrame);
                    writer.Write(item.Message.PackedValue);
                    writer.Write(item.SourceIndex);
                }
            }
            writer.Write(plan.UnitFragments.Length);
            foreach (MidiUnitFragmentRenderPlan fragment in plan.UnitFragments)
            {
                writer.Write(fragment.CanonicalZeroBasedPortNumber);
                writer.Write(fragment.CanonicalZeroBasedChannelNumber);
                writer.Write((ushort)0);
                writer.Write(fragment.TrackId);
                writer.Write(fragment.SegmentId);
                writer.Write(fragment.EventInstrumentId);
                writer.Write(fragment.InstanceGroupId);
                writer.Write(fragment.SubVoiceId);
                writer.Write(fragment.SourceIndex);
                writer.Write(fragment.StartFrame);
                writer.Write(fragment.EndFrame);
                writer.Write(Convert.FromHexString(fragment.SemanticFingerprint));
                writer.Write(fragment.PcmCacheKey is null
                    ? new byte[32]
                    : Convert.FromHexString(fragment.PcmCacheKey));
                writer.Write(fragment.PcmCachePayloadOffset);
                writer.Write(fragment.PcmCacheHit);
                writer.Write(new byte[7]);
                writer.Write(fragment.Events.Length);
                foreach (ScheduledMidiMessage item in fragment.Events)
                {
                    writer.Write(item.SampleFrame);
                    writer.Write(item.Message.PackedValue);
                    writer.Write(item.SourceIndex);
                }
            }
        }

        if (payload.Length + ChecksumByteCount > MaximumFileByteCount)
        {
            throw new InvalidDataException("The IPC MIDI event plan exceeds its bounded byte limit.");
        }

        byte[] payloadBuffer = payload.GetBuffer();
        ReadOnlySpan<byte> payloadBytes = payloadBuffer.AsSpan(0, checked((int)payload.Length));
        Span<byte> checksum = stackalloc byte[ChecksumByteCount];
        _ = SHA256.HashData(payloadBytes, checksum);

        using FileStream file = new(
            filePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        file.Write(payloadBytes);
        file.Write(checksum);
        file.Flush(flushToDisk: true);
    }

    public static MidiRenderPlan Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FileInfo info = new(filePath);
        if (info.Length is < ChecksumByteCount or > MaximumFileByteCount)
        {
            throw new InvalidDataException("The IPC MIDI event plan length is invalid.");
        }

        byte[] fileBytes = File.ReadAllBytes(filePath);
        int payloadLength = fileBytes.Length - ChecksumByteCount;
        ReadOnlySpan<byte> payload = fileBytes.AsSpan(0, payloadLength);
        ReadOnlySpan<byte> storedChecksum = fileBytes.AsSpan(payloadLength, ChecksumByteCount);
        Span<byte> actualChecksum = stackalloc byte[ChecksumByteCount];
        _ = SHA256.HashData(payload, actualChecksum);
        if (!CryptographicOperations.FixedTimeEquals(storedChecksum, actualChecksum))
        {
            throw new InvalidDataException("The IPC MIDI event plan checksum is invalid.");
        }

        try
        {
            using MemoryStream stream = new(fileBytes, 0, payloadLength, writable: false, publiclyVisible: true);
            using BinaryReader reader = new(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
            {
                throw new InvalidDataException("The IPC MIDI event plan header or version is invalid.");
            }

            int sampleRate = reader.ReadInt32();
            long totalFrameCount = reader.ReadInt64();
            int portCount = reader.ReadInt32();
            if (portCount is < 0 or > 16)
            {
                throw new InvalidDataException("The IPC MIDI Port count is invalid.");
            }

            int sourceCount = reader.ReadInt32();
            if (sourceCount is < 0 or > MaximumEventCount
                || ((long)sourceCount * SourceIdByteCount) + sizeof(int)
                    > payloadLength - stream.Position)
            {
                throw new InvalidDataException("The IPC MIDI source count is invalid.");
            }
            long[] sourceIds = new long[sourceCount];
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                sourceIds[sourceIndex] = reader.ReadInt64();
            }
            int disabledSourceCount = reader.ReadInt32();
            if (disabledSourceCount is < 0 || disabledSourceCount > sourceCount
                || (long)disabledSourceCount * sizeof(int) > payloadLength - stream.Position)
            {
                throw new InvalidDataException("The IPC disabled MIDI source count is invalid.");
            }
            int[] disabledSourceIndices = new int[disabledSourceCount];
            for (int sourceIndex = 0; sourceIndex < disabledSourceCount; sourceIndex++)
            {
                disabledSourceIndices[sourceIndex] = reader.ReadInt32();
            }

            MidiPortRenderPlan[] ports = new MidiPortRenderPlan[portCount];
            int totalEventCount = 0;
            for (int portIndex = 0; portIndex < portCount; portIndex++)
            {
                byte portNumber = reader.ReadByte();
                byte reservedByte = reader.ReadByte();
                ushort reservedWord = reader.ReadUInt16();
                if (reservedByte != 0 || reservedWord != 0)
                {
                    throw new InvalidDataException("The IPC MIDI Port record has non-zero reserved fields.");
                }
                int eventCount = reader.ReadInt32();
                totalEventCount = checked(totalEventCount + eventCount);
                if (eventCount < 0 || totalEventCount > MaximumEventCount
                    || (long)eventCount * EventByteCount > payloadLength - stream.Position)
                {
                    throw new InvalidDataException("The IPC MIDI event count is invalid.");
                }

                ScheduledMidiMessage[] events = new ScheduledMidiMessage[eventCount];
                for (int eventIndex = 0; eventIndex < eventCount; eventIndex++)
                {
                    long sampleFrame = reader.ReadInt64();
                    MidiMessage message = MidiMessage.FromPackedValue(reader.ReadUInt32());
                    int sourceIndex = reader.ReadInt32();
                    events[eventIndex] = new ScheduledMidiMessage(sampleFrame, message, sourceIndex);
                }

                ports[portIndex] = new MidiPortRenderPlan(portNumber, events);
            }

            int fragmentCount = reader.ReadInt32();
            if (fragmentCount is < 0 or > MaximumEventCount
                || (long)fragmentCount * UnitFragmentHeaderByteCount
                    > payloadLength - stream.Position)
            {
                throw new InvalidDataException("The IPC MIDI Unit fragment count is invalid.");
            }
            MidiUnitFragmentRenderPlan[] fragments = new MidiUnitFragmentRenderPlan[fragmentCount];
            for (int fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
            {
                byte portNumber = reader.ReadByte();
                byte channelNumber = reader.ReadByte();
                ushort reserved = reader.ReadUInt16();
                if (reserved != 0)
                {
                    throw new InvalidDataException(
                        "The IPC MIDI Unit fragment has a non-zero reserved field.");
                }
                long trackId = reader.ReadInt64();
                long segmentId = reader.ReadInt64();
                long eventInstrumentId = reader.ReadInt64();
                long instanceGroupId = reader.ReadInt64();
                long subVoiceId = reader.ReadInt64();
                int sourceIndex = reader.ReadInt32();
                long startFrame = reader.ReadInt64();
                long endFrame = reader.ReadInt64();
                string fingerprint = Convert.ToHexStringLower(reader.ReadBytes(32));
                if (fingerprint.Length != 64)
                {
                    throw new InvalidDataException(
                        "The IPC MIDI Unit fragment fingerprint is truncated.");
                }
                byte[] cacheKeyBytes = reader.ReadBytes(32);
                if (cacheKeyBytes.Length != 32)
                {
                    throw new InvalidDataException(
                        "The IPC MIDI Unit fragment cache key is truncated.");
                }
                bool hasCacheKey = cacheKeyBytes.Any(value => value != 0);
                string? cacheKey = hasCacheKey
                    ? Convert.ToHexStringLower(cacheKeyBytes)
                    : null;
                long cachePayloadOffset = reader.ReadInt64();
                bool cacheHit = reader.ReadBoolean();
                if (reader.ReadBytes(7).Any(value => value != 0))
                {
                    throw new InvalidDataException(
                        "The IPC MIDI Unit fragment cache binding has non-zero reserved fields.");
                }
                int eventCount = reader.ReadInt32();
                totalEventCount = checked(totalEventCount + eventCount);
                if (eventCount < 0 || totalEventCount > MaximumEventCount
                    || (long)eventCount * EventByteCount > payloadLength - stream.Position)
                {
                    throw new InvalidDataException(
                        "The IPC MIDI Unit fragment event count is invalid.");
                }
                ScheduledMidiMessage[] events = new ScheduledMidiMessage[eventCount];
                for (int eventIndex = 0; eventIndex < eventCount; eventIndex++)
                {
                    events[eventIndex] = new(
                        reader.ReadInt64(),
                        MidiMessage.FromPackedValue(reader.ReadUInt32()),
                        reader.ReadInt32());
                }
                fragments[fragmentIndex] = new(
                    portNumber,
                    channelNumber,
                    trackId,
                    segmentId,
                    eventInstrumentId,
                    instanceGroupId,
                    subVoiceId,
                    sourceIndex,
                    startFrame,
                    endFrame,
                    fingerprint,
                    events,
                    cacheKey,
                    cachePayloadOffset,
                    cacheHit);
            }

            if (stream.Position != payloadLength)
            {
                throw new InvalidDataException("The IPC MIDI event plan contains trailing payload data.");
            }

            return new MidiRenderPlan(
                sampleRate,
                totalFrameCount,
                ports,
                sourceIds,
                disabledSourceIndices,
                fragments);
        }
        catch (Exception exception) when (exception is EndOfStreamException
            or OverflowException
            or ArgumentException)
        {
            throw new InvalidDataException("The IPC MIDI event plan structure is invalid.", exception);
        }
    }

    private static void ValidateWritablePlanBounds(MidiRenderPlan plan)
    {
        int sourceCount = plan.SourceIds.Length;
        int disabledSourceCount = plan.InitiallyDisabledSourceIndices.Length;
        if (sourceCount > MaximumEventCount)
        {
            throw new InvalidDataException("The IPC MIDI event plan exceeds its bounded source limit.");
        }

        long totalEventCount = 0;
        foreach (MidiPortRenderPlan port in plan.Ports)
        {
            totalEventCount += port.Events.Length;
            if (totalEventCount > MaximumEventCount)
            {
                throw new InvalidDataException("The IPC MIDI event plan exceeds its bounded event limit.");
            }
        }
        foreach (MidiUnitFragmentRenderPlan fragment in plan.UnitFragments)
        {
            totalEventCount += fragment.Events.Length;
            if (totalEventCount > MaximumEventCount)
            {
                throw new InvalidDataException(
                    "The IPC MIDI event plan exceeds its bounded Unit fragment event limit.");
            }
        }

        long payloadByteCount = FixedPayloadByteCount
            + ((long)sourceCount * SourceIdByteCount)
            + ((long)disabledSourceCount * sizeof(int))
            + ((long)plan.Ports.Length * PortHeaderByteCount)
            + ((long)plan.UnitFragments.Length * UnitFragmentHeaderByteCount)
            + ((long)totalEventCount * EventByteCount);
        if (payloadByteCount + ChecksumByteCount > MaximumFileByteCount)
        {
            throw new InvalidDataException("The IPC MIDI event plan exceeds its bounded byte limit.");
        }
    }
}
