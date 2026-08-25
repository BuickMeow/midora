using Midora.Domain;

namespace Midora.Compiler;

public sealed record CSharpMappingDraftCompilationResult(
    bool Succeeded,
    string? ErrorMessage,
    IReadOnlyList<string> ReferencedContextFields)
{
    public static CSharpMappingDraftCompilationResult Success(
        IReadOnlyCollection<string> referencedContextFields) =>
        new(
            true,
            null,
            referencedContextFields.Order(StringComparer.Ordinal).ToArray());
}

/// <summary>
/// Validates an unapplied bounded Mapping Function expression with the same fixed ABI,
/// whitelist, limits, cache key, and expression compiler as formal Project compilation.
/// It does not mutate a Project and does not make the draft a formal consumer input.
/// </summary>
public sealed class CSharpMappingDraftCompiler : IDisposable
{
    private readonly MappingExpressionCompiler _compiler = new();
    private bool _disposed;

    public CSharpMappingDraftCompilationResult Compile(
        int abiVersion,
        string body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(body);

        MidoraProject owner = new(480);
        CSharpMappingFunction function = new(owner)
        {
            Name = "Draft",
            Body = body,
            AbiVersion = abiVersion
        };
        try
        {
            IReadOnlySet<string> fields = _compiler.GetReferencedContextFields(function);
            return CSharpMappingDraftCompilationResult.Success(fields);
        }
        catch (MappingException exception)
        {
            return new(false, exception.Message, []);
        }
    }

    /// <summary>
    /// Releases all compiled draft delegates. Desktop callers invoke this at Project-session
    /// boundaries so no draft compilation cache crosses Projects.
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
