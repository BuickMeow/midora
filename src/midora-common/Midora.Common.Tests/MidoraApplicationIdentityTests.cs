namespace Midora.Common.Tests;

public sealed class MidoraApplicationIdentityTests
{
    [Fact]
    public void ApplicationIdentityIsStableAndVersionIndependent()
    {
        Assert.Equal("Zacksony.Midora", MidoraApplicationIdentity.AppUserModelId);
        Assert.DoesNotContain("1.0", MidoraApplicationIdentity.AppUserModelId, StringComparison.Ordinal);
    }
}
