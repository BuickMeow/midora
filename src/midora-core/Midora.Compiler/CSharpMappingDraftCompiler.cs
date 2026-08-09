using Midora.Domain;

namespace Midora.Compiler;

public sealed record CSharpMappingDraftCompilationResult(bool Succeeded, string? ErrorMessage)
{
    public static CSharpMappingDraftCompilationResult Success { get; } = new(true, null);
}

/// <summary>
/// Validates an unapplied C# Mapping draft with the same fixed ABI, Roslyn profile,
/// references, cache key, and collectible load context as formal Project compilation.
/// It does not mutate a Project and does not make the draft a formal consumer input.
/// </summary>
public sealed class CSharpMappingDraftCompiler : IDisposable
{
    private readonly CSharpMappingCompiler _compiler = new();
    private bool _disposed;

    public CSharpMappingDraftCompilationResult Compile(
        int abiVersion,
        string body,
        IEnumerable<string> declaredContextFields)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(declaredContextFields);

        MidoraProject owner = new(480);
        CSharpMappingFunction function = new(owner)
        {
            Name = "Draft",
            Body = body,
            AbiVersion = abiVersion
        };
        foreach (string field in declaredContextFields)
        {
            function.DeclaredContextFields.Add(field);
        }
        try
        {
            _ = _compiler.GetOrCompile(function);
            return CSharpMappingDraftCompilationResult.Success;
        }
        catch (MappingException exception)
        {
            return new(false, exception.Message);
        }
    }

    /// <summary>
    /// Releases all compiled drafts and their collectible load contexts. Desktop callers invoke
    /// this at Project-session boundaries so no draft compilation cache crosses Projects.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _compiler.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _compiler.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
