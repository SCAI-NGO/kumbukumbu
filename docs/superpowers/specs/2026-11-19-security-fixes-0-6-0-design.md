# The fixes of the 0.6.0 security review

Status: approved by Chris, 2026-11-19, decisions included.

## 1. Goal and decisions

0.6.0 has been tested three ways: the defensive security review (#262), the MCP and browser suite (#261), and Chris's hand tests, which follow on 2026-11-20 on the build this run and its sibling produce. This run fixes the review's findings and the UI tests' findings that live in the same code. Its sibling, `feature/fixes-0-6-0`, fixes the rest of the UI tests' findings, in parallel.

Decisions, each with Chris's reason:
- **Two runs side by side, not one after the other.** The UI tests' findings in the discreet code (F-10, F-11) and in the skills code (M-01) go with this run, so the two runs touch disjoint code and both pull requests are ready in the morning, before the hand tests.
- **Fixes run on Opus, not Mythos.** Mythos is the specialist for the review itself; fixing is ordinary engineering.
- **Security fixes keep their details private** until the release, as with 0.4.0 and 0.5.0. The private spec in `SCAI-NGO/kumbukumbu-security` describes them; this public spec names them by id only (§2).
- **0.7.0 starts with the Lows that need a decision** (agreed 2026-11-19): they are not in this run.

Kumbu²'s principles hold for every item: campy (notes are files and frontmatter, no stores beside them), human-centric (the screen speaks of what the person wants), invite and lead, and honest (nothing is hidden quietly; honesty is part of the security architecture).

## 2. Security fixes

The findings S6-01 to S6-24 and the Lows the private spec lists are fixed as the private spec says: `specs/2026-11-19-0.6.0-fixes-design.md` on the branch `fixes/0-6-0` of `SCAI-NGO/kumbukumbu-security`. It holds Chris's decisions and the order. Nothing of their details reaches this repository before the release: not a commit message, test name, comment or pull request.

## 3. A memory ceiling for the server's container

`compose.yaml` gives the server service a memory limit, `mem_limit: ${KUMBU_MEM_LIMIT:-<default>}`.

- **The default:** measured, not guessed. During the first end-to-end run, **before** the limit is added, take the peak of the server container (`docker stats`, container `kumbu-e2e-kumbu-1`). The e2e stack runs with search off, so add 1.5 GB for the embedding model; the default is twice that sum, rounded up to whole gigabytes, and never below `3g`. Write the measurement and the choice into the report. The PDF sidecar keeps its own `mem_limit` (`compose.yaml`).
- **Documented** in `README.md` (what it does, the format, the default, when to raise it: many users, large vaults) and in `.env.example`, as every variable is.
- Why: without a ceiling, a process that grows takes the whole host with it; with one, the container restarts and the host stays up. A deployment with more memory raises it in its `.env`.

## 4. The UI tests' findings in this run's code

From `docs/superpowers/overnighter/2026-11-19-tests-0-6-0-report.md`, section "Findings":

- **F-10 (High):** a hide from elsewhere never leaves a discreet vault's note in the clear: the address becomes the last ordinary note's (or `/`), and neither the palette, Recent nor the shell behind a window shows anything of the hidden vault, every time. The report's "Expected" is the target. Its two `test.fixme` tests in `web/e2e/discreet/discreet-0-6-0.spec.ts` become plain tests and pass in both end-to-end runs. The bug is intermittent, so the fix names the race it closes, and the two tests run ten times in a row on the stack without a failure (`--repeat-each=10`).
- **F-11 (decided by Chris):** ✕ on a window whose note was hidden shows **the vault list**, as the windows spec says (lines 135 and 168), not the last ordinary note. The windows fixme test asserts it.
- **M-01 (decided by Chris):** the system skill wins the bare name over any vault's `_skills/project-memory`: `skills cat project-memory` reads the system skill, as the project memory spec §1 says. A vault's skill of that name stays reachable by its path, and `skills ls` keeps listing both. **The person's own `/Skills/project-memory` still takes the system skill's place, as that spec says**: a test pins it. The test that pinned the ambiguity answer changes with it.

## 5. Housekeeping

- `docs/superpowers/open-items.md`: close the rows this run settles (F-10, F-11, M-01, and the review's row by id only, with no wording that links an id to a fix), and add a row with this run's pull request. Expect a merge conflict with the sibling run there; it is harmless.

## 6. End-to-end example

Alice has Home marked discreet and shown, with `/Home/Zahnarzt.md` open, and Settings › Vaults over it.
1. On her phone, she hides Home.
2. On the laptop, at once, the address leaves `/Home/Zahnarzt.md`; the window over it shows nothing of Home; the palette and Recent name no Home note (F-10).
3. She closes the window with ✕: the vault list shows (F-11).
4. Her agent asks `skills cat project-memory` and reads the system skill, although the team vault Work has a `_skills/project-memory` (M-01).

## 7. Anti-goals

- No new store, index or collection.
- Nothing about a discreet vault reaches `localStorage`, the address or the browser's history.
- No behaviour of an ordinary vault changes beyond what the private spec decides.

## 8. Security boundary

- **Not touched:** `Auth/`, `CallerMiddleware`, `Crypto/` and encryption at rest, the slots, sharing and roles, sessions and the password window, the PIN check, sync, OAuth, WebDAV's authentication, and both CSPs in `src/Kumbu.Server/Api/Security.cs`.
- The exception is what the private spec allows for its findings. It narrows what a caller can do; it never widens it.

## 9. Out of scope

- The sibling run's findings: F-05 to F-09, F-12 to F-14, M-02, M-03, `rm`'s wording. Its code (`PointMenu.tsx`, `useNoteMenuKey.ts`, `PublishDialog.tsx`, `PublicationBar.tsx`, `UploadTool.cs`, the creation time of moves) is left to it.
- The Lows the private spec keeps for 0.7.0.
- The UI tests' open points for Chris (the install hint on the phone, `graph --in/--out`, "(mv)" for members in `blame`): backlog.
- Dependabot and every dependency upgrade.
