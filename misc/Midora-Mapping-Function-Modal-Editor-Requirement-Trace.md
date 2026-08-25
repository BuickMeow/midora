# Mapping Function Modal Editor Requirement Trace

## Input and formal output

- Input: an Event Instrument Mapping Function name and one ABI v3 numeric expression entered in a modal dialog.
- Formal output: one atomic Project edit containing the normalized name, validated expression, ABI v3, and automatically inferred Mapping Context dependencies.
- Create and update use the existing Project commands; the dialog never mutates Project objects directly while typing or validating.
- Approved `System.Math` methods and constants are available both as implicit names (`Sin`, `PI`) and qualified names (`Math.Sin`, `Math.PI`); both forms bind to the same ABI v3 allowlist.

## Boundaries and failure behavior

- The edit buffer belongs only to the open dialog. Cancel, title-bar close, Project replacement, or process exit discards it.
- Validate uses the same bounded ABI v3 compiler as formal compilation and does not change Project, Undo/Redo, Modified state, persistence, or canonical output.
- OK validates before submitting. Syntax/allowlist/limit/name/uniqueness/command failures keep the dialog open and display the error in its result area.
- Successful OK produces one Project Undo unit; `.midora` contains only the successfully submitted source model.
- The removed independent Mapping Function Workspace, cross-window Draft, Apply Draft, and Discard Draft paths are not fallback behavior.

## Diagnostics, runtime ownership, and preview priority

- Project diagnostics continue to describe the last successfully submitted Project version. Draft validation errors are local dialog feedback.
- Diagnostic navigation selects the Mapping Function in its owning Event Instrument and opens the same modal editor.
- Event Instrument bottom-keyboard Held Preview is runtime-only state. Before a modal window, context/menu surface, or Tab/Workspace transition opens, the UI synchronously stops only that owned keyboard preview task.
- Main timeline playback and other preview task types are not stopped by this cleanup path.

## Explicit non-goals

- No change to Batch Edit expressions.
- No free C# statements, arbitrary .NET calls, multiline source, persisted compiled delegate, or Project-level draft recovery.
- The Math implicit import is not a general `using static`: unknown methods, types, and members remain unavailable.
- No change to Mapping ABI v3 evaluation semantics or canonical consumer flow.
