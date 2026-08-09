using Midora.Mapping.Contract.V2;

namespace Midora.Compiler.Tests;

public sealed class CSharpMappingDraftCompilerTests
{
    [Fact]
    public void DraftUsesTheFormalAbiAndRoslynProfileWithoutMutatingAProject()
    {
        using CSharpMappingDraftCompiler compiler = new();

        CSharpMappingDraftCompilationResult result = compiler.Compile(
            MappingAbiV2.Version,
            "return context.GateLength > 192 ? value : 0d;",
            [nameof(MappingContextV2.GateLength)]);

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public void InvalidDraftReturnsTheFormalCompilerDiagnostic()
    {
        using CSharpMappingDraftCompiler compiler = new();

        CSharpMappingDraftCompilationResult result = compiler.Compile(
            MappingAbiV2.Version,
            "return missingSymbol;",
            []);

        Assert.False(result.Succeeded);
        Assert.Contains("missingSymbol", result.ErrorMessage, StringComparison.Ordinal);
    }
}
