# Cross-Track Range-State Restore Requirement Trace

## Requirement basis

- SRS 12.4 and 13.8 require a nonzero playback/render range to restore the
  effective non-note MIDI state at the range start without replaying earlier
  notes.
- SRS 13.10 and 23.7 define shared Event Instrument Usage and MIDI Channel Root
  connected intervals as one channel-state lifecycle across their child Tracks.
- INV-050, INV-051 and INV-073 require all formal consumers to use the same
  canonical range semantics and shared Root/Usage lifecycle.

## Input and formal output

- Input: a compilation range starting inside an active shared Usage or MIDI
  Channel Root interval, including projects where note and state events are on
  different Tracks.
- Output: one canonical range-start state per effective semantic target, chosen
  by absolute tick, then Arrangement Track order, then source event order.
- Earlier NoteOn events are not restored. Recognized channel-mode SysEx remains
  on its existing Root-wide state path.

## Boundaries

- Historical state is considered only from the active connected Usage/Root
  interval containing the requested start tick.
- A child Segment ending before the requested start does not discard its held
  state while another child Segment keeps the shared interval active.
- At the shared interval boundary, existing Reset/default lifecycle semantics
  remain authoritative; state does not cross a disconnected interval.
- Monitoring filters still remove state owned only by disabled Tracks.

## Failure, diagnostics and persistence

- This change introduces no new diagnostic and no Project persistence field.
- Invalid Project data continues to fail through existing semantic validation.
- The behavior belongs to canonical compilation and paged canonical queries,
  not to UI or BASS-specific reinterpretation.
- PCM cache generations are advanced because older entries may encode incomplete
  nonzero-range state.

## Explicit non-goals

- No pre-range note retriggering.
- No state inheritance across disconnected Segment/Root/Usage lifecycles.
- No byte-level SMF running-status or opaque-event reinterpretation.
- No change to Mute/Solo persistence or canonical source content.

## Verification

- Fixed and Auto Root regression tests cover ended sibling state Tracks.
- Paged Pure MIDI regression covers state stored in an `.mpk` sibling Segment.
- Shared logical Usage regression covers an earlier sibling Track whose state is
  held by a later connected Segment.
- The opt-in `cyber-night.mid` gate compares Ch.1 range-start state at ticks
  26112 and 78000 against the effective state obtained from tick 0.
- The real BASS gate renders Ch.1 from tick 26112 with `i2D4BM-NoFx.sf2` and
  requires non-silent, fault-free PCM.
