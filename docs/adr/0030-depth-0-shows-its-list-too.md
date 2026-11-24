# 0030. The publish dialog lists the note at depth 0 too, and keeps 0 as "this note only"

- **Date:** 2026-11-25
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [Publish 2.0](../superpowers/specs/2026-11-18-publish-bundles-design.md) decisions 1 and 2

## Context

In the publish dialog, depth 0 showed no list ("This note only"), while depth 1 showed "What goes out" with the note itself, and for a note without links nothing more. So 0 and 1 looked like the same choice, one with the note listed and one without. The depth field also had the focus but not its value when the dialog opened, so typing a digit added to the depth instead of replacing it.

Making 1 the minimum and default was the first idea. Depth 1, though, also takes the notes the start note links to; only for a note without links are 0 and 1 the same. Depth 0 is also what every single-note publication is stored as, from before Publish 2.0 on, and what `publish add` and `--depth` mean in `sh` and over MCP.

## Decision

The dialog lists what goes out at every depth, at 0 the note alone, and asks the server for that list as at any other depth. The depth keeps its range of 0 to 5, 0 the default, in the dialog, the API, `sh` and MCP alike. The depth field opens selected, and is selected whenever it is entered, so that a digit replaces it.

## Why

- The inconsistency was the missing list, not the number: with the list at every depth, 0 and 1 differ in what they list, and only where they really differ.
- A minimum of 1 would make every new publication take the linked notes by default, more than today, and "this note only" would take unticking every linked note. Publishing less by default is the safer side.
- Reading a stored depth 0 as 1 would widen existing publications at their next update, to notes their owner never chose: refused outright.
- Counting the note itself as level 1 in the dialog only (1 to 6) would keep the behaviour but give the dialog and `sh publish --depth` two numbers for one thing, against one UX for both.

## Consequences

- Opening the dialog asks for the preview once more than before, at depth 0; it is the same walk, cheap at depth 0. A note marked unpublishable shows its refusal already when the dialog opens.
- Nothing changes on the server, in the stored data or in `sh`.
