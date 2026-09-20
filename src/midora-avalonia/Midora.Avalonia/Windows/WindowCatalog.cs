using Avalonia.Controls;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Visual-review catalog of every ported Avalonia window. Used by the temporary
/// "Windows" menu in the main window and by the <c>--smoke-windows</c> startup probe.
/// </summary>
internal static class WindowCatalog
{
    internal sealed record Entry(string Group, string Name, Func<Window> Factory);

    public static IReadOnlyList<Entry> Entries { get; } =
    [
        new("File & Project", "New Project…", () => new NewProjectDialog()),
        new("File & Project", "MIDI Export…", () => new MidiExportDialog()),
        new("File & Project", "Audio Render…", () => new AudioRenderDialog()),
        new("File & Project", "Application Preferences…", () => new ApplicationPreferencesDialog()),
        new("File & Project", "About Midora", () => new AboutDialog()),

        new("Timeline Tools", "Batch Edit…", () => new BatchEditDialog()),
        new("Timeline Tools", "Batch Edit Help", () => new BatchEditHelpDialog()),
        new("Timeline Tools", "Batch Edit Presets…", () => new BatchEditPresetDialog()),
        new("Timeline Tools", "Batch Expression Editor", () => new BatchExpressionEditor()),
        new("Timeline Tools", "Humanize Selection…", () => new HumanizeSelectionDialog()),
        new("Timeline Tools", "Quantize Selection…", () => new QuantizeSelectionDialog()),
        new("Timeline Tools", "Split Notes…", () => new SplitNotesDialog()),
        new("Timeline Tools", "Note Split Presets…", () => new NoteSplitPresetDialog()),
        new("Timeline Tools", "Note Split Help", () => new NoteSplitHelpDialog()),
        new("Timeline Tools", "Batch Create…", () => new TimelineGenerationDialog()),
        new("Timeline Tools", "Batch Create Help", () => new TimelineGenerationHelpDialog()),
        new("Timeline Tools", "Batch Create Presets…", () => new TimelineGenerationPresetDialog()),
        new("Timeline Tools", "Transpose Selection…", () => new TransposeSelectionDialog()),
        new("Timeline Tools", "Scale Selection…", () => new ScaleSelectionDialog()),
        new("Timeline Tools", "Join Notes…", () => new JoinNotesDialog()),
        new("Timeline Tools", "Selection…", () => new SelectionDialog()),

        new("Tracks & MIDI", "Track Properties…", () => new TrackPropertiesDialog()),
        new("Tracks & MIDI", "MIDI Channel Root Settings…", () => new MidiChannelRootSettingsDialog()),
        new("Tracks & MIDI", "MIDI Target…", () => new MidiTargetDialog()),
        new("Tracks & MIDI", "Direct MIDI Lane Target…", () => new DirectMidiLaneTargetDialog()),
        new("Tracks & MIDI", "MIDI State Entry…", () => new MidiStateEntryDialog()),
        new("Tracks & MIDI", "New Raw MIDI Track…", () => new NewRawMidiTrackDialog()),
        new("Tracks & MIDI", "New Logical Track with Instrument…", () => new NewLogicalTrackWithInstrumentDialog()),
        new("Tracks & MIDI", "Instrument Selection…", () => new InstrumentSelectionDialog()),

        new("Instruments & Mapping", "Instrument Catalogs…", () => new InstrumentCatalogDialog()),
        new("Instruments & Mapping", "Parameter Mapping Properties…", () => new ParameterMappingPropertiesDialog()),
        new("Instruments & Mapping", "Logical Parameter Definition…", () => new LogicalParameterDefinitionDialog()),
        new("Instruments & Mapping", "Logical Parameter Event Binding…", () => new LogicalParameterEventBindingDialog()),
        new("Instruments & Mapping", "Mapping Function…", () => new MappingFunctionDialog()),
        new("Instruments & Mapping", "Mapping Function Code Editor", () => new MappingFunctionCodeEditor()),
        new("Instruments & Mapping", "Mapping Function Help", () => new MappingFunctionHelpDialog()),

        new("View & Misc", "Onion Settings…", () => new OnionSettingsDialog()),
        new("View & Misc", "Onion Sources…", () => new OnionSourcesDialog()),
        new("View & Misc", "Object Properties…", () => new ObjectPropertiesDialog()),
        new("View & Misc", "Color Picker…", () => new ColorPickerDialog()),
        new("View & Misc", "Text Input…", () => new TextInputDialog()),
        new("View & Misc", "Text Details…", () => new TextDetailsDialog()),
        new("View & Misc", "Message…", () => new MessageDialog()),
        new("View & Misc", "Value Trace Shape", () => new ValueTraceShapeSelectorPreviewWindow()),
    ];
}
