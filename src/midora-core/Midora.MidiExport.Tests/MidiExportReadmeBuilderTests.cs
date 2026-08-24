using System.Text;

namespace Midora.MidiExport.Tests;

public sealed class MidiExportReadmeBuilderTests
{
    [Fact]
    public void BuildsDeterministicStrictUtf8ReadmeFromFrozenTaskSnapshot()
    {
        MidiExportReadmeRequest request = CreateRequest();

        byte[] first = MidiExportReadmeBuilder.Build(request);
        byte[] second = MidiExportReadmeBuilder.Build(request);
        string markdown = new UTF8Encoding(false, true).GetString(first);

        Assert.Equal(first, second);
        Assert.False(first.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.DoesNotContain('\r', markdown);
        Assert.EndsWith("\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Mode: Per Port", markdown, StringComparison.Ordinal);
        Assert.Contains("- Notes: 12345", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("SoundFont:", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  >", markdown, StringComparison.Ordinal);
        Assert.Contains("- Range: \\[120, 480\\)", markdown, StringComparison.Ordinal);
        Assert.Contains("- Midora Port 3 → output Port 1", markdown, StringComparison.Ordinal);
        Assert.Contains("GS and Yamaha XG Normal Part", markdown, StringComparison.Ordinal);
        Assert.Contains("exactly one original Midora Channel Unit", markdown, StringComparison.Ordinal);
        Assert.Contains("Port 03.mid", markdown, StringComparison.Ordinal);
        Assert.Contains("2026-08-06T12:34:56.1234567Z", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsErrorDiagnosticsAndInvalidSnapshotRanges()
    {
        MidiExportReadmeRequest source = CreateRequest();
        MidiExportReadmeRequest withError = Clone(
            source,
            diagnostics: [new("Error", "E001", "failed")]);
        MidiExportReadmeRequest invalidRange = Clone(source, startTick: 500, endTick: 100);

        Assert.Throws<ArgumentException>(() => MidiExportReadmeBuilder.Build(withError));
        Assert.Throws<ArgumentOutOfRangeException>(() => MidiExportReadmeBuilder.Build(invalidRange));
    }

    private static MidiExportReadmeRequest CreateRequest() => new()
    {
        ProjectName = "Project *One*",
        ProjectVersion = "v1",
        AuthorOrTeam = "Midora contributors",
        OriginalWork = "Original",
        Copyright = "Copyright",
        NoteOnEventCount = 12_345,
        Mode = MidiExportMode.PerPort,
        RangeSource = MidiExportRangeSource.Manual,
        StartTick = 120,
        EndTick = 480,
        Routing = MidiExportRoutingStrategy.Compact,
        TicksPerQuarterNote = 192,
        TempoEventCount = 2,
        TimeSignatureEventCount = 1,
        KeySignatureEventCount = 0,
        Tracks =
        [
            new("2", 2, "Bass", false, "not selected"),
            new("1", 1, "Lead", true)
        ],
        PortMappings = [new(3, 1, "Port 03.mid")],
        Diagnostics = [new("Information", "I001", "No issue")],
        FileNames = ["Port 03.mid", "README.md"],
        CreatedWithSoftwareVersion = "0.1.0",
        LastSavedWithSoftwareVersion = "0.1.0",
        ExportSoftwareVersion = "0.1.0",
        ExportedAtUtc = new DateTimeOffset(2026, 8, 6, 12, 34, 56, TimeSpan.Zero)
            .AddTicks(1_234_567)
    };

    private static MidiExportReadmeRequest Clone(
        MidiExportReadmeRequest source,
        long? startTick = null,
        long? endTick = null,
        IReadOnlyList<MidiExportReadmeDiagnostic>? diagnostics = null) => new()
        {
            ProjectName = source.ProjectName,
            ProjectVersion = source.ProjectVersion,
            AuthorOrTeam = source.AuthorOrTeam,
            OriginalWork = source.OriginalWork,
            Copyright = source.Copyright,
            NoteOnEventCount = source.NoteOnEventCount,
            Mode = source.Mode,
            RangeSource = source.RangeSource,
            StartTick = startTick ?? source.StartTick,
            EndTick = endTick ?? source.EndTick,
            Routing = source.Routing,
            TicksPerQuarterNote = source.TicksPerQuarterNote,
            TempoEventCount = source.TempoEventCount,
            TimeSignatureEventCount = source.TimeSignatureEventCount,
            KeySignatureEventCount = source.KeySignatureEventCount,
            Tracks = source.Tracks,
            PortMappings = source.PortMappings,
            Diagnostics = diagnostics ?? source.Diagnostics,
            FileNames = source.FileNames,
            CreatedWithSoftwareVersion = source.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = source.LastSavedWithSoftwareVersion,
            ExportSoftwareVersion = source.ExportSoftwareVersion,
            ExportedAtUtc = source.ExportedAtUtc
        };
}
