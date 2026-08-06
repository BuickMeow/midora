namespace Midora.Persistence;

internal enum MidoraPackageFaultPointV1
{
    BeforeContainerOpen,
    BeforeManifestRead,
    BeforeBackup,
    BeforeContentWrite,
    BeforeZipWrite,
    BeforeSelfValidation,
    BeforePublish,
    BeforeBackupCleanup,
    BeforeStagingDirectoryCleanup
}

internal interface IMidoraPackageFaultInjectorV1
{
    void ThrowIfRequested(MidoraPackageFaultPointV1 point, string path);
}

internal sealed class NoOpMidoraPackageFaultInjectorV1 : IMidoraPackageFaultInjectorV1
{
    public static NoOpMidoraPackageFaultInjectorV1 Instance { get; } = new();

    private NoOpMidoraPackageFaultInjectorV1()
    {
    }

    public void ThrowIfRequested(MidoraPackageFaultPointV1 point, string path)
    {
    }
}
