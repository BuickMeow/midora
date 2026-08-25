using Midora.Mapping.Contract.V2;

namespace Midora.Compiler.Tests;

public sealed class CSharpMappingDraftCompilerTests
{
    [Fact]
    public void DraftUsesTheFormalBoundedExpressionAbiAndInfersDependencies()
    {
        using CSharpMappingDraftCompiler compiler = new();

        CSharpMappingDraftCompilationResult result = compiler.Compile(
            MappingExpressionAbiV3.Version,
            "context.GateLength > 192 ? Clamp(value, 0, 127) : 0");

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal([nameof(MappingContextV2.GateLength)], result.ReferencedContextFields);
    }

    [Fact]
    public void InvalidDraftReturnsTheFormalCompilerDiagnostic()
    {
        using CSharpMappingDraftCompiler compiler = new();

        CSharpMappingDraftCompilationResult result = compiler.Compile(
            MappingExpressionAbiV3.Version,
            "missingSymbol");

        Assert.False(result.Succeeded);
        Assert.Contains("missingSymbol", result.ErrorMessage, StringComparison.Ordinal);
    }
}
