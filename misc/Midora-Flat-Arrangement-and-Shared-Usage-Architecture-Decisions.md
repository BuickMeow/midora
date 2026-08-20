# Midora Flat Arrangement and Shared Usage Architecture Decisions

Date: 2026-08-20
Status: Accepted
Scope: destructive development-time replacement; no legacy `.midora` compatibility

## ADR-CORE-046 / ADR-UI-041（已接受）：平铺 Arrangement 与共享执行身份

### Decision

Midora replaces the visible `Event Instrument / MIDI Channel Root → child Track` Arrangement tree with three orthogonal structures:

1. a global tagged Arrangement Track order;
2. internal Event Instrument Usage / MIDI Channel Root membership;
3. an independent Event Instrument Definition index.

Event Instrument Usage is an unnamed stable-ID object referencing one Definition. Logical Tracks reference a Usage. Multiple Tracks referencing one Usage share channel state and connected Segment lifecycle when per-note isolation is disabled. A zero-member Usage is deleted atomically; its Definition remains.

MIDI Channel Root remains the authoritative Unit identity. All Roots are non-empty. Fixed Root routing is presented as a Track property, but the Root stores the only authoritative route. Moving/deleting the final member deletes the Root in the same Undo transaction. Fixed members may be globally non-contiguous; Auto shared members must be contiguous and render as a brace block.

## Reasons

- Preserve imported SMF Track order exactly enough for cross-application work.
- Remove visually empty parent rows from Arrangement.
- Let multiple Logical Tracks intentionally share one materialized Event Instrument state and Unit allocation.
- Keep expensive Event Instrument Definitions reusable and independent from Track lifetime.
- Reuse existing Root/Segment audio cache and hard-boundary semantics instead of creating an unrelated MIDI execution path.
- Avoid a user-facing named group creation step for the common workflow.

## Rejected alternatives

- Keep the visible tree: cannot preserve arbitrary imported Track order and wastes vertical space.
- Store Port.Channel directly on every Track: creates conflicting duplicated authority for shared state.
- Make Definition itself the shared runtime owner: cannot express multiple independent usages of the same Definition.
- Keep empty Fixed Roots: exposes a reservation object users do not need and conflicts with Track-property UX.
- Flatten without internal owners: cannot represent Auto shared channels or shared Logical state.

## Consequences

- Persistence schema, protobuf descriptors and package index are replaced.
- Compiler allocation and overlap grouping move from Track/Segment binding to Usage connected intervals.
- Pure MIDI merge/SMF order use the global Track order rather than Root child order.
- Arrangement drag/drop can change order and owner membership in one atomic edit.
- Cache dirty ownership for shared Logical state moves to Usage.
- Event Instrument Definition deletion is blocked while referenced; deleting Tracks never deletes Definitions.

## Required verification

See SRS §24.14. Full/incremental equivalence, same-tick deterministic order, non-empty owner invariants, failure atomicity, route collision, shared-state cleanup and SMF order are release-blocking.
