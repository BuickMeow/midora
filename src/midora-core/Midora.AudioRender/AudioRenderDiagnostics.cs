using Midora.Domain;

namespace Midora.AudioRender;

public enum AudioRenderDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record AudioRenderDiagnostic(
    string Code,
    AudioRenderDiagnosticSeverity Severity,
    string Message,
    string? SourceKey = null,
    MidoraId TrackId = default,
    string? FinalPath = null,
    string? TemporaryPath = null);
