# MIDI Export README Note Count Requirement Trace

## Requirement

- Remove the program-level SoundFont placeholder from MIDI export `README.md`.
- Render `Notes` as the exact MIDI Note On event count produced by the frozen export compilation.
- Keep the field format consistent with the read-only Project Settings statistic: one decimal count, without Markdown quote markers.

## Formal flow

```text
Frozen MIDI Export CompileContext
→ CanonicalCompiledResult.TotalNoteOnEventCount
→ MidiExportReadmeFactory
→ MidiExportReadmeBuilder
→ README.md: "- Notes: <count>"
```

## Boundaries

- The count belongs to the frozen export compilation, including its selected range and tracks; it is not a raw Project object count.
- SoundFont is an Application Preference and is excluded from Project and MIDI export semantics.
- README generation does not alter canonical events, MIDI bytes, Project data, Undo/Redo, or Application Preferences.

## Failure and validation

- A negative Note On count is rejected as an invalid README snapshot.
- A failed export compilation cannot produce a successful README.

## Persistence and runtime ownership

- The count is transient export-task data and is written only to the requested sidecar `README.md`.
- No new persisted Project field or runtime audio state is introduced.
