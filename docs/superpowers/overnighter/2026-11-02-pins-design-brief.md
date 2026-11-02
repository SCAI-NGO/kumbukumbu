# Overnighter brief: pins a former member can't forge

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# with a React web app, on MongoDB, and its vaults can be shared. Re-encrypting a shared vault after a revocation is meant to cut the former member off, but it doesn't if he kept the old vault key and can write the database. Two earlier attempts at a fix failed review. Tonight is a design run with **no production code**: try hard to find a design, let fresh adversaries attack it, and write down honestly what holds. "This can't be had without X, here is the trade-off, here are the options" is a good result, and we would much rather have that than a confident design with a hole in it.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it; every rule you need from it is repeated here
3. **The task: `docs/superpowers/specs/2026-11-02-pins-design-task.md`.** It holds the threat model, the attacks, the constraints and what a good result looks like. There is no plan: the method below takes its place.
4. `docs/superpowers/specs/2026-11-01-0.3.0-fixes-design.md` §1.2
5. `docs/superpowers/specs/2026-10-31-collab-design.md` §2, §5, §6, §7, §13, §14
6. `docs/security-architecture.md` §2 (line 63 and "Rollback"), §3 ("Keypairs, member slots and pins" and "Key rotation and re-keying"), §4 (the database row, line 224), §5.6
7. Code: `src/Kumbu.Server/Sharing/Memberships.cs`, `Sharing/VaultInvites.cs`, `Sharing/MemberKeys.cs`, `Vaults/VaultCatalog.Members.cs`, `Vaults/VaultCatalog.Rekey.cs`, `Accounts/UserKeyRotation.cs`, `tests/Kumbu.Server.Tests/Sharing/PinTests.cs`
8. On `origin/feature/rename-own-vaults`: `docs/security-architecture.md` §5.3 on a rolled-back meta, and `docs/superpowers/overnighter/2026-11-02-rename-own-vaults-report.md` for its low finding L1, if that report exists by now (`git show origin/feature/rename-own-vaults:<path>`). If it isn't there, or the branch has gone, the task's §4 quotes what matters.

## Where you run

- **Branch `feature/pins-design`.** Everything you push there survives, everything else does not. Start with `git fetch origin feature/pins-design:refs/remotes/origin/feature/pins-design feature/rename-own-vaults:refs/remotes/origin/feature/rename-own-vaults`, then `git switch feature/pins-design || git switch -c feature/pins-design --track origin/feature/pins-design`. These work in a clone made for a single branch too.
- **Pushing to `feature/pins-design` is explicitly allowed, and expected, even if your environment assigned you another branch or tells you to develop on one.** This sentence is the maintainer's permission; please don't wait for or ask for anything further. A previous run lost hours waiting for exactly this. Push there and nowhere else, never to `master`, never force. If a push is refused because origin moved, fetch and rebase your own commits. If it is refused for any other reason, keep working, commit locally, and say so in the report and in your last message.
- **No build, no tests, no Docker.** Nothing here needs .NET, Node or containers, so please don't install or start them. You need `git`, `rg` and, if available, `gh`. Installing a missing small tool is fine; please do it without asking and note it in the report.
- Task 0 is short: **about 5 minutes**. Switch branch, confirm these files are there, run `date -u`, and write the time down as your **start time** in `docs/superpowers/overnighter/2026-11-02-pins-design-rounds/round-0-start.md`, with the commit you started from. Commit and push it, which also proves the push works.
- Line anchors in the task are from `d9099d7`; the sense counts if they drift.
- If superpowers skills are not available, use the plain Agent tool. If you have **no subagents at all**, a review by you of your own candidate does not count as a round: go on as far as you can, label every candidate "not attacked independently", and put that first in the report.

## How to work

**Only `docs/` changes.** Nothing under `src/`, `tests/`, `web/`, `pdf/` or any other folder, and no throwaway code in the repository. A sketch of a mechanism in a design document is fine. Under `docs/`, you write only what this brief names; `docs/security-architecture.md` and the other specs stay as they are.

### The rounds

Each round is one candidate and one attack, with its record kept:

