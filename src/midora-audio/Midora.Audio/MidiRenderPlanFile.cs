using Midora.Midi;
using System.Security.Cryptography;

namespace Midora.Audio;

public static class MidiRenderPlanFile
{
    private const uint Magic = 0x5041444d;
    private const int Version = 2;
    private const int ChecksumByteCount = 32;
    private const int MaximumFileByteCount = 256 * 1024 * 1024;
    private const int MaximumEventCount = 16 * 1024 * 1024;

    public static void Write(string filePath, MidiRenderPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(plan);

        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(plan.SampleRate);
            writer.Write(plan.TotalFrameCount);
            writer.Write(plan.Ports.Length);
            writer.Write(plan.SourceIds.Length);
            foreach (Guid sourceId in plan.SourceIds)
            {
                writer.Write(sourceId.ToByteArray());
            }
            writer.Write(plan.InitiallyDisabledSourceIndices.Length);
            foreach (int sourceIndex in plan.InitiallyDisabledSourceIndices)
            {
                writer.Write(sourceIndex);
            }

            int totalEventCount = 0;
            foreach (MidiPortRenderPlan port in plan.Ports)
            {
                ReadOnlySpan<ScheduledMidiMessage> events = port.Events;
                totalEventCount = checked(totalEventCount + events.Length);
                if (totalEventCount > MaximumEventCount)
                {
                    throw new InvalidDataException("The IPC MIDI event plan exceeds its bounded event limit.");
                }

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
        if (sourceCount is < 0 or > MaximumEventCount)
        {
            throw new InvalidDataException("The IPC MIDI source count is invalid.");
        }
        Guid[] sourceIds = new Guid[sourceCount];
        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            sourceIds[sourceIndex] = new Guid(reader.ReadBytes(16));
        }
        int disabledSourceCount = reader.ReadInt32();
        if (disabledSourceCount is < 0 || disabledSourceCount > sourceCount)
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
            _ = reader.ReadByte();
            _ = reader.ReadUInt16();
            int eventCount = reader.ReadInt32();
            totalEventCount = checked(totalEventCount + eventCount);
            if (eventCount < 0 || totalEventCount > MaximumEventCount)
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

        if (stream.Position != payloadLength)
        {
            throw new InvalidDataException("The IPC MIDI event plan contains trailing payload data.");
        }

        return new MidiRenderPlan(
            sampleRate,
            totalFrameCount,
            ports,
            sourceIds,
            disabledSourceIndices);
    }
}
