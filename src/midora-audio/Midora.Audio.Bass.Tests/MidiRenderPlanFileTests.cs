using Midora.Midi;

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

    private static MidiRenderPlan CreatePlan()
    {
        MidiPortRenderPlan port = new(0,
        [
            new ScheduledMidiMessage(0, MidiMessage.ProgramChange(0, 0)),
            new ScheduledMidiMessage(100, MidiMessage.NoteOn(0, 60, 100)),
            new ScheduledMidiMessage(500, MidiMessage.NoteOff(0, 60, 17))
        ]);
        return new MidiRenderPlan(48_000, 1_000, [port]);
    }
}
