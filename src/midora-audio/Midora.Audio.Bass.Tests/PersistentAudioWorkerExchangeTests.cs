namespace Midora.Audio.Bass.Tests;

public sealed class PersistentAudioWorkerExchangeTests
{
    [Fact]
    public void RequestAndResponseAreGenerationScopedAndRoundTripExactly()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-persistent-exchange-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const long generation = 17;
            string[] expectedArguments = ["", "设备", "48000", "path with spaces"];
            PersistentAudioWorkerExchange.WriteRequest(
                directory,
                generation,
                expectedArguments);

            Assert.Equal(
                expectedArguments,
                PersistentAudioWorkerExchange.ReadRequest(directory, generation));
            PersistentAudioWorkerResponse expected = new(
                Succeeded: true,
                ActualSampleRate: 48_000,
                ActualDeviceBufferFrameCount: 512,
                Error: string.Empty);
            PersistentAudioWorkerExchange.WriteResponse(directory, generation, expected);
            Assert.True(PersistentAudioWorkerExchange.TryReadResponse(
                directory,
                generation,
                out PersistentAudioWorkerResponse actual));
            Assert.Equal(expected, actual);

            PersistentAudioWorkerExchange.DeleteExchange(directory, generation);
            Assert.False(PersistentAudioWorkerExchange.TryReadResponse(
                directory,
                generation,
                out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
