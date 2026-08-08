using Midora.Midi;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Midora.Audio.Bass.Tests;

public sealed class MidiRenderPlanFileTests
{
    [Fact]
    public void RoundTripsDeterministicallyAndRejectsChecksumDamage()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"midora-plan-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string firstPath = Path.Combine(directory, "first.mdap");
        string secondPath = Path.Combine(directory, "second.mdap");
        try
        {
            MidiRenderPlan plan = CreatePlan();
            MidiRenderPlanFile.Write(firstPath, plan);
            MidiRenderPlan restored = MidiRenderPlanFile.Read(firstPath);
            MidiRenderPlanFile.Write(secondPath, restored);

            Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
            Assert.Equal(plan.SampleRate, restored.SampleRate);
            Assert.Equal(plan.TotalFrameCount, restored.TotalFrameCount);
            Assert.Equal(plan.Ports[0].Events[1], restored.Ports[0].Events[1]);
            Assert.Equal(
                plan.UnitFragments[0].SemanticFingerprint,
                restored.UnitFragments[0].SemanticFingerprint);
            Assert.Equal(
                plan.UnitFragments[0].Events.ToArray(),
                restored.UnitFragments[0].Events.ToArray());
            Assert.Equal(plan.UnitFragments[0].PcmCacheKey, restored.UnitFragments[0].PcmCacheKey);
            Assert.Equal(
                plan.UnitFragments[0].PcmCachePayloadOffset,
                restored.UnitFragments[0].PcmCachePayloadOffset);
            Assert.Equal(plan.UnitFragments[0].PcmCacheHit, restored.UnitFragments[0].PcmCacheHit);
            Assert.Equal(plan.SourceIds.ToArray(), restored.SourceIds.ToArray());
            Assert.Equal(
                plan.InitiallyDisabledSourceIndices.ToArray(),
                restored.InitiallyDisabledSourceIndices.ToArray());

            byte[] damaged = File.ReadAllBytes(firstPath);
            damaged[12] ^= 1;
            File.WriteAllBytes(firstPath, damaged);
            _ = Assert.Throws<InvalidDataException>(() => MidiRenderPlanFile.Read(firstPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 1)]
    public void RejectsChecksumValidInvalidPortAndReservedFields(int portRecordOffset, byte value)
    {
        string path = Path.Combine(Path.GetTempPath(), $"midora-plan-invalid-{Guid.NewGuid():N}.mdap");
        try
        {
            MidiRenderPlan plan = CreatePlan();
            MidiRenderPlanFile.Write(path, plan);
            byte[] bytes = File.ReadAllBytes(path);
            int firstPortOffset = 28
                + (plan.SourceIds.Length * sizeof(long))
                + sizeof(int)
                + (plan.InitiallyDisabledSourceIndices.Length * sizeof(int));
            bytes[firstPortOffset + portRecordOffset] = value;
            RewriteChecksum(bytes);
            File.WriteAllBytes(path, bytes);

            _ = Assert.Throws<InvalidDataException>(() => MidiRenderPlanFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsChecksumValidImpossibleSourceCountBeforeAllocation()
    {
        string path = Path.Combine(Path.GetTempPath(), $"midora-plan-count-{Guid.NewGuid():N}.mdap");
        try
        {
            MidiRenderPlanFile.Write(path, CreatePlan());
            byte[] bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(24, sizeof(int)),
                16 * 1024 * 1024);
            RewriteChecksum(bytes);
            File.WriteAllBytes(path, bytes);

            _ = Assert.Throws<InvalidDataException>(() => MidiRenderPlanFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsPreviousPortOnlyPlanVersion()
    {
        string path = Path.Combine(Path.GetTempPath(), $"midora-plan-version-{Guid.NewGuid():N}.mdap");
        try
        {
            MidiRenderPlanFile.Write(path, CreatePlan());
            byte[] bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeof(uint), sizeof(int)), 3);
            RewriteChecksum(bytes);
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => MidiRenderPlanFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RewriteChecksum(Span<byte> bytes)
    {
        const int checksumByteCount = 32;
        int payloadLength = bytes.Length - checksumByteCount;
        _ = SHA256.HashData(
            bytes[..payloadLength],
            bytes[payloadLength..]);
    }

    private static MidiRenderPlan CreatePlan()
    {
        const long sourceId = 7_001;
        MidiPortRenderPlan port = new(0,
        [
            new ScheduledMidiMessage(0, MidiMessage.ProgramChange(0, 0), 0),
            new ScheduledMidiMessage(100, MidiMessage.NoteOn(0, 60, 100), 0),
            new ScheduledMidiMessage(500, MidiMessage.NoteOff(0, 60, 17), 0)
        ]);
        MidiUnitFragmentRenderPlan fragment = new(
            0,
            0,
            trackId: sourceId,
            segmentId: 7_002,
            eventInstrumentId: 7_003,
            instanceGroupId: 7_004,
            subVoiceId: 7_005,
            sourceIndex: 0,
            startFrame: 0,
            endFrame: 1_000,
            semanticFingerprint: new string('a', 64),
            port.Events,
            pcmCacheKey: new string('b', 64),
            pcmCachePayloadOffset: 1_024,
            pcmCacheHit: true);
        return new MidiRenderPlan(48_000, 1_000, [port], [sourceId], [0], [fragment]);
    }
}
