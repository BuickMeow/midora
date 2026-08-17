# Midora Desktop Editor and Event Instrument UX Requirement Trace

## Scope and authority

- Product-owner request dated 2026-08-17: point-set Logical Parameter editing, Event Instrument clipboard/duplicate support, per-instrument editor session state, color editing and timeline color projection, consistent selection/conductor interaction, custom dialogs, English UI, configuration fixes, and binding/playback reliability.
- SRS anchors: 17.2.3, 17.6, 18.1.3-18.1.4, 18.2.5, 18.3-18.4, 18.7-18.8, 20.3.7, 20.11, 20.13 and 20.15; cross-system invariants are governed by section 22.
- Explicit product-owner override: this increment adds copy/paste for Event Instruments even though SRS 20.6.5 excludes them from the ordinary object clipboard. SRS text is not changed. Paste remains an explicit project edit and creates fresh stable IDs.

## Inputs and formal outputs

- Inputs: Project Event Instruments, Logical Tracks, Segments, Logical Parameter lanes, Conductor events, current editor tool/modifiers, and session-only viewport state.
- Formal Project outputs: Event Instrument clones, binding/color/configuration edits, Logical Parameter points, and Conductor/selection edits produced through Application edit commands and Undo/Redo.
- Presentation outputs: point-set lane snapshots, fixed-size Conductor event glyphs, normalized Segment palette derived from the bound Event Instrument color, raw track-header color stripe, custom modal dialogs, and English UI text.

## Boundaries and ownership

- Project source owns instruments, colors, bindings, Segment data, points, and Conductor events. Stable IDs remain identity.
- Per-instrument zoom, scroll, selected lower editor and splitter heights are session UI state only; they are neither persisted in `.midora` nor compiled.
- Clipboard payloads are copy-time snapshots. Paste/duplicate remap every contained stable ID and preserve internal references only through that remapping.
- Instrument color projection changes presentation only. It must not affect compilation, playback, MIDI export, or audio rendering.
- Playback continues to consume only the current canonical compiled result; a stale background result must never be published for a newer Project revision.

## Edge and failure conditions

- Invalid loop or color input does not partially mutate the Project; loop fields commit only after both values parse and validate.
- Rebinding an already-bound track requires explicit confirmation after the drag operation has completed.
- Unmodified Select-mode blank clicks clear object selection; Ctrl/Alt marquee semantics remain unchanged.
- Parameter point edits clamp/validate tick and value through Application commands and commit as one undoable edit.
- Modal errors and confirmations use Midora-owned windows. Operating-system file/folder pickers remain native integration surfaces.

## Diagnostics and non-goals

- User-facing UI text and diagnostics remain English. Invalid operations surface their complete English message through custom dialogs.
- This increment does not change canonical Event Instrument semantics, audio scheduling, `.midora` persistence format, or the SRS itself.
- It does not persist session viewport state across application restarts.

## 2026-08-17 workspace, playback recovery, and resource increment

- Inputs: open Project/workspace state, explicit Compile, track-header context, held-preview failures, and the currently verified embedded SoundFont runtime resource.
- Formal outputs: no new canonical musical semantics. The embedded SoundFont extraction command copies the exact embedded resource bytes to an explicitly selected external path and does not mutate Project source data. Track-wide Segment selection and workspace focus are session-only UI state.
- Workspace boundary: Arrangement is the permanent index-zero workspace for every open Project. It cannot be closed or reordered; other workspaces remain session-only and reorder no earlier than index one.
- Focus boundary: workspace and Event Instrument section switches focus their non-editing tab host. They must not implicitly focus a search field, editor, toggle, or command button, and background compile/diagnostic refresh must not change the active workspace.
- Playback failure boundary: recovery from a prior Preview error occurs before the next application playback task is registered. Stop also consults the Playback Controller state so a coordinator bookkeeping mismatch cannot turn Stop into a no-op. Reset Playback Engine remains an explicit user command.
- Failure conditions: missing/mismatched embedded SoundFont runtime data rejects extraction; cancellation or copy failure never publishes a partial destination; an existing destination requires picker-authorized overwrite.
- Diagnostics: a clean explicit compile leaves the current workspace unchanged. A completed compile with warnings or errors may open Diagnostics, without focusing its search input.

## Standalone Event Instrument interchange decision

- Feasibility: an Event Instrument payload is mostly self-contained. Its direct structural dependency is Project TPQN, while effective sound can still differ with the target Project SoundFont and Project reset defaults. Standalone interchange is nevertheless not only a UI command: it creates a public persisted format and requires a versioned container, stable-ID remapping, single/batch conflict policy, TPQN conversion and rounding rules, overflow/collision validation, transactional import, and unknown-version handling.
- Current authority: SRS 7.2.1, 7.20, 7.21, 16.29, 20.5.7, and 21.2 explicitly exclude standalone/cross-Project Event Instrument import/export from the initial release. No extension, manifest, compatibility contract, or TPQN resampling rule is currently specified.
- Decision for this increment: do not introduce an ad-hoc format. Recommended follow-up is one versioned batch-capable package whose manifest records source TPQN and format version, with one Event Instrument protobuf payload per entry; a single export is the same package with one entry. Import must allocate fresh stable IDs and remap every internal reference atomically.
- Required product decision: approve the standalone format/extension and exact rational TPQN conversion policy before implementation, because these choices affect file compatibility and audible timing.

## 2026-08-17 rejected direct-edit rollback and transport icon increment

- Inputs: direct Project-backed text, choice and Boolean edits in Event Instrument Configurations, the common Inspector and Project Settings. Text remains a local draft while the user is typing; a focus-loss or Enter commit, closed choice popup, or Boolean click is the submission boundary.
- Formal output: only a successfully validated Application edit command may change Project source or create Undo history. A rejected command produces no Project mutation and no Undo entry.
- Rejection presentation: after reporting the concrete failure, the submitting control is reprojected from the current Project model so it cannot continue to display a value that was never accepted. This includes Event Instrument configuration/lifecycle/overlap/isolation/loop/follow-velocity controls, common Inspector choices and Booleans, and Project Settings fields and choices.
- Boundary: modal creation/edit dialogs and the C# Mapping Function draft retain invalid local input for correction because those values are explicitly staged drafts rather than failed direct Project edits.
- Presentation-only output: Reset Playback Engine uses the filled Fluent System Icons `Flash 20` geometry stored in the shared icon resource dictionary. This changes neither playback behavior nor Project data.
