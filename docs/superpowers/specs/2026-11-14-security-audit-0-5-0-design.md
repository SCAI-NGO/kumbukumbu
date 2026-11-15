# Security audit of 0.5.0

Status: approved by Chris, 2026-11-14.

## 1. Goal and decisions

Before 0.5.0 is released, an independent **defensive** review of everything that changed since 0.4.0. It checks the new code against the security promises of `docs/security-architecture.md` and the features' specs, and rates each weakness on the scale of the 0.4.0 audit.

**Blue team only.** This is code review and defensive testing of our own code. Chris is its maintainer. No exploit development, no attack payloads beyond the ordinary untrusted inputs a unit test feeds a parser, no attack chains, and nothing run against a live system. A weakness is shown by a test of the behaviour the code **should** have ("a read-only member's flag write is refused"), which fails today, or by a trace through the code. Ordinary unit-test inputs include the standard sanitiser test strings (`<script>`, `javascript:` links, event-handler attributes, SVG `foreignObject`, CSS `url()`) and bounded oversized or deeply nested inputs (YAML, Mermaid, Markdown). Each test asserts that the code neutralises or limits them. There is nothing obfuscated, no work on evading a filter, and no chaining of steps. Chris: blue-team work is what we do.

Decisions, each with Chris's reason:

- **This run only finds and reports; it fixes nothing.** Its findings go into **one** fix overnighter together with the UI-test findings, the points from Chris's hand tests and four small UX points. Chris's reason: one fix run instead of two means fewer merge conflicts, one review and one pre-release to test.
- **The exception is a High or Critical finding.** Once the second review confirms it, the run reports it at once (§6). Chris has it fixed right away, outside this run; it doesn't wait for the joint fix run.
- **The policy stays as for 0.4.0:** Medium and up is fixed before 0.5.0; a Low only if quick, the rest later.
- **Each 0.5.0 pull request had its own security review of its diff.** This audit looks at what lies between them: features meeting each other, the release as a whole, and patterns that recur.
- **The run is on Anthropic's servers, with the model `claude-mythos-5-2`.** Chris's reason: defensive security work is done in the open, where Anthropic can see it.

## 2. Scope

The diff `v0.4.0..v0.5.0-pre.1` (`8f3f4b55`, which staging runs), and the existing code it calls into wherever a check happens there (for example `CallerMiddleware`, the vault access check, the session's store, `Security.cs`). Code untouched since 0.4.0 is in scope only where a new feature reaches it in a new way. The end-to-end suite (`web/e2e/`, #200) is test code and isn't at the tag. Review it at the branch head (`git diff 8f3f4b55..HEAD`), and only for whether it can leak secrets or reach the image (`.dockerignore`).

| # | Area | PRs | Where |
|---|---|---|---|
| A | The AI link: a new **anonymous** read path | #192 | `src/Kumbu.Server/Publishing/` (`AiLinkApi.cs`, `AiLinks.cs`, `PublicationStore.cs`, `ReaderApi.cs`), `web/src/reader/`, `web/s.html`, the rate limits in `Api/Security.cs` |
| B | Rendering untrusted Markdown: Mermaid, embeds | #195 | `web/src/reader/mermaidRender.ts` and `MermaidDiagram.tsx` (the Mermaid path, `diagramAlt`, the source refusals), the rest of `web/src/reader/`, `web/src/note/`, both CSPs (read, not changed) |
| C | Linking: embeds in `cat`, block references, unlinked mentions, aliases | #195, #196 | `src/Kumbu.Core/Links/`, `src/Kumbu.Core/Sections/`, `src/Kumbu.Core/Notes/`, `src/Kumbu.Core/Sh/Commands/` (`cat`) |
| D | The calendar | #197 | `src/Kumbu.Core/Calendar/`, `src/Kumbu.Core/Board/` (frontmatter writes), `src/Kumbu.Server/Api/CalendarApi.cs`, `Accounts/CalendarSettings.cs`, `Accounts/AccountCallerZone.cs`, `web/src/calendar/` |
| E | Flags, the explorer, Recent changes, Take photo | #198 | `Api/FlagApi.cs`, `Api/RecentApi.cs`, `Api/NoteApi.cs` (the tree), `src/Kumbu.Core/Tags/`, `web/src/flags/`, `web/src/recent/`, `web/src/shell/`, `web/src/upload/` |
| F | Discreet vaults across the new views | all | Recent changes, What's on, the calendar, the badges (the OS app-icon one too), the tree's new fields, the flag filter's counts, the AI link, mentions and aliases across vaults; `web/src/discreet/`, `Vaults/VaultCatalog.cs` |
| G | Cross-cutting | all | **the console's read-only promise for every command** (the calendar run found that `calendar -- close …` slipped past it: look for the same pattern everywhere, `Api/ConsoleApi.cs`); resource exhaustion in the new parsers (YAML frontmatter, aliases, mentions, Mermaid sources); dependencies (`dotnet list package --vulnerable --include-transitive`, `pnpm -C web audit`, `pnpm -C pdf audit`, including #191's minor updates); secrets or vault and note names in logs, metrics and error texts; `src/Kumbu.Server/Skills/` (`SkillsApi.cs`, `SkillUpdatesApi.cs`, `SkillUpdates.cs`, `SkillUpdateTaker.cs`: the small update changes); `.dockerignore` and the e2e suite, at the branch head |

The threat model is `docs/security-architecture.md` §2. The trust boundaries to check, meaning who may see or do what:
- someone without an account who holds an AI link or a published link: they may read that copy and nothing more;
- a user with an account but no access to the vault: they see nothing of it;
- a member with a lower role, read-only Bob above all: he changes nothing;
- a holder of an API key, a WebDAV share, a device key or a connection limited to some vaults: only those vaults, at that right;
- a writer whose Markdown, frontmatter or Mermaid source others open: it is untrusted input, and it can't act in the reader's session or make it send requests;
- a person looking over the shoulder of a user with discreet vaults: nothing of a hidden vault shows.

