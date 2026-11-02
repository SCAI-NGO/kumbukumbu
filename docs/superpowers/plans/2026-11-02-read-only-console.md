# Read-only console: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (or, if it is unavailable, emulate it with the plain Agent tool). One fresh subagent per task, then a spec-compliance review and a code-quality review, each by its own subagent. Subagents do red, green, commit and mutations; they never merge, push or switch branches. The controller pushes. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A page `/console` in the web app that runs the reading `sh` commands for a signed-in person, with `cd` and `pwd`, and that cannot change a vault.

**Architecture:** One session-only endpoint, `POST /api/console`, checks a server-side allowlist against the shell's own parser and then calls the real `Shell`. The browser keeps the working directory, turns relative paths into absolute ones, and shows what it sent on a `→` line. Nothing is kept in browser storage.

**Tech stack:** .NET 10, minimal APIs, xUnit v3 on Microsoft.Testing.Platform, Testcontainers with MongoDB; React with Vite, zustand, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-02-console-design.md` ("spec §n"), reviewed and approved by the maintainer. Don't edit it. Read-only is the maintainer's decision and final: please don't add a way of writing, however small.

**Line anchors** are from commit `85fafd2`'s parent `c38664e`, plus documents. They may drift as you work; the sense counts.

## Global constraints

- Branch `feature/read-only-console`. **Do not merge.** One **draft** pull request to `master`. No tags, no version change (`version.txt`, `src/Kumbu.Server/Kumbu.Server.csproj`, `web/package.json`), no pushes to `master`.
- British English in code, comments, docs, UI copy and commit messages. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`. Match the surrounding naming, idiom and comment density; comments only where the reasoning isn't obvious.
- The repository names no organisation, customer or employer anywhere, tests and examples included. People: Alice, Bob. Vaults: `Gartenarbeit`, `Work`.
- Commit messages imperative and free-form ("Let the console refuse what writes"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- `Manual`, the tool descriptions, `Help`'s text, the shell's hints and its error sentences do not change. `CallerMiddleware` does not change: the cookie stays refused on `/mcp`.
- Neither commands nor output go to a log, and nothing of the console goes to `localStorage` or `sessionStorage`.
- `web/src/api/schema.d.ts` is edited **by hand**, as generated output would have it.

## Review focus

Each has its test in the task that owns the code.

1. A refused command in disguise (`"rm" /x`, `r\m /x`, `cat /a | rm /b`, `rm --help`, `head | mkdir /x`): nothing runs, the vault is unchanged (Task 1).
2. Paths with spaces, umlauts and quotes (`cd "Projekte 2026"`, `cat "Bewässerung's Plan.md"`): the `→` line reads back to the same words (Tasks 3, 4).
3. The working directory stops existing, or the vault goes: the next command's error shows, `cd /` still works (Task 5).
4. Double Enter, typing while a command runs, leaving during a command, and an answer arriving after sign-out: one run only, and a late answer never lands in the next user's console (Task 5).
5. A very long unbroken line in the output: it wraps and the page doesn't scroll sideways (Task 5).

## How every task runs

1. **Red:** write the tests the task names; run only them. Expected: non-zero exit, the named tests failing for the reason the task gives. A compile error counts as red only for a member the task creates. "Zero tests ran" is a failure of the command, not red.
2. **Green:** the smallest change that passes; the task's command again. Expected: exit 0, 0 failed.
3. **Commit** locally.
4. **Mutations**, one at a time, those the task lists: make the breakage, run the task's command, see at least one test fail, restore with `git checkout -- <file>`. A mutation that survives means the test is too weak: strengthen it and say so in the report.
5. **Reviews, then push:** spec compliance, then code quality, each by its own subagent; fixes go back through 1 to 4; then the controller runs `git push -u origin feature/read-only-console`.

Test filters are Microsoft.Testing.Platform's: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*Name"`, repeatable, ORed. `FullyQualifiedName~` doesn't work, and a glob in mid-name selects nothing. Web: `pnpm -C web test --run src/console`.

**Full verification** at each phase gate (after Task 2, after Task 5, and in the final task), all of it, as CI does. With your own MongoDB, give each run a fresh one (`docker rm -fv kumbu-test-mongo`).

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

`pnpm -C web build` is the web CI gate: it runs the type check, which `pnpm test` does not. Run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.

Test fixtures: `tests/Kumbu.Server.Tests/KumbuAppFactory.cs` (`[Collection("app")]`, `app.PublicApi`), `tests/Kumbu.Server.Tests/Api/RestClient.cs` (`RestClient.NewAsync(app.PublicApi, "Work")`, `CreateAsync`, `KeyAsync`, `SendAsync`, `GetAsync`, `JsonAsync`, `ErrorAsync`), `tests/Kumbu.Server.Tests/Sharing/Share.cs` (`ShareWithAsync`). An MCP client in tests: see `tests/Kumbu.Server.Tests/Metrics/LabelGuardTests.cs`. Web: `web/src/test/app.tsx`'s `fakeApi`, `json`, `noContent`, `renderApp`.

---

## Task 0: baseline

Nothing here changes code. Time budget: **15 minutes** for setup and first build and tests; the last run here needed about 25 with Docker troubles. If it takes much longer, note it and carry on.

- [ ] Tools: `dotnet --version` satisfies `global.json` (10.0.100, roll forward); `echo $DOTNET_CLI_TELEMETRY_OPTOUT` prints `1`; `node --version` is 26; `pnpm --version` is 10.32.1 (`npm install -g pnpm@10.32.1` if missing); `docker info` works.
- [ ] `git log --oneline -3` shows documents-only commits (spec, plan, brief) on top of `c38664e`.
- [ ] Run the full verification.

**Expected, exit 0 for each.** From CI run 41518422552 of `c38664e` on `master`; confirm with `gh run view 41518422552 --log | rg "total:|succeeded:|skipped:|Tests "`. Measured on the planning machine at `85fafd2`: web 85 files, 1368 tests, passed; `pnpm -C web build` succeeds; Core tests total 1372, 2 skipped; `tests/Kumbu.Server.Tests` builds. The server's tests weren't run there, as it has no Docker.

| Suite | Expected |
|---|---|
| `dotnet test` | **total 3408, succeeded 3404, skipped 4, failed 0** |
| Skipped, Core (2) | `UnigramTokenizerTests.MatchesHuggingFaceOnArcticL`, `UnigramTokenizerTests.MatchesHuggingFaceOnTheRealVocabulary` |
| Skipped, Server (2) | `OpenAiCompatibleDriverTests.TensorX_ranks_by_meaning`, `SearchIndexTests.The_real_model_ranks_by_meaning` |
| Web (`pnpm -C web test --run`) | **85 files, 1368 tests**, all passed |
| Sidecar (`pnpm -C pdf test`) | **6 files, 78 tests**, all passed |
| Builds | succeed |

Known failures before you start: **none**. Running as root can skip 2 more. Write the measured numbers into the report.

---

## Phase A: the server

## Task 1: the endpoint and the read-only rule

**Files:**
- Modify: `src/Kumbu.Core/Sh/Shell.cs` (after `:62`)
- Modify: `src/Kumbu.Server/Mcp/ToolResult.cs:68-100`
- Create: `src/Kumbu.Server/Api/ConsoleApi.cs`
- Modify: `src/Kumbu.Server/Program.cs:264` (map it)
- Modify: `tests/Kumbu.Server.Tests/Api/PasswordWindowTests.cs:126` (`DeliberatelyOpen`)
- Test: `tests/Kumbu.Server.Tests/Api/ConsoleApiTests.cs`

**Interfaces:**
- Produces, Core: `public bool HasCommand(string name)` on `Shell`.
- Produces, Server: `internal static Task<(ShellResult Result, KumbuMetrics.ToolError Kind)> ToolResult.GuardAsync(Func<Task<ShellResult>> body)`, the present `RunBodyAsync` renamed; MCP's behaviour stays exactly the same.
- Produces, HTTP: `POST /api/console`, body `{ "call": "sh", "command": "ls /" }`, answer `ConsoleView`:

```csharp
// Defaults are required: Program.cs:132 sets RespectRequiredConstructorParameters, as AccountApi.cs:84 shows.
public sealed record ConsoleRequest(string? Call = null, string? Command = null, string? Path = null);
public sealed record ConsolePartView(string Type, string? Text, string? MediaType, string? Data); // "text" or "image"; Data base64
public sealed record ConsoleVersionView(string Path, string Token);
public sealed record ConsoleView(ConsolePartView[] Parts, bool IsError, ConsoleVersionView? Version);
```

- Produces: `ConsoleApi.Allowed` and `ConsoleApi.Refused`, `internal static readonly FrozenSet<string>`, exactly the lists in step 3; `internal static ConsoleView ConsoleApi.View(ShellResult)`.

- [ ] **Step 1: write the tests in `ConsoleApiTests`** (`[Collection("app")]`). Vault `Work`, with a note `/Work/plan.md` holding `# Plan\nTomaten\n` created through `RestClient`.
  - `Runs_a_reading_command`: `cat /Work/plan.md` answers 200, `isError` false, a text part containing `Tomaten`, and `version.path` equal to `/Work/plan.md`.
  - `Pipes_run`: `cat /Work/plan.md | rg -c Tomaten` answers `1`.
  - `A_checkpoint_can_be_read`: with a checkpoint made through the REST API or an API key's `sh`, `cat --checkpoint=<name> /Work/plan.md` still runs.
  - `Refused_commands_still_run_over_mcp`: `mkdir`, `mkcp` and `rm`, with a maintain key through `RestClient.ShAsync`, do what they always did.
  - `A_busy_vault_is_503`: using `HoldRekeyAsync`, as `tests/Kumbu.Server.Tests/Sharing/AliasApiTests.cs:110-115` does: 503 with `Retry-After`, for `sh`.
  - `Nothing_of_a_command_reaches_the_log`: with `CapturedLog`, as `tests/Kumbu.Server.Tests/Api/AccountApiTests.cs:188-189` does, send a marker in a command, in a note's text read by `cat`, and in a line that errors; no log line holds it.
  - `The_body_is_limited`: the endpoint's metadata (`Endpoints(...)`, as `PasswordWindowTests`) has `IRequestSizeLimitMetadata.MaxRequestBodySize` 65536. Don't expect a 413 through `TestServer`, which doesn't enforce it.
  - `Every_refused_command_stays_refused` (`[Theory]`). Generate three rows from each of `restore /Work/plan.md`, `mv /Work/plan.md /Work/b.md`, `mkdir /Work/new`, `rm /Work/plan.md`, `mkcp /Work/plan.md cp1`, `rmcp /Work/plan.md cp1`: the command alone, `cat /Work/plan.md | <command>` and `<command> | head`. Add `"rm" /Work/plan.md`, `r\m /Work/plan.md`, `'r'm /Work/plan.md`, `rm --help`. Each: 200, `isError` true, text exactly `<name>: not in this console, which only reads`. Afterwards `GET` the note: same version, not in the trash, no `/Work/b.md`, no folder `/Work/new`, no checkpoint.
  - `Lines_the_shell_answers_itself`: `grep x` gives the shell's own `command not found: grep — use rg (try 'help rg')`; `ls / > x` gives its `'>' is not supported` sentence; the empty line gives `sh: empty command`.
  - `Every_shell_command_is_classified`: no `Shell` can be resolved outside a request (`tests/Kumbu.Server.Tests/Metrics/ToolMetricsTests.cs:21`), so send `help` through the console and parse its `Commands:` block, as `ToolMetricsTests.cs:26-27` does. Every name there, and in `KumbuMetrics.KnownCommands` (`src/Kumbu.Server/Metrics/KumbuMetrics.cs:488`), is in exactly one of `Allowed` and `Refused`; each name in those two sets is in `KnownCommands`.
  - `The_lists_are_disjoint`: no name is in both `Allowed` and `Refused`; `help` and `rg` are allowed, `rm` isn't.
  - `An_api_key_is_refused`: with a key from `KeyAsync`, 403 and the sentence `src/Kumbu.Server/Api/Security.cs:148` gives.
  - `A_forged_origin_is_refused`: session with `Origin: https://evil.test`, as `PasswordWindowTests.cs:495` does: 403.
  - `A_read_only_member_reads_a_shared_vault`: Bob, member with role `read` (`ShareWithAsync`), can `cat` Alice's note, and still gets the refusal for `rm`.
  - `An_unknown_call_is_400`: `{ "call": "write" }` and `{ "call": "board" }` are 400, and a missing `command` for `sh` is 400.
  - `Errors_say_what_mcp_says`: for `cat /Work/nope.md`, `grep x` and `ls | foo`, the text is equal to what the `sh` tool returns over MCP with an API key on the same vault.
  - `Images_become_image_parts`: `ConsoleApi.View` of a `ShellResult` whose `Parts` hold a `TextPart` and an `ImagePart` gives a text part and an image part with `mediaType` and base64 `data`, in order.
  - `Tool_metrics_do_not_count_the_console`: scrape before and after a console `sh` (`tests/Kumbu.Server.Tests/Metrics/Scrape.cs`): `Scrape.Value(text, "kumbu_mcp_tool_calls_total{tool=\"sh\"}")` is unchanged. The `sh`-command counter may move, as the shell's observer reports every run.
- [ ] **Step 2: red.** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ConsoleApiTests"`. Fails: `ConsoleApi` doesn't exist.
- [ ] **Step 3: implement.**
  - `Allowed`: `ls`, `cat`, `outline`, `rg`, `semsearch`, `img`, `pdf`, `links`, `backlinks`, `tags`, `board`, `head`, `tail`, `wc`, `sort`, `history`, `diff`, `trash`, `lscp`, `uname`, `help`. `Refused`: `restore`, `mv`, `mkdir`, `rm`, `mkcp`, `rmcp`. The check is an allowlist: `shell.HasCommand(name) && !Allowed.Contains(name)` refuses. `Refused` exists only for the classification test and the local help's agreement.
  - Handler: `Pipeline.TryParse(command, ...)`; on success, the first stage that is refused answers `ShellResult.Error($"{name}: not in this console, which only reads")` with nothing run; everything else goes through `ToolResult.GuardAsync(...)` and `View`. Inside the lambda, catch `VaultBusyException` and throw `new ApiException(503, e.Message, ApiErrors.BusyRetryAfter)`: it derives from `VaultUnavailableException` (`src/Kumbu.Server/Vaults/Credentials.cs:135`), which `GuardAsync` would turn into a 200, and spec §4 keeps the busy status. MCP is unchanged. A line that doesn't parse goes to the shell as well, so the error is its own.
  - The version in `View` is `result.Version`, separately; the web app prints `version of <path>: <token>` (`ToolResult.cs:24`).
  - `api.MapPost("", RunAsync).WithMetadata(SessionOnlyMetadata.Instance).WithMetadata(new RequestSizeLimitAttribute(64 * 1024)).Errors(400, 401, 403, 413, 429, 503)` on a group `/api/console` with `AddEndpointFilter(ApiErrors.Filter)`, as `src/Kumbu.Server/Api/TagApi.cs:21-24`.
  - Add `["POST /api/console"] = Reading` to `DeliberatelyOpen` (`Reading` is at `PasswordWindowTests.cs:119`).
  - No logging of the command. Don't call `KumbuMetrics.ToolCalled`.
- [ ] **Step 4: green.** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ConsoleApiTests" --filter-class "*PasswordWindowTests" --filter-class "*ToolMetricsTests"`. Expected: exit 0, 0 failed. Use this command for the mutations too.
- [ ] **Step 5: commit**: "Add the console's endpoint, which refuses what writes".
- [ ] **Step 6: mutations.** (a) Drop `mkcp` from the check by adding it to `Allowed`: `Every_refused_command_stays_refused` and `Every_shell_command_is_classified` fail. (b) Check only the first stage: the pipeline rows fail. (c) Remove `SessionOnlyMetadata`: `An_api_key_is_refused` and `PasswordWindowTests` fail. (d) Move `rm` from `Refused` to `Allowed`: the `rm` rows fail. (e) `HasCommand` always true: `Lines_the_shell_answers_itself` fails. (f) Remove the size attribute: `The_body_is_limited` fails. (g) Remove the busy catch: `A_busy_vault_is_503` fails.

## Task 2: `cd`, and the history limit

**Files:**
- Modify: `src/Kumbu.Server/Api/ConsoleApi.cs`
- Modify: `src/Kumbu.Core/Attachments/StoreAttachments.cs` only if a helper fits there; else stay in `ConsoleApi.cs`
- Modify: `tests/Kumbu.Server.Tests/Metrics/LabelGuardTests.cs`
- Test: `tests/Kumbu.Server.Tests/Api/ConsoleApiTests.cs`

**Interfaces:**
- Consumes: `ConsoleApi`, `ConsoleView` from Task 1.
- Produces, HTTP: `POST /api/console` with `{ "call": "cd", "path": "/Work/projects" }`. Success: `isError` false and one text part holding the path as `VaultPath.Value`, which the web app takes as the new directory. Failure: `isError` true, text `cd: <path>: not a folder`.

- [ ] **Step 1: tests.**
  - `Cd_takes_folders` (`[Theory]`): `/`; `/Work`; `/Work/projects` holding a note; an empty folder made with `POST` through the REST API (see `tests/Kumbu.Server.Tests/Api/FolderMarkerApiTests.cs`); a folder holding only an attachment (`CreateFileAsync`).
  - `Cd_refuses_the_rest` (`[Theory]`): `/Work/plan.md`, an attachment's path, `/Work/nope`, `/Nope`, a relative `Work`, a kept folder's marker path. The text is `cd: <path>: not a folder`.
  - `Cd_without_a_path_is_400`: `{ "call": "cd" }`.
  - `Cd_on_a_busy_vault_is_503`: as `A_busy_vault_is_503`; its lookups go through the same wrapper.
  - `Cd_sees_a_member_s_name`: Bob renamed the shared vault for himself (`PUT /api/memberships/{id}/alias`); `cd` works on his name and not on Alice's.
  - `A_vault_of_another_user_is_not_a_folder`: Bob, no member: `/AlicesVault` gives the same text as for one that doesn't exist.
  - `History_takes_a_permit`: with `AccountLimits.HistoryScansPerUser` (60) calls of `history /Work` answered, the next is 429 with `Retry-After` and the sentence `AccountLimits.HistoryScansRefused`; `cat /Work/plan.md` still works; `cat /Work/plan.md | head` takes none. A pipeline with `history` in a later stage takes one too, although the shell refuses the line. A refused line takes none.
  - In `LabelGuardTests`, send the marker through the console as `rg zzsecretmarker /` and as a command name `zzsecretmarker`. `No_label_carries_user_input` must stay green.
- [ ] **Step 2: red.** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ConsoleApiTests" --filter-class "*LabelGuardTests"`. Expected: the new `Cd_*` and `History_takes_a_permit` tests fail, as `cd` answers 400 and `history` takes no permit.
- [ ] **Step 3: implement.** A folder is: the root; or a first segment in `ListVaultsAsync`; or, when `ReadAsync` of the path finds no note, `ListNotesUnderAsync` non-empty or `StoreAttachments.UnderAsync` non-empty (a kept folder's marker counts there, so an empty folder is found; `src/Kumbu.Core/Sh/Commands/Ls.cs:64-72` does the same). An attachment's own path must not count: check `StoreAttachments.GetAsync` first. Refuse `FolderMarker.Is(path)`. Permit: `limits.HistoryScan(caller.User!.Id)`, throwing `ApiErrors.TooMany(retryAfter, AccountLimits.HistoryScansRefused)`, as `src/Kumbu.Server/Api/HistoryApi.cs:285`.
- [ ] **Step 4: green.** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ConsoleApiTests" --filter-class "*LabelGuardTests"`. Expected: exit 0, 0 failed.
- [ ] **Step 5: commit**: "Let the console change directory, and count history against the limit".
- [ ] **Step 6: mutations.** (a) Don't check for an attachment: a row of `Cd_refuses_the_rest` fails. (b) Don't count the marker: the empty folder row fails. (c) Only count `history` in the first stage: `History_takes_a_permit` fails.
- [ ] **Step 7: phase gate.** Full verification; expect the baseline plus the new tests, 0 failed.

---

## Phase B: the web app

## Task 3: the tokeniser, in agreement with the server's

**Files:**
- Create: `web/src/console/tokenise.ts`, `web/src/console/tokenise.test.ts`
- Create: `web/src/console/console-fixture.json`
- Test: `tests/Kumbu.Server.Tests/Api/ConsoleFixtureTests.cs`

**Interfaces:**
- Produces: `tokenise(line: string): string[][] | null`, the stages as `Pipeline.TryParse` gives them, `null` for a line it refuses; `[]` for an empty one. `quote(word: string): string`. `joinLine(stages: string[][]): string`.
- Produces: the fixture `{ "allowed": string[], "refused": string[], "lines": ({ "line": string, "stages": string[][] } | { "line": string, "refused": true })[] }`.

- [ ] **Step 1: write the fixture and tests.** `allowed` and `refused`: the lists `ConsoleApi.Allowed` and `ConsoleApi.Refused` of Task 1: `ls`, `cat`, `outline`, `rg`, `semsearch`, `img`, `pdf`, `links`, `backlinks`, `tags`, `board`, `head`, `tail`, `wc`, `sort`, `history`, `diff`, `trash`, `lscp`, `uname`, `help`; and `restore`, `mv`, `mkdir`, `rm`, `mkcp`, `rmcp`. `lines` covers at least: plain words; `cat "a b.md"`; `'single "x"'`; `"double \"x\" \\ y"`; `"\n"` literally inside double quotes staying as backslash and `n`; `a\ b`; a trailing backslash; `rg 'a|b' /x`; `a | b | c`; a quoted `"|"`; a tab between words; leading and trailing spaces; umlauts; an empty quoted word `''`. Refused: `a > b`, `a && b`, `a ; b`, `` a `b` ``, `$(a)`, `${a}`, `a || b`, `| a`, `a |`, `"open`, `'open`, a line break in the middle, `a\<LF>b` (a backslash, a line break, then `b`; at the very end of a line the trim removes the break, so it wouldn't be refused).
  - `ConsoleFixtureTests.Lines_parse_as_the_fixture_says` finds the fixture by walking up from `AppContext.BaseDirectory` to the folder holding `Kumbu.slnx`, and compares each line with `Pipeline.TryParse`.
  - `ConsoleFixtureTests.Allowed_is_the_servers`: `allowed` equals `ConsoleApi.Allowed` as a set.
  - `ConsoleFixtureTests.Refused_is_the_servers`: `refused` equals `ConsoleApi.Refused` as a set.
  - `tokenise.test.ts`: each line gives the same. `quote` round trip: for each word in `["plain", "a b", "it's", "say \"x\"", "back\\slash", "", "a|b", "Bewässerung", "-n", "/x/y.md#Goals", "$HOME", "a;b"]`, `tokenise(quote(word))` is `[[word]]`, and for `a|b`, `joinLine([["rg", "a|b"], ["head"]])` tokenises back to the same stages.
  - `quote("Bewässerung")` and `quote("/x/y.md#Goals")` are returned unquoted.
- [ ] **Step 2: red.** `pnpm -C web test --run src/console` and `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ConsoleFixtureTests"`. The second fails only once the fixture's meaning is wrong, so it may pass at once; say so if it does: its red comes from mutation (b).
- [ ] **Step 3: implement `tokenise`** as a port of `src/Kumbu.Core/Sh/Tokenizer.cs` and `Pipeline.cs`, state for state. **`quote`:** unquoted if it matches `/^[\p{L}\p{N}_\-./:#,=+@%]+$/u`; else in single quotes if it holds no `'`; else in double quotes, with `\` and `"` escaped.
- [ ] **Step 4: green.** `pnpm -C web test --run src/console` and `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ConsoleFixtureTests"`. Expected: exit 0, 0 failed.
- [ ] **Step 5: commit**: "Add the console's tokeniser, checked against the shell's".
- [ ] **Step 6: mutations.** (a) Backslash in double quotes escapes everything: a line fails. (b) Change one expectation in the fixture: both sides fail; restore. (c) Add `rm` to `allowed`: `Allowed_is_the_servers` fails. (c2) Remove `rm` from `refused`: `Refused_is_the_servers` fails. (d) `quote` forgets to escape `"`: the round trip fails.

## Task 4: relative paths

**Files:**
- Create: `web/src/console/paths.ts`, `web/src/console/paths.test.ts`

**Interfaces:**
- Consumes: `tokenise`, `joinLine` from Task 3; the fixture's `allowed`.
- Produces: `resolvePath(path: string, cwd: string): string` (`.`, `..`, stays at `/`, keeps a `#section`, removes a trailing `/` except at the root); `resolveLine(line: string, cwd: string): string`, the line to send. `commandTable`, keyed by command name.

- [ ] **Step 1: tests** for spec §7, with cwd `/Gartenarbeit/Shopping` unless stated. Each row is `resolveLine(in)` → out:

| In | Out |
|---|---|
| `ls` | `ls /Gartenarbeit/Shopping` |
| `ls ..` | `ls /Gartenarbeit` |
| `ls` at `/` | `ls` |
| `rg foo` | `rg foo /Gartenarbeit/Shopping` |
| `rg foo` at `/` | `rg foo` |
| `rg foo /` | `rg foo /` (the sent line equals the typed one) |
| `rg -n foo a.md b.md` | `rg -n foo /Gartenarbeit/Shopping/a.md /Gartenarbeit/Shopping/b.md` |
| `rg --files` | `rg --files /Gartenarbeit/Shopping` |
| `rg --files sub` | `rg --files /Gartenarbeit/Shopping/sub` |
| `rg --tag x foo` | `rg --tag x foo /Gartenarbeit/Shopping` |
| `rg -e foo a.md` | `rg -e foo /Gartenarbeit/Shopping/a.md` |
| `rg -e foo` | `rg -e foo /Gartenarbeit/Shopping` |
| `rg -- --help` | `rg -- --help /Gartenarbeit/Shopping` |
| `semsearch how do plants grow` | `semsearch how do plants grow /Gartenarbeit/Shopping` |
| `semsearch Tomaten plan.md` | `semsearch Tomaten plan.md /Gartenarbeit/Shopping` |
| `semsearch Tomaten ./plan.md` | `semsearch Tomaten /Gartenarbeit/Shopping/plan.md` |
| `semsearch -n 3 Tomaten` | `semsearch -n 3 Tomaten /Gartenarbeit/Shopping` |
| `tags` | `tags /Gartenarbeit/Shopping` |
| `tags x` | `tags x /Gartenarbeit/Shopping` |
| `tags ./sub` | `tags /Gartenarbeit/Shopping/sub` |
| `tags x sub` | `tags x /Gartenarbeit/Shopping/sub` |
| `history` | `history /Gartenarbeit/Shopping` |
| `history --since 2h a.md` | `history --since 2h /Gartenarbeit/Shopping/a.md` |
| `trash`, `lscp` | with the working directory |
| `board` | `board /Gartenarbeit` |
| `board --column Doing` | `board --column Doing /Gartenarbeit` |
| `board` at `/` | `board` |
| `cat plan.md#Goals` | `cat /Gartenarbeit/Shopping/plan.md#Goals` |
| `cat "a b.md"` | `cat '/Gartenarbeit/Shopping/a b.md'` |
| `cat --version v1 a.md` | `cat --version v1 /Gartenarbeit/Shopping/a.md` |
| `cat --checkpoint=c a.md` | `cat --checkpoint=c /Gartenarbeit/Shopping/a.md` |
| `cat` | `cat` |
| `head -n 5 a.md` | `head -n 5 /Gartenarbeit/Shopping/a.md` |
| `head -5 a.md` | `head -5 /Gartenarbeit/Shopping/a.md` |
| `head` | `head` |
| `tail -n +3 a.md` | `tail -n +3 /Gartenarbeit/Shopping/a.md` |
| `cat a.md \| rg foo` | `cat /Gartenarbeit/Shopping/a.md \| rg foo` |
| `cat a.md \| head -n 2` | `cat /Gartenarbeit/Shopping/a.md \| head -n 2` |
| `pdf a.pdf 3-5` | `pdf /Gartenarbeit/Shopping/a.pdf 3-5` |
| `pdf info a.pdf` | `pdf info /Gartenarbeit/Shopping/a.pdf` |
| `img a.png --size small` | `img /Gartenarbeit/Shopping/a.png --size small` |
| `diff a.md` | `diff /Gartenarbeit/Shopping/a.md` |
| `diff a.md a1b2c3` | `diff /Gartenarbeit/Shopping/a.md a1b2c3` |
| `diff a.md b.md` | `diff /Gartenarbeit/Shopping/a.md /Gartenarbeit/Shopping/b.md` |
| `diff a.md ../b.md` | `diff /Gartenarbeit/Shopping/a.md /Gartenarbeit/b.md` |
| `uname -a`, `help rg` | unchanged |
| `rm a.md`, `mv a b`, `unknown a` | unchanged (no table entry: the server answers) |
| `cat a > b`, `cat "open` | unchanged (tokenise refused it) |
| `cat /abs/x.md` | unchanged, spelled as typed: `cat   "/abs/x.md"` stays exactly that |

  Also: `resolvePath` for `../..`, `../../..` at depth 2 giving `/`, `./a/../b`, and a name with an apostrophe. Every `allowed` name of the fixture has a `commandTable` entry (a test), and `commandTable` has no other.
- [ ] **Step 2: red.** `pnpm -C web test --run src/console/paths`. Expected: fails, as `paths.ts` doesn't exist.
- [ ] **Step 3: implement.** One entry per allowed command: its value-taking options and how operands map to paths, as the table above. Read each command's `Spec` in `src/Kumbu.Core/Sh/Commands/` (`Options.cs:30-40` says the grammar: `-n5`, `-n 5`, `--long=value`, combined flags, options after operands, `--` ends them; `digitsMean` for `head -5`), and `--version`, `--checkpoint`, which `Shell.TakeCheckpoint` takes for every reader. `Rg`'s `Spec` has `-e`/`--regexp` (`src/Kumbu.Core/Sh/Commands/Rg.cs:26`): with it, all operands are paths. If nothing changed, send the line as typed; else `joinLine`. Right of a pipe, only explicit paths are resolved and no default added. For `tags`, `semsearch` follow the spec's table.
- [ ] **Step 4: green.** `pnpm -C web test --run src/console/paths`. Expected: exit 0, 0 failed.
- [ ] **Step 5: commit**: "Let the console turn relative paths into absolute ones".
- [ ] **Step 6: mutations.** (a) Treat option values as operands: the `head -n 5` row fails. (b) Add defaults right of a pipe: the `rg foo` pipe row fails. (c) Treat the second `semsearch` word as a path: its rows fail. (d) Remove `sort` from the table: the agreement test fails.

## Task 5: the page

**Files:**
- Create: `web/src/console/api.ts`, `web/src/console/store.ts`, `web/src/console/run.ts`, `web/src/console/help.ts`, `web/src/console/ConsolePage.tsx`, `web/src/console/ConsolePage.test.tsx`
- Modify: `web/src/api/schema.d.ts` (by hand: path `/api/console` beside `/api/tags` at `:5232`, and the schemas of Task 1)
- Modify: `web/src/router.tsx:124` (`{ path: "/console", lazy: lazyPage(() => import("./console/ConsolePage")) }`)
- Modify: `web/src/shell/TreeDrawer.tsx:3`, `:265-268` (a link "Console" with `SquareTerminal` from `lucide-react`, between Settings and Admin)
- Test: `web/src/shell/MobileShell.test.tsx:391-397`

**Interfaces:**
- Consumes: `resolveLine`, `resolvePath`, `tokenise` from Tasks 3 and 4; `POST /api/console` from Tasks 1 and 2.
- Produces: `useConsole`, a zustand store: `cwd`, `entries`, `history`, `reset()`. In memory only. It subscribes to `useSession` (`web/src/state/session.ts`): whenever `user?.id` changes, unless from null to a user, it resets and raises a generation number, so an answer from before never lands. `signedIn` can replace one user by another directly (`web/src/router.tsx:61-63`), which is why it isn't "to null".

- [ ] **Step 1: tests in `ConsolePage.test.tsx`**, through `renderApp("/console")` with `fakeApi`.
  - **Opening:** shows `Kumbu² console, not a bash, and read-only. Type help for the commands, help <command> for details.`, the prompt `/`, focus in the input. Extend the account-links test at `web/src/shell/MobileShell.test.tsx:391-397` with the Console link.
  - **Running:** at `/Gartenarbeit/Shopping`, `rg -n Tomaten plan.md` posts `{ call: "sh", command: "rg -n Tomaten /Gartenarbeit/Shopping/plan.md" }`, shows `> rg -n Tomaten plan.md`, then `→ rg -n Tomaten /Gartenarbeit/Shopping/plan.md`, then the output. `→` also shows when identical, for `sh` calls only: `cd` shows none (spec §3).
  - **Prompt:** the vault segment and the rest of the path are separate elements, with `text-accent-text` and `text-muted`. In the scrollback the path line appears only when the directory changed.
  - **`cd`:** posts `{ call: "cd", path }` with the resolved path, takes the answer's text as the directory; on `isError` prints it and stays. `cd` alone goes to `/` with no request. `cd ..` at `/` stays. `cd a b` says `cd: too many arguments`. `cd x | head`, `pwd | head`, `clear | head` say `<name>: can't be part of a pipeline`, and nothing is posted. **The directory stopped existing:** a command's error shows, the prompt stays, `cd /` works without a request.
  - **Local:** `pwd` prints without a request. `clear` empties the scrollback, keeps the history. `help` shows the block of spec §6 and then the server's `help`, after `The manual agents get, unchanged:`. `help cd`, `help pwd`, `help clear` answer locally. `help rg` and `help | head` go to the server.
  - **History:** up and down walk; adjacent duplicates once; an empty line isn't recorded and runs nothing; at most 200.
  - **Version and parts:** a `version` prints `version of <path>: <token>` on its own line; an `image` part is an `<img>` with a `data:` URL and an `alt` naming the command.
  - **Errors:** `isError` output has a marker and `text-warning`. A 429 and a 503 print their sentence. Nothing throws on a 401, which the client handles.
  - **HTML is text:** output `<img src=x onerror=alert(1)>` is shown as those characters, and no `img` element appears.
  - **Long line:** the output element has `whitespace-pre-wrap` and `break-words`.
  - **One at a time:** two quick Enters post once; the input is disabled while running. Leaving the page aborts the request (`signal.aborted`). Coming back shows directory, scrollback and history.
  - **After sign-out:** the store's empty, and an answer arriving after `useSession.getState().signOut()` is dropped. Also after `end()`, and when `signedIn` replaces Alice with Bob directly: her scrollback goes.
  - **No storage:** after a session of commands, `localStorage` and `sessionStorage` hold no part of a command, a path or an output (compare the keys and values before and after).
  - **Help agrees with the server:** the block names exactly the commands of the fixture's `refused`, `restore, mv, mkdir, rm, mkcp and rmcp`.
- [ ] **Step 2: red.** `pnpm -C web test --run src/console/ConsolePage src/shell/MobileShell`. Expected: fails, as the page and the link don't exist.
- [ ] **Step 3: implement**, plain DOM, as `routes/Keys.tsx`'s page frame does for its back link and heading. The output is `role="log"`, monospaced, React text nodes only, no Markdown. `web/src/index.css:280-295` has the colour tokens: `accent-text`, `muted`, `dim`, `warning`; there is no error colour, so `warning`, plus the marker. The `→` line in `dim`. Scrollback holds the last 500 commands. It scrolls to the newest answer unless the user scrolled up. A click in the output focuses the input, unless text is selected. It has to work on a phone, with the input at least `min-h-11`, and needs no layout of its own there.
- [ ] **Step 4: green.** `pnpm -C web test --run src/console/ConsolePage src/shell/MobileShell`, then `pnpm -C web build`. Expected: exit 0, 0 failed, the build succeeds.
- [ ] **Step 5: commit**: "Add the console page".
- [ ] **Step 6: mutations.** (a) `dangerouslySetInnerHTML` for output: "HTML is text" fails. (b) `persist` the store to `sessionStorage`: "No storage" fails. (c) No generation check: "After sign-out" fails. (d) No reset on sign-out: it fails. (e) Input not disabled while running: "One at a time" fails. (f) Send the typed line rather than the resolved one: "Running" fails.
- [ ] **Step 7: phase gate.** Full verification.

---

## Phase C: documents and the end

## Task 6: documents, hand-test guide and security review

**Files:**
- Modify: `README.md`, in `### The web app` (`:42`)
- Modify: `docs/security-architecture.md`
- Create: `docs/test-playbook-console.md`

- [ ] **README:** what the console is, that it only reads and the server enforces that, that `cd` and relative paths exist only there, and what it can't test: the MCP layer, the write paths, a key's rights. No new environment variables.
- [ ] **`docs/security-architecture.md`:** the user row in §4 (`:220`: the console, read-only); §6, "No endpoint is left undecided" (`:382`: the endpoint is reading, and open); §7's rate limits row (`:610`: `history` through the console counts against the `HistoryScan` limit); the storage list at line 226 stays true, and may say that the console adds nothing. Cite code by file and line, as the document does. Keep the references true that your work moved, for example in `ToolResult.cs`.
- [ ] **`docs/test-playbook-console.md`**, in the style of `docs/test-playbook-collab.md`. Each step says what to type, where to look and what passes. It covers:
  - opening the console and the welcome line;
  - the agreed example: create a note `/Gartenarbeit/Shopping/plan.md` holding `Tomaten` in the web app, then `cd /Gartenarbeit/Shopping`, `rg -n Tomaten plan.md`, and compare the `→` line;
  - `cd` on a file, a missing path, an empty folder;
  - a path with a space and an umlaut;
  - `rm` and `cat x | rm y`, and that the note is still there;
  - `help`;
  - `img` and `pdf` with a real attachment, which the tests can't see;
  - a reload (everything gone), sign-out and another user in the same tab;
  - a member of a shared vault;
  - a phone.
- [ ] **Security review:** a fresh subagent gets the diff of `git diff master...HEAD` and this task's questions, not your conclusions: can any line reach a write through `POST /api/console`? Does anything reach a log or browser storage? Is any output rendered as HTML? Did `/mcp`, `CallerMiddleware` or anything agents see change? Every finding goes in the report, fixed or not; fixes go through red, green and a mutation.
- [ ] **Commit**: "Document the console, with its hand test".

## Task 7: the end

- [ ] Full verification. Expected: 0 failed, the skipped ones of Task 0 only, the new tests on top.
- [ ] Check `git diff master...HEAD --stat`: no version file; `CallerMiddleware.cs` untouched; under `src/Kumbu.Server/Mcp/` only `ToolResult.cs` changed; under `src/Kumbu.Core/Sh/` only `Shell.cs`, which gains `HasCommand`.
- [ ] **Do not merge.** Push, then open a **draft** pull request from `feature/read-only-console` to `master`, its body saying what it is, that a security review was done and where its findings are, and ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. If none can be opened, use the compare URL `master...feature/read-only-console`.
- [ ] Last commit: `docs/superpowers/overnighter/2026-11-02-read-only-console-report.md`, as the brief says, and a row in `docs/superpowers/open-items.md` with the pull request's link. Push.