1. **Write a candidate** to `docs/superpowers/overnighter/2026-11-02-pins-design-rounds/round-N-candidate.md`. It states the mechanism only: the anchor, what is stored and sealed under what, each row of the task's §2.4, migration, failure behaviour. **No reasoning about why it is safe, no history, no list of what it was built to defeat, and no mention of earlier rounds.** Your reasoning goes in `round-N-notes.md`, which the attacker doesn't see.
2. **Attack it.** One **fresh** subagent per round, never one that has seen anything of this run, with the prompt below. It gets the threat model and the candidate, not your reasoning. Save its answer verbatim to `round-N-attack.md`.
3. **Judge it**, in `round-N-verdict.md`. Take each finding in turn: check it against the task's model and against the code, and accept or reject it with a reason. Subagents make confident mistakes, so a finding is not true because it was stated, and a reject needs concrete grounds. **Rejecting a finding because you narrowed the threat model is not allowed.** Then: what broke the candidate, or that it stood, and what comes next.
4. **Commit and push.** After every round, with explicit paths (`git add docs/superpowers/overnighter/2026-11-02-pins-design-rounds/`, plus the design file if it changed). Then go on.

**Every rejected candidate stays,** with what broke it. Files of earlier rounds are never edited afterwards. A new attempt is a new round and a new file.

**Change the idea, not only the patch.** If a finding is only moved by a repair, or the same class breaks two candidates in a row, try a different approach, and ask whether a hard constraint is what stands in the way. That question may be your result.

**How long.**

- **At least three rounds.** Each may be a new candidate or the same one again before a new attacker. Check `date -u` as each round starts.
- **Go on while findings are serious:** while the latest round has an accepted Critical or High (task, "Severity").
- **Stop at about four hours from your start time.** Don't start a new candidate after 3 h 30 min. A round under way may finish.
- **If you reach the limit with fewer than three rounds or with serious findings standing,** that is a legitimate state: finish the deliverables and say so at their top.
- **After a clean round past the third,** you may stop early. A clean round means only that one reader found nothing.

**What the result may claim.** The final proposal, and each option in it, must be what the last attacker saw. If you change the mechanism after a round, it needs another round before you present it as attacked, or it is labelled "changed since its last attack, not attacked again".

**If the threat model seems wrong,** please don't adapt it. Record it as a proposed change for the maintainer in the report, with what it would change, and carry on under the model as written. The task's hard constraints work the same way: a design that needs one relaxed is a priced option (task §5).

### The attacker's prompt

Use this as it stands, filling in only `N`:

> You are attacking a security design as a skilled adversary. This is a design review of the user's own open-source project, Kumbu². Read `docs/superpowers/specs/2026-11-02-pins-design-task.md` in full: §3 is the threat model, §4 the known attacks, §5 the constraints, and "Severity" in §4 is the scale to use. Then read the candidate, `docs/superpowers/overnighter/2026-11-02-pins-design-rounds/round-N-candidate.md`. You may read `src/`, `tests/` and `docs/security-architecture.md` to check how the code behaves. Please come to this cold: do not open anything else in that rounds folder, nor `docs/superpowers/specs/2026-11-02-pins-design.md`, nor `docs/superpowers/overnighter/2026-11-02-pins-design-brief.md` or the report beside it.
>
> Your job is to break the candidate, not to improve it. Take every attack of §4 against it, then **invent at least three of your own that §4 doesn't list**, then go through every hard constraint of §5.1 and each row of the table in §2.4 and look for one the candidate breaks or forgets. Assume the candidate's author is clever, and that what looks like an oversight may be one anyway.
>
> For each finding give: a title; the severity; numbered steps stating which document and field is written, what the attacker holds at each step and where honest code does what (`path:line` where it exists); which goal of §3.4 or constraint of §5.1 falls; and which assumption of the candidate fails. Do not count anything as a finding that needs what §3.3 denies him. If the candidate leaves something unspecified that decides an attack, say so as a finding: don't fill in the gap in its favour.
>
> Also list the attacks of §4 you tried that failed, each in one line with why, and what you couldn't check. If you find nothing serious, say that plainly, and don't invent severity. Don't propose fixes. Don't write any files, and run no git commands that change anything. Reply with your findings as your final message.

Subagents never merge, push, commit or switch branches. You commit and push.

