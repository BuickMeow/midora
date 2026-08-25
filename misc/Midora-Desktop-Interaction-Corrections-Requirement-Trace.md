# Desktop Interaction Corrections Requirement Trace

## Inputs and formal outputs

- Mapping Function create/edit opens a self-contained modal expression dialog; opening the dialog must never depend on a style resource scoped only to `MainWindow`.
- Event Instrument structure-header icon buttons remain visible after the approved one-device-pixel optical size reduction and continue using the shared Fluent icon template.
- While a `ComboBox` drop-down is open, mouse-wheel input belongs to that drop-down surface whether or not its content currently needs a scrollbar. The gesture must not scroll an ancestor settings, properties, or workspace panel.
- When the Event Instrument bottom keyboard owns the active held Preview, the primary transport button executes Stop. Its preliminary pointer handling must not stop the Preview and then reinterpret the same click as Play.
- Optional Event Instrument object-reference choices use the explicit label `None`; they do not present an imperative `Select an object...` placeholder as though a reference were required.

## Boundaries and failure behavior

- These corrections change no Project source model, canonical result, persistence format, mapping expression ABI, audio rendering semantics, or Undo/Redo behavior.
- Mapping Function dialog construction failure is a UI failure and must be prevented before any Project command is attempted.
- Drop-down wheel isolation applies through the shared `ComboBox` template. A custom non-`ComboBox` popup remains responsible for its own wheel routing.
- Held Preview cleanup remains selective: stopping the Event Instrument keyboard Preview must not stop or start main timeline playback as a side effect.

## Diagnostics, ownership, and non-goals

- Validation errors remain local to the Mapping Function dialog until OK succeeds.
- Drop-down state and held Preview ownership are runtime UI/application-task state and are not persisted.
- The `None` label changes only the presentation of an already-supported null reference; it does not make a required Mapping Step reference optional.
- No change is made to wheel behavior for menus, context menus, completion popups, or ordinary list controls.
- No `computer-use` verification is part of this change.
