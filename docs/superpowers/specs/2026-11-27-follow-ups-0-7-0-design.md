# The follow-ups of the 0.7.0 fixes

Status: approved by Chris, 2026-11-27, decisions included.

## 1. Goal and decisions

The fix run of 0.7.0 (#352) left a short list of points open for Chris. He decided them on 2026-11-27; this run builds what he decided, so that `v0.7.0-pre.6` holds them. Decisions, each with Chris's reason:

- **One small run for all of them.** They are follow-ups of one run, mostly a few lines each; one run means one review and one pre-release to test.
- **Security points keep their details private** until the release, as with every version before. The private spec in `SCAI-NGO/kumbukumbu-security` describes them: `specs/2026-11-27-0.7.0-follow-ups-design.md` on the branch `fixes/0-7-0-follow-ups`. This spec names them by id only.

Kumbu²'s principles hold for every item: campy (notes are files and frontmatter, no stores beside them), human-centric, honest (nothing is hidden quietly; honesty is part of the security architecture), and offline: what needs a connection says so before, dimmed, not hidden.

## 2. The security review's points

L7-10, L7-11, L7-12, L7-17, L7-20, L7-24, L7-28, S7-10, S7-16, S7-19, S7-21 and one further point of the review, as the private spec says.

## 3. F-15's last review point

`e85ca9a` (the fix that keeps a full check owed when an incremental one was queued before it; the hash is on `origin/feature/fixes-0-7-0`, and on `master` the line is `if (full) this.fullOwed = false;` at `packages/eidolon-core/src/engine/engine.ts:435`) has no committed test of its own. **Passes:** a Vitest test in the Eidolon core that queues an incremental check before the owed full check has run and asserts that the full check still runs and clears the debt only when it was a full one; it fails with the line removed.

## 4. End-to-end example

Alice keeps Work offline. A cut-off upload leaves a full check owed (F-15); before it runs, an incremental check is queued by a reconnect. The incremental one runs and the debt stays; the full one runs next and clears it; the new note is uploaded without a reload. The test of §3 shows exactly this sequence with the engine's fakes.

## 5. Anti-goals

- No new feature, no new notice in the UI: a point that would need one is recorded instead (it needs a UX review first).
- No edit of an ADR: a changed decision gets a new record.

## 6. Security boundary

Not touched: authentication, sessions and their cookies, the key derivation and wrapping of the offline copy, sharing and roles, discreet vaults' semantics, except where the private spec says. No new dependency, no upgrade.

## 7. Out of scope

- The Obsidian plugin's copy of the Eidolon core (moves after the release).
- The documentation that lands with the release.
- Everything of the fix run's report not named here; the hand-test residuals stay in the backlog.
