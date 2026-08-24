using Midora.Compiler;
using Midora.Domain;

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
        DateTimeOffset exportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(outputPlan);
        ArgumentNullException.ThrowIfNull(createdWithSoftwareVersion);
        ArgumentNullException.ThrowIfNull(lastSavedWithSoftwareVersion);
        ArgumentNullException.ThrowIfNull(exportSoftwareVersion);
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
            Notes = string.Empty,
            SoundFontDescription = null,
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
            Diagnostics = compiled.Diagnostics
                .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Info)
                .Select(diagnostic => new MidiExportReadmeDiagnostic(
                    diagnostic.Severity.ToString(),
                    diagnostic.Code,
                    diagnostic.Message))
                .ToArray(),
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
