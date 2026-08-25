namespace Midora.Common.Tests;

public sealed class MidoraWindowsApplicationIdentityTests
{
    [Fact]
    public void ApplicationIdentityIsStableAndVersionIndependent()
    {
        Assert.Equal("Zacksony.Midora", MidoraWindowsApplicationIdentity.AppUserModelId);
        Assert.DoesNotContain("1.0", MidoraWindowsApplicationIdentity.AppUserModelId, StringComparison.Ordinal);
    }
}
