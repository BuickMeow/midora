# Midora Event Instrument lifecycle and editor fixes — requirement trace

Date: 2026-08-16

## Scope and sources

This change is based on SRS 9.1.3–9.1.6, 9.8.4–9.8.8, 10.1–10.11,
12.4–12.8, 14.8, 18.2.4, 18.5 and 18.6, plus the product-owner
decisions recorded on 2026-08-16. The implementation continues to follow:

```text
Project Source Data
→ Semantic Validation
→ Compilation
→ Canonical Compiled Result
→ Playback / Preview / MIDI Export / Audio Rendering
```

## Inputs

- Event Instrument Mapping Chains and their ordered Mapping Steps.
- Stateful SubVoice MIDI events, including their original held values.
- Event Instrument ADSR-like Envelope Presets, lifecycle strategies and Loop
  interval.
- Logical Note Gate, Segment hard boundary, Initial State and Project Reset
  Defaults.
- Current Project revision, Track → Event Instrument binding and Segment source
  identity used by full/incremental compilation and audio cache staging.
- Piano-roll Grid/Snap settings and a Draw placement gesture.
- Project metadata name and an SMF Type 1 export request.

## Formal outputs

- Deleting a deletable non-Note Mapping Chain removes its owning
  `SubVoiceEventMapping` or `LogicalParameterMapping`; Undo restores the same
  object, chain, steps, references and order. Note mappings remain mandatory.
- Mapping Step up/down uses the explicitly selected Mapping Step and preserves
  that selection after rebuilding the workspace.
- A stateful non-Note event mapping that consumes an Envelope is evaluated over
  the active/release lifetime. Its base value is the most recent original
  target value, or the effective Initial State/default before the first event;
  output is emitted only when the normalized MIDI value changes.
- A Note that starts before Loop Start and ends after Loop End remains active
  across loop iterations and is released by the lifecycle Gate/Release end.
  Notes wholly inside the loop keep their ordinary repeated NoteOn/NoteOff pairs.
- Ordinary instance/Gate end performs exact NoteOff and target Reset without
  CC120. CC120 All Sound Off is reserved for Segment hard end and explicit
  consumer task/range hard boundaries.
- A sounding Segment owns each enabled Unit lane through Segment End. Ordinary
  instance NoteOff therefore remains inside the fragment and SoundFont release
  samples continue into realtime, offline and cached PCM; isolated instances
  reuse deterministically colored lanes only when their formal lifecycles do
  not overlap.
- Full and incremental compilation produce the same canonical result after
  instrument edits and Track binding changes; semantic audio fingerprints/cache
  keys change whenever audible source semantics change.
- A snapped piano-roll Draw gesture has a minimum length of one active Snap
  unit, never one raw tick while Snap is enabled.
- The SMF conductor track Track Name is the Project Name; an invalid/blank
  defensive input falls back to `Conductor`.

## Boundaries and failure conditions

- Mapping Chain deletion still requires explicit confirmation when it contains
  Steps and never deletes referenced Mapping Functions, Envelopes or Logical
  Parameters.
- Explicit creation of, or retargeting to, a genuinely new non-Note event
  target may create its optional shared Mapping owner once. Later point
  editing/reinsertion and load repair do not recreate an owner the user
  explicitly deleted.
- Envelope-driven state output is limited to value-class non-Note MIDI targets;
  Note number/velocity remain event-time values. CC91/CC93 remain rejected.
- All generated ticks remain inside the instance and `[segmentStart,
  segmentEnd)` lifecycle boundary. Segment End still performs exact NoteOff,
  CC120 and Reset before Channel Unit reuse.
- Segment-owned lane reservation can increase peak Channel Unit use relative to
  lifecycle-only allocation. Exceeding 256 is a formal resource error; playback
  must not regain capacity by silently hard-cutting release samples.
- A non-zero Release reaches End Value on the final integer tick contained in
  its left-closed/right-open interval; NoteOff/Reset follows at Release End.
- Mapping failures remain structured compiler diagnostics with source IDs; a
  failed compile publishes no partial canonical result.
- Initial State and Reset Defaults remain separate. Reset Defaults do not seed a
  missing initial target value.
- MIDI export continues to fail atomically on invalid canonical input, range,
  path or SMF encoding; the name change does not alter Project source data.

## Persistence and runtime ownership

- Deleting an owning Mapping object, lifecycle/loop edits, Initial State,
  Project Reset Defaults, Track bindings and Project Name are Project source
  data and participate in History/Modified/persistence.
- Selection, Scenario Preview values, generated canonical events, compiler
  caches, semantic fingerprints, audio cache entries and export task paths are
  derived/runtime state and are not persisted in `.midora`.
- Scenario Preview is explanatory/test UI state only and creates no Project
  events.

## Explicit non-goals

- No compatibility path for earlier development-only `.midora` data is added.
- Reset Defaults are not redefined as Initial State.
- Loop does not stretch every Note inside the loop into one indefinite Note;
  only a Note whose template lifetime spans the loop boundary is held across it.
- No All Sound Off is added at ordinary Gate end, and no consumer reinterprets
  lifecycle semantics outside the compiler.
- This change does not introduce MIDI 2.0, new lifecycle strategies, arbitrary
  envelope points, or a second audio/cache semantic model.