## 3. The severity scale and the method

Unchanged from the 0.4.0 audit: `docs/superpowers/specs/2026-11-10-security-audit-0-4-0-design.md` §3 (the scale: impact times reachability) and §4 steps 1 to 4 (per area: the trust boundaries and promises; a review by a fresh subagent; evidence by a behaviour test or a precise trace; then an **independent second review** of every Medium-or-higher finding and of every Low described in public, which checks the claim against the code and may confirm it, rate it up, or refute it). Wherever the 0.4.0 spec's §3 to §5 speak of attackers, exploits, exploitability or an adversarial check, read this spec's terms instead: the trust boundary crossed (§2), **reachability** (who can reach the weak code, and with what), and the second review. One rule of §4 step 4 stays: a Low that will be described in public may only be **confirmed or rated up** by the second review, never down. The earlier per-diff reviews are in the reports `docs/superpowers/overnighter/2026-11-1{1,2,3,4}-*-report.md`. A finding they list as accepted is not new. One they list as fixed that isn't fixed is new.

**The 0.4.0 audit's confidential findings** are in the private repository: `audits/2026-11-10-0.4.0.md` on the branch `audit/0-4-0` (pull request #1), with their fixes on `fixes/0-4-0` (pull request #2). Neither is merged yet. A 0.4.0 finding still open, or one whose fix doesn't hold at `v0.5.0-pre.1`, is reported **under its 0.4.0 id, in the private file only**. It isn't a new finding.

## 4. Where the findings go

Of the 0.4.0 audit's §5, only its **publication rule** applies here: no detail of a Medium-or-higher weakness reaches a public branch, commit message or pull request before its fix is released. Its rules on fixes, regression tests on the public branch, fix commit messages and F11 don't apply, because this run fixes nothing. With the names changed:
- **Confidential findings** (Medium and higher) go to the private repository `SCAI-NGO/kumbukumbu-security`, branch `audit/0-5-0`, file `audits/2026-11-14-0.5.0.md`, the tests that show them under `audits/2026-11-14-0.5.0/`, with a pull request there.
- **The public report** goes to `docs/superpowers/overnighter/2026-11-14-security-audit-report.md` on the public branch `audit/0-5-0-security`. It has the counts per severity, the Low findings in full, and each confidential finding by id and severity only.
- Ids are `S5-NN` for Medium and higher and `L5-NN` for Low. They are numbered afresh, and the prefix keeps them apart from the 0.4.0 audit's ids.
- **A Medium-or-higher finding that the second review refutes or rates down stays in the private file only**, with the verdict. A Low goes into the public report only after the second review has confirmed it as Low, or refuted it.
- Each finding is written so the joint fix run's brief can link to it unchanged: id, title, area, severity with one sentence on impact and one on how reachable it is, the trust boundary it crosses, the evidence (test or trace), the second review's verdict, proposed fix with the tests that should prove it, and whether it needs a decision from Chris.

## 5. No fixes

The run changes no product code, public or private. Its only commits are: on the public branch, the plan, `docs/superpowers/open-items.md` and the report; in the private repository, `audits/`.

**Tests that show a Medium-or-higher weakness** are written and run in a separate worktree outside the public clone (`git worktree add <scratch>/audit-run v0.5.0-pre.1`), which is never committed from. Their source of truth is the private clone, under `audits/2026-11-14-0.5.0/`. They never touch the public working tree.

## 6. A High or Critical finding

As soon as the second review confirms it:
1. write it in full into the private file and push it;
2. send a `PushNotification`: "Security review 0.5.0: a High (or Critical) finding, S5-NN, details in the private pull request". It names no area and no detail;
3. move the run's card on the Factory board to **Waiting for Chris**, with the id, the severity and the question "Fix S5-NN now? Recommendation: yes, details in the private pull request";
4. carry on with the audit.

## 7. End-to-end example

1. Area G: the agent lists every command of `sh` and the web console's check (`Api/ConsoleApi.cs`) that refuses writes. The promise: the console only reads.
2. It writes a behaviour test: "the console refuses `mv` whatever options come first". Say it fails for `mv --dry-run a b`: the console doesn't refuse it, and `mv` writes.
3. Rating: impact, a write the same session may make anyway through the note view, so no right is gained and nothing hidden shows, but the console's promise to only read is broken. Reachability: it needs a signed-in session. **Low**, for that reason, with no doubt left: the calendar's fix (`WriteVerb`) suggests a general one.
4. A second review checks the claim against the code and confirms it. The finding goes into the public report as `L5-03`, with the proposed fix (one check that reads the line as the command does, for every command) and the tests that should prove it. The run fixes nothing.

## 8. Out of scope

- Fixing anything (§5).
- Testing against staging, production or any real host: everything runs locally against the test server and a test MongoDB.
- The Obsidian plugin, the deployment hosts, Komodo, the image signing.
- Dependency upgrades: a vulnerable package is a finding, and the upgrade goes to the fix run or to Dependabot.
- Code untouched since 0.4.0, except where §2 says it is in scope.
- Changing a design decision of an earlier spec. If one looks unsafe, it becomes a finding with "needs a decision from Chris".

## 9. Security boundary

The audit reads everything and changes no product code. What it may write: the plan, `docs/superpowers/open-items.md` and the public report on `audit/0-5-0-security`; `audits/` in the private repository; its own card on the Factory board, through the `Kumbu_Factory` connector, which can reach that vault only. Nothing about a confidential finding may go to a public branch, a commit message, a pull request or the Factory card.
