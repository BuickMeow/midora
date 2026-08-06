namespace Midora.MidiExport;

internal enum MidiExportOutputFaultPoint
{
    BeforeStagingDirectory,
    BeforeArtifactWrite,
    BeforeSelfValidation,
    BeforePublish,
    BeforeRollback,
    BeforeCleanup
}

internal interface IMidiExportOutputFaultInjector
{
    void ThrowIfRequested(MidiExportOutputFaultPoint point, string path);
}

internal sealed class NoOpMidiExportOutputFaultInjector : IMidiExportOutputFaultInjector
{
    public static NoOpMidiExportOutputFaultInjector Instance { get; } = new();

    private NoOpMidiExportOutputFaultInjector()
    {
    }

    public void ThrowIfRequested(MidiExportOutputFaultPoint point, string path)
    {
    }
}
