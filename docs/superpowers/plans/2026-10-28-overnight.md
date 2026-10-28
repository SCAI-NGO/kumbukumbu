# Overnight 2026-10-28 — Brief

> **For the overnight agent:** this is a brief, not a plan. It names two packages, their order and the rules that hold for both. For each you write the plan yourself (superpowers:writing-plans) from the spec, against the code as it stands by then, and execute it (superpowers:subagent-driven-development, subagents preferred). Read this brief, `CLAUDE.md`, and each package's spec before starting it.

**Time:** starts around 22:00 on 2026-10-28; it may run until the evening of 2026-10-29. Take the time to do it well; don't take it to go round in circles. When something resists for more than three honest attempts, write down what you tried, choose the simplest acceptable way out (or leave it as a listed gap), and move on.

**Goal:** the rest of attachments for the omnibus release 0.2.5. People can already store and look at PDFs and images (part 1, merged in #81 and #84); tonight the server learns to measure itself (monitoring), and the model learns to read attachments with `img` and `pdf` (part 2). No release, no tag, no version bump tonight.

## Packages, in this order

1. **Monitoring.** Spec: `docs/superpowers/specs/2026-10-28-monitoring-design.md`.
2. **Attachments, part 2: the model reads attachments (`img`, `pdf`).** Spec: `docs/superpowers/specs/2026-10-28-attachments-model-reading-design.md`. It builds on part 1 as built on `master` (spec `docs/superpowers/specs/2026-10-27-attachments-foundation-design.md`, code from #81 and #84) and on monitoring. Its metrics need monitoring's code. If monitoring is held up, part 2 may start on everything else and add the metrics last.

Write each plan to `docs/superpowers/plans/2026-10-28-<topic>.md` and commit it before executing it.

## Branches and pull requests (Chris)

One branch and one **draft pull request against `master`** per package. Chris merges them himself in the GitHub web UI. You don't merge to `master`, tag or release.

- **`monitoring`**: from `master`.
- **`attachments-model-reading`**: from `monitoring`. Until Chris has merged monitoring, its pull request also shows monitoring's commits; say so in its description.
- No change to `kumbu-obsidian` is expected. If one turns out to be needed, it gets its own branch and draft pull request there.

## Global rules

- Everything in `CLAUDE.md`: British English, SPDX headers, no organisation, customer or employer names anywhere (test data and examples included), imperative commit messages without prefixes, each ending with the session trailer you were given.
- Tests first: xUnit for the server, Vitest for web and the PDF sidecar. Before calling a package done: `dotnet test --solution Kumbu.slnx`, `pnpm -C web install --frozen-lockfile`, `pnpm -C web test --run`, `pnpm -C web build`, plus the sidecar's tests and build. Everything green, or the failure written down with its output.
- Security first: validate at boundaries; untrusted bytes get limits before decoding; no secrets, note content, paths or names in logs or metrics. `docs/security-architecture.md` must stay true to the code: each package that changes behaviour described there updates it, with file:line references.
- New NuGet packages: update **both** lock files (`dotnet restore Kumbu.slnx --force-evaluate`); otherwise CI fails with `NU1004`.
- API changes: regenerate `web/src/api/schema.d.ts` against your **own** stack (a compose project of your own and a free port, `docker compose -p kumbu-<package> …`), then `down -v`.
- Docs as you go: `README.md` (env variables with format and example), `.env.example`, `docs/concept.md` §12 where a package changes a decision recorded there (part 2 does).
- Each package's "my call" points are decisions already made. "Left to the builder" is yours: write each choice down with one line of reason, in the plan or the hands-on note.

## Hands-on, per package

As in earlier steps: rebuild the stack, use what you built as a person (the web app, with a browser if you have one, otherwise through the API) and as an agent (MCP `sh`), and write down what you saw, including what is awkward: `docs/notes-monitoring-hands-on.md`, `docs/notes-attachments-model-hands-on.md`. Findings you don't fix go into the note as a list, so Chris can turn them into issues.

Things only Chris can test go into the note as a short checklist for him: Grafana from his own machine through the SSH tunnel, and the client test of `docs/attachments-client-research.md` §6 (the six-digit code per client, and the size steps).

## Review

After each package: a review of the package's whole diff (superpowers:requesting-code-review), fix what it finds, then open its draft pull request. At the end: a review across both, and fixes go onto the branch where the code lives.

## The report at the end

Each draft pull request's description, and at the end a short summary in the session for Chris (in German) with:

- what was built, per package;
- what is left open or deviates from a spec, and why;
- the hands-on findings that are not fixed;
- Chris's checklist;
- test results, with numbers.
