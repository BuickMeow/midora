# MIDI Export Effects-Off Initialization Requirement Trace

## Input

- Frozen MIDI Export Canonical Compiled Result.
- Frozen SMF Track Projection and output Port mapping.
- The single MIDI Channel owned by each emitted event MTrk.

## Formal output

Every emitted Logical Unit or Pure MIDI event MTrk receives these relative-tick-zero events:

```text
Control Change 91 = 0
Control Change 93 = 0
```

The Conductor MTrk receives neither event because it has no MIDI Channel.

## Ordering and boundaries

```text
Track structure Meta
→ optional Channel 10 GS/XG initialization
→ CC91=0
→ CC93=0
→ frozen canonical/opaque events
```

- Whole Project, Per Logical Track, Per Port, paged and non-paged encoders use the same rule.
- A Pure MIDI user CC91/CC93 at relative tick 0 remains present after the exporter initialization and therefore takes precedence.
- The exporter does not add the events to the Canonical Compiled Result or change compiler validation.

## Failure and diagnostics

- Existing encoding validation and atomic failure behavior remain unchanged.
- No new diagnostic is produced for the fixed exporter initialization.

## Persistence and runtime ownership

- The two events exist only in the exported SMF bytes.
- They do not enter `.midora`, Project source data, Undo/Redo, compile statistics, playback, audio rendering, canonical fingerprints, or Application Preferences.

## Explicit non-goals

- Do not change BASSMIDI `NOFX` behavior.
- Do not clear any other controller.
- Do not remove, coalesce, or reinterpret Pure MIDI CC91/CC93 source events.