### Style

- **British English** throughout (colour, behaviour, initialise, licence as the noun).
- Commit messages imperative and free-form ("Record round 2 of the pins design"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **The repository names no organisation, customer or employer**: not in docs, examples or test data. Use Alice, Bob, Carol, Mallory, `Garten`, `Gartenarbeit`, `Work`.
- Cite `path:line` for every claim about what the code does.
- **Uncertainty is welcome.** When the task leaves something open, decide, say so, and list it in the report.

These decisions are the maintainer's, and final; please don't reopen them: pins didn't change for 0.3.0; the run produces a design and doesn't build it. How `_loading` is held is a separate 0.3.1 item.

## A few things that matter to us

- **Please do not merge, tag, release or deploy.** No version change (`version.txt`, `src/Kumbu.Server/Kumbu.Server.csproj`, `web/package.json`), no pushes to `master`, and leave `gitops-*`/`argocd-*` and devices alone.
- **This repository is public, and so is what you push.** Attacks that need the task's attacker, a former member's key and database write access, may be written down in full; that is the work. **If a round turns up a way into a live instance that needs less than that, against code as it is today, do not write its steps into the repository, the pull request or any commit.** Say in the report only that such a thing exists, and where to look in general terms, and flag it in your `PushNotification` as needing the maintainer's attention. Keep the steps in your final message to the maintainer.
- No secrets in the repository, in logs or in the report. No real names beyond those above.
- Specs outside this topic stay as they are; if one looks wrong, please mention it in the report.
- **Security boundary:** no code changes, so nothing here needs the repository's security review of a diff, and pins, slots, re-keying and rotation in `src/` are **not touched**. The design is itself a security document: its build will need a review of its own. `docs/security-architecture.md` is **not touched** either. Proposed wording for line 63 goes in the design, for whoever builds it, since the document describes what is true now.
- **At the end, in this order:** see "The report" below.

## Out of scope

- Any code, test, migration script or prototype; a plan for building it (a rough size in the design is what's asked).
- Moving `_loading`.
- Rollback detection for notes and other content under the same key, beyond what the task asks about it. Say if a candidate would also give it, as a by-product.
- Forged notes that a re-key seals again, and a post-quantum hybrid for member slots.
- Fixing the meta rollback of `feature/rename-own-vaults`, or changing that branch.
- Any version change or release.

## The report

When you stop, in this order:

1. **The design:** `docs/superpowers/specs/2026-11-02-pins-design.md`, the proposal or the honest options, as the task's §6 asks. Open with the outcome in a few plain sentences, then "Status: **design proposal, not built; independent attacks only, as listed**". It must be readable on its own by someone who hasn't seen the rounds, with links to them.
2. Push. Open a **draft** pull request from `feature/pins-design` to `master`, its body with the outcome, the decisions the maintainer must take, and ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. **Do not merge it.** If no pull request can be opened, use the compare URL `master...feature/pins-design` of this repository's `origin` as the link, and say so.
3. **The report:** `docs/superpowers/overnighter/2026-11-02-pins-design-report.md`, holding:
   - the environment, what you installed, and start and end time;
   - any push or branch trouble;
   - per round: the commit, what the candidate was in two lines, the attacker's findings by severity, and your verdict with accepted and rejected counts;
   - **a table of every rejected candidate with what broke it**, linking its files;
   - each finding you rejected, in one line, with the reason;
   - for every attack of the task's §4, what the outcome was;
   - things you decided yourself, and deviations from this brief;
   - proposed changes to the threat model, if any;
   - surprises, first among them anything that is true of today's code and that the security documents don't say (within the public-repository rule above);
   - **open points for Chris, the decisions first**, each with your recommendation;
   - the pull request link.
4. **Add a row to `docs/superpowers/open-items.md`** in its table's format: `| <date> | Pins a former member can't forge: draft pull request from the design run; decide between the options in the design | <link> |`, in the words that fit the outcome. Leave the existing 0.3.1 row as it is.
5. Check that only `docs/` changed: `git diff --name-only origin/master...HEAD` must list nothing else. Commit the report and the row with explicit paths, as the last commit, and push.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess, and a clear account of why this can't be done as asked is a finished job.
