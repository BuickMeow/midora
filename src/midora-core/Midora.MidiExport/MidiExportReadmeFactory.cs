using Midora.Compiler;
using Midora.Domain;
using System.Collections;

namespace Midora.MidiExport;

public static class MidiExportReadmeFactory
{
    public static MidiExportReadmeRequest Create(
        MidoraProject project,
        MidiExportCompilationResult compilation,
        MidiExportFrozenOutputPlan outputPlan,
        MidiExportRangeSource rangeSource,
        string createdWithSoftwareVersion,
        string lastSavedWithSoftwareVersion,
        string exportSoftwareVersion,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(outputPlan);
        ArgumentNullException.ThrowIfNull(createdWithSoftwareVersion);
        ArgumentNullException.ThrowIfNull(lastSavedWithSoftwareVersion);
        ArgumentNullException.ThrowIfNull(exportSoftwareVersion);
        cancellationToken.ThrowIfCancellationRequested();
        if (!compilation.Succeeded)
        {
            throw new ArgumentException(
                "A success README.md cannot be created from a failed MIDI export compilation.",
                nameof(compilation));
        }
        if (!outputPlan.Succeeded || outputPlan.Mode != compilation.Mode)
        {
            throw new ArgumentException(
                "The README.md output plan is failed or belongs to another MIDI export mode.",
                nameof(outputPlan));
        }

        CanonicalCompiledResult compiled = compilation.CompiledResult;
        return new()
        {
            ProjectName = project.Metadata.ProjectName,
            ProjectVersion = project.Metadata.ProjectVersion,
            AuthorOrTeam = project.Metadata.AuthorOrTeam,
            OriginalWork = project.Metadata.OriginalWork,
            Copyright = project.Metadata.Copyright,
            NoteOnEventCount = compiled.TotalNoteOnEventCount,
            Mode = compilation.Mode,
            RangeSource = rangeSource,
            StartTick = compiled.StartTick,
            EndTick = compiled.EndTick,
            Routing = compilation.Routing,
            TicksPerQuarterNote = compiled.TicksPerQuarterNote,
            TempoEventCount = compiled.Conductor.Tempos.Length,
            TimeSignatureEventCount = compiled.Conductor.TimeSignatures.Length,
            KeySignatureEventCount = compiled.Conductor.KeySignatures.Length,
            Tracks = compilation.Tracks.Select(track => new MidiExportReadmeTrack(
                track.StableSourceKey,
                track.ProjectDisplayOrder,
                track.DisplayName,
                track.Participates,
                track.ExclusionReason)).ToArray(),
            PortMappings = BuildPortMappings(compilation, outputPlan),
            Diagnostics = MidiExportReadmeDiagnosticProjection.Create(compiled.Diagnostics, cancellationToken),
            FileNames = outputPlan.Targets.Select(target => target.FileName).ToArray(),
            CreatedWithSoftwareVersion = createdWithSoftwareVersion,
            LastSavedWithSoftwareVersion = lastSavedWithSoftwareVersion,
            ExportSoftwareVersion = exportSoftwareVersion,
            ExportedAtUtc = exportedAtUtc
        };
    }

    private static MidiExportReadmePortMapping[] BuildPortMappings(
        MidiExportCompilationResult compilation,
        MidiExportFrozenOutputPlan outputPlan) =>
        compilation.UsedZeroBasedPorts.Select(port =>
        {
            int original = port + 1;
            string? fileName = compilation.Mode == MidiExportMode.PerPort
                ? outputPlan.Targets.Single(target => target.SourceKey == $"port:{original:D2}").FileName
                : null;
            return new MidiExportReadmePortMapping(
                original,
                compilation.Mode == MidiExportMode.PerPort ? 1 : original,
                fileName);
        }).ToArray();

}

/// <summary>Frozen canonical diagnostics; repeated values are projected on demand.</summary>
internal sealed class MidiExportReadmeDiagnosticProjection(
    IReadOnlyList<CompilerDiagnostic> source) : IReadOnlyList<MidiExportReadmeDiagnostic>
{
    public static MidiExportReadmeDiagnosticProjection Create(
        IReadOnlyList<CompilerDiagnostic> source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is CompilerDiagnosticList compact)
        {
            int included = checked(compact.CountSeverity(DiagnosticSeverity.Warning)
                + compact.CountSeverity(DiagnosticSeverity.Info));
            if (included == 0) return new(CompilerDiagnosticList.Empty);
            if (included == compact.Count) return new(compact);
            return new(compact.Filter(
                static value => value.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Info,
                cancellationToken));
        }
        List<CompilerDiagnostic> diagnostics = [];
        for (int index = 0; index < source.Count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            CompilerDiagnostic diagnostic = source[index];
            if (diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Info)
                diagnostics.Add(diagnostic);
        }
        return new(diagnostics.ToArray());
    }

    public int Count => source.Count;
    public MidiExportReadmeDiagnostic this[int index] => Project(source[index]);
    public IEnumerator<MidiExportReadmeDiagnostic> GetEnumerator()
    {
        foreach (CompilerDiagnostic diagnostic in source) yield return Project(diagnostic);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    private static MidiExportReadmeDiagnostic Project(CompilerDiagnostic value) =>
        new(value.Severity.ToString(), value.Code, value.Message);
}
