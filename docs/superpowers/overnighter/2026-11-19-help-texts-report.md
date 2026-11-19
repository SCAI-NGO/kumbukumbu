# Overnighter report: help texts that say what the code does

Run of 2026-11-19 (UTC 19:55 to about 23:45), branch `feature/help-texts`, brief `docs/superpowers/overnighter/2026-11-19-help-texts-brief.md`, spec `docs/superpowers/specs/2026-11-19-help-texts-design.md`, plan `docs/superpowers/plans/2026-11-19-help-texts.md`.

**Pull request:** https://github.com/SCAI-NGO/kumbukumbu/pull/264 (not merged).

## In short

Everything in the spec is done: §1, §2 (and the callers of `ForPath` beyond the spec's list), every item of §3 and of §4. Each task was red first, then green, with every mutation shown failing. A fresh reviewer checked each task, reading each text against the code. Seven findings came out of those reviews, all Minor, and all were fixed. A final whole-branch review found no Critical or Important issues; its four Minor findings were fixed in one wave. The final gate on the branch merged with the current `master` is below.

## Environment

- Claude Code cloud session (Anthropic's servers), 4 cores, 15 GB RAM, Ubuntu 24.04.
- .NET SDK 10.0.404 (roll forward from `global.json`'s 10.0.100), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- Node 26.13.1 in `/opt/node26`. **Installed:** pnpm 10.32.1 with `npm install -g pnpm@10.32.1`; the `pnpm` first on `PATH` was 10.28.0 from `/opt/node22`, so every command ran with `/opt/node26/bin` first.
- Docker 29.10.0 was already running, but **Testcontainers can't start the tests' MongoDB here.** The tests ask for `nofile` 64000 (`TestImages.cs`), above this host's hard limit of 20000 (`error setting rlimit type 7: operation not permitted`). The first run had 5,984 errors. As a fallback I ran `mongo:7.0` by hand: `docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 mongod --replSet rs0 --bind_ip_all --wiredTigerEngineConfigString "file_manager=(close_idle_time=5,close_scan_interval=5,close_handle_minimum=250)"`.
- The fixture's own `EnsureReplicaSetAsync` couldn't initiate that replica set: the member reported itself as `ReplicaSetGhost`, server selection timed out after 30 s, and the second run had 3,352 errors. I initiated `rs0` by hand with `mongosh` (member `127.0.0.1:27017`), and from then on every server run used `KUMBU_TEST_MONGO=mongodb://127.0.0.1:27017`. This is worth knowing for the next cloud run; the repository is unchanged.
- **The Factory board:** the `Kumbu_Factory` connector isn't in this session (ToolSearch for "Kumbu" finds nothing), so there is no card. This report and the pull request are the record.
- **Superpowers:** the skills were there. Plan written with `writing-plans`, executed with `subagent-driven-development`: a fresh implementer per task or pair of tasks, a fresh reviewer per review, and a final whole-branch review. Implementers and reviewers committed nothing beyond their task; only I pushed.

## Task 0: planned against measured

| | Planned (laptop, `63d3ca46`) | Measured (cloud, `21a1ae5` = `63d3ca46` + docs) |
|---|---|---|
| Server `dotnet test --solution Kumbu.slnx` | 7,780 tests, 7,696 passed, 83 skipped, 1 failed (name not captured), 23 min 46 s | 7,780 tests, **7,695 passed, 85 skipped, 0 failed**, 25 min 33 s (Core 34.8 s) |
| Web `pnpm -C web test --run` | 205 files, 4,111 tests, all passed | 205 files, 4,111 tests, all passed (242 s) |
| `pnpm -C web build` | passed, chunk-size warning | passed, chunk-size warning |

The laptop's one failure didn't reproduce in the cloud. The skips are the embedding model, the TensorX key and the tokenizer (`KUMBU_TOKENIZER`, `KUMBU_MODEL_DIR`). Task 0 took about 40 minutes rather than 15, because of the MongoDB fallback above.

## The plan review

A fresh subagent got the brief, the spec and the plan, and found 11 points. All of them went into the plan (`21a1ae5`):

1. Task 9 would have created a new `help.test.ts`, but `ConsolePage.test.tsx:306-316` already compares the refused list exactly, and the new file would have broken it. Task 9 extends that test instead.
2. §2's caller search missed `Problem`, `LargeProblem` and `TooLargeToCheck`, which also fall back to the name `Skills`. They were added to the search.
3. Task 2 ran only the Core suite, though the server tests write into `/Skills/…`. The server classes were added.
4. The brief points at `Blame.cs:58-63` for "a folder exists", but that check doesn't count attachments or folder markers. The plan uses `StoreAttachments.UnderAsync`, as `ArchiveCommand` and `NoteMover` do (decision 3 below).
5. Long `Usage` lines widen every row of the `help` list. Ruled: no `Usage` longer than graph's, with a test.
6. pdf isn't in `TestShell.Of()`, so its test moved out of the theory.
7. pdfimages' and xml's budgets are instance properties, not constants, so the mutations had to be shaped to match.
8. Several existing tests pin old wording; the plan names them in their tasks.
9. §1 left open what an unchanged SVG write with a message answers. Ruled: no line, as no version is written.
10. blame's `Usage` already shows `[#section]`.
11. html had the same "a call stops after 10 seconds" claim as xml. Fixed on the way.

## Tasks, commits and tests

| Task | Commits | Test evidence (full Core suite before each commit unless said) |
|---|---|---|
| 1 §1 | `99080f0`, `896b83f` | 7 new tests red, then Core 3,759 (0 failed, 70 skipped); `McpClientTests` 32/32; 6 mutations |
| 2 §2 | `09edff7`, `ea39479`, `28c0679`, `7b048ba`, `d41b324`, `c60beba`, `8f0afa3`, `b2653ca`, (`4098af0` mine) | 16 new tests, each red first; Core 3,765 (0 failed); Server 4,037 (0 failed, 15 skipped, 25 min 44 s); 13 mutations |
| 3 §3.9 | `634e615` | Core 3,773 (0 failed); 3 mutations |
| 4 §3.10 | `92a7cc5` | Core 3,777 (0 failed); 3 mutations |
| 5 §3.1–3.3 | `b378dcf`, review fix `6602345` | Core 3,781, then 3,791 (0 failed); 9 + 4 mutations |
| 6 Manual.cs | `0787477` (+ `6602345`) | Core 3,790 (0 failed); `UploadToolTests` 20/20; 13 mutations |
| 7 §3.4–3.6 | `6b75623`, review fix `c8b673f` | Core 3,795, then 3,797 (0 failed); mutations per claim, 2 in the fix |
| 8 §3.7, 3.8, 3.11 | `feb2dc1` | Core 3,797 (0 failed); mutations per claim |
| 9 §3.12 | `4af5b13` | `ConsolePage.test.tsx` 2 red, then 114/114; `src/console` 328 passed; build ok; 2 mutations |
| 10 §4 options | `9dc463d` | Core 3,823 (0 failed); 6 mutations |
| 11 §4 the rest | `75029df`, found on the way `fa75e5a`, review fix `a45090e` | Core 3,823, then 3,824 (0 failed); 11 + 3 + 7 mutations |
| final review wave | `6fd1361` | 3 new tests red; Core 3,826 (0 failed); `McpClientTests` 33/33; 3 mutations |
| merge of `master` | `0635929` | `open-items.md` conflict only: both sides' rows kept |

The real outputs (RED and GREEN lines, every mutation with its failing line) are in the appendix, one section per task, exactly as each implementer reported them. Text-only changes without a test (the line wrapping in `ArchiveCommand.cs`, `Help.cs`, `Html.cs` and `Rm.cs` before its width test, and the README) had "none: wording only" as their mutation step.

### Phase gates

Each gate ran in a separate worktree (`git worktree`, detached) while the next implementer worked in the main one:

- **After §1, §2, §3.9 and §3.10, at `92a7cc5`:** `dotnet restore --locked-mode` ok; Release build 0 errors. Server 7,814 tests, 7,728 passed, 85 skipped, **1 failed**: `MembershipApiTests.Last_use_is_the_use_on_this_vault_not_the_keys_global_one` (`Expected: 1, Actual: 0`). It is known flaky and passed all 3 runs alone. Web 205 files, 4,111 passed; build ok.
- **After §3, at `c8b673f`:** restore ok; Release build 0 warnings, 0 errors. Server 7,835 tests, 7,749 passed, 85 skipped, **1 failed**: the same test, which again passed all 3 runs alone. It counts profiler entries of a write (`MembershipApiTests.cs:623-638`) and fails under load, as `open-items.md` (2026-11-07) records. Web 205 files, 4,111 passed; build ok.
- **Final, on `0635929` (branch merged with `master` at `a0c94cb`):** `dotnet restore --locked-mode` ok; `dotnet build --configuration Release --no-restore` 0 warnings, 0 errors; `dotnet test --solution Kumbu.slnx`: 7,865 tests, **7,780 passed, 85 skipped, 0 failed**, 23 min 39 s (Core 34 s); `pnpm -C web install --frozen-lockfile && pnpm -C web test --run`: 205 files, 4,111 tests passed; `pnpm -C web build` ok (chunk-size warning). CI on the pull request: see below.

## Mutations

Every mutation, each on its own, with the test it failed and the failing line, is in the appendix under each task's "Mutations" heading. In total: 6 (Task 1), 13 (Task 2), 3 (Task 3), 3 (Task 4), 13 (Tasks 5 and 6, plus 4 in their fix), 13 (Task 6), mutations per claim (Tasks 7 and 8, plus 2 in the fix), 2 (Task 9), 6 (Task 10), 11 + 3 + 7 (Task 11, the found-on-the-way commit and its fix), and 3 (the final wave). Each one failed its test. None survived, though one exposed something: in Task 1, the error guard first survived, because every `AttachmentWriter` error returned early. The restructure in `896b83f` made the guard reachable, and the mutation then failed.

## §2: each caller of `ForPath` and its verdict

`rg -n 'ForPath\(' src` and `rg -n 'AttachmentFormat\.(Problem|LargeProblem|TooLargeToCheck|RenameProblem|IsPdf|KindOf)\(' src` at `896b83f`:

| Caller | Verdict |
|---|---|
| `NoteWriter.cs:129` (`WriteAsync`), `:612-616` (`NotText`) and its callers `:147,198,333`, `Replacer.cs:32` | changed (`09edff7`): the mark from `store.SkillsVaultAsync` |
| `FileApi.cs:194` (`Problem`), `:213` (`TooLargeToCheck`), `:214` (`LargeProblem`) | changed (`ea39479`) |
| `FileApi.cs:215` (large image's dimensions) | stays: a large `.svg` is refused just before by `TooLargeToCheck` as SVG and as a skill's text file alike, so `ForPath` meets no `.svg` there; for other names the skill rule can't make an image |
| `FileApi.cs:282` (`Dimensions`) | stays: an `.svg` is Svg (excluded by `!= Svg`) or Text (not an image), null either way; the writer reads an SVG's size itself |
| `FileApi.cs:141` (`OnlyAsDownload`) | stays: already passes the mark |
| `DavWrites.cs:623,624,634` | changed (`28c0679`), the mark through a new one-line `DavTree.SkillsVaultAsync` (the tree already holds the share's store and asks it at `DavTree.cs:82`) |
| `DavWrites.cs:760` (`Kept`) | stays: its format only refuses a marker and decides image dimensions; an `.svg` gets none as Svg (`Dimensions` excludes it, a large one is refused first) or as Text, and the writer decides the format by the mark |
| `SyncApi.cs:186` (`IsText`), and `:189` (`KindOf`) | changed (`7b048ba`): `access.IsSkillsVault ? unlocked.Name : null`; `KindOf` had to change with it (see Concerns) |
| `SkillUpdateTaker.cs:264` (`IsText`) | changed (`d41b324`): the copy's `Access.IsSkillsVault` |
| `AgentUploadApi.cs:60` | changed (`c60beba`): can differ for `.svg` (Svg, an image, vs Text); only the unmarked-`Skills` case reaches it, as the upload tool refuses a skill's text file before issuing a URL |
| `Publications.cs:909`, `Bundles.cs:185` (`MayCopy`) | changed (`8f0afa3`): can differ for `.svg`; a marked vault's whole content is excluded by `InSkillPlace`, so only the unmarked `Skills` was wrong |
| `SkillCopier.cs:175` (`Dimensions`) | stays: an `.svg` is Svg or Text, and the writer ignores given dimensions for both (it reads an SVG's size itself, `AttachmentWriter.cs:72`) |
| `MarketplaceIndex.cs:275,276` (`IsStorable`) | stays: the path is built in a vault named `SkillPaths.SkillsVault` (`:265`), so the default name is the right mark by construction; it asks whether a file fits any skill's folder, not a caller's vault |
| `AttachmentFormat.cs:100` (`IsPdf(VaultPath)`) and its callers `SectionSearch.cs:213,250`, `Semsearch.cs:105`, `EncryptedNoteStore.cs:1261,1348` | stays: `.pdf` is no text name, so the skill rule can't change the answer |
| `AttachmentFormat.cs:153` (`KindOf`) and its callers `NoteApi.cs:586,652,913`, `HistoryApi.cs:240,265,464`, `RecentApi.cs:76`, `Activity.cs:183` | left, open-items row (`b2653ca`): `KindOf` got an optional `skillsVault` (default unchanged) for `SyncApi`; the others don't all hold the mark (`NoteApi`'s file view is a static mapper, `Activity` builds rows in Core) |
| `AttachmentFormat.cs:130,131,146,167,192,197` (inside `RenameProblem`, `Problem`, `LargeProblem`, `TooLargeToCheck`) | stays: they pass their own `skillsVault` on |
| `AttachmentWriter.cs:63,70,102,103,125`, `UploadTool.cs:67,71`, `NoteMover.cs:189`, `TreeWriter.cs:112`, `NoteApi.cs:797` | stays: already pass the mark |

`IsPdf(AttachmentState)` callers (`EncryptedNoteStore.cs:963`, `PdfAnalysisRunner.cs:81`, `Csv.cs:174`, `SearchIndex.cs:205,295`, `AttachmentText.cs:27`) go by the stored media type, not `ForPath`: not in scope.

The marks passed are the caller's own. `SkillsVaultAsync` of the request's store names only a vault the caller owns, of kind Skills, and not as a member. `VaultAccess.IsSkillsVault` asks `IsOwner && Membership is null`. No vault gets skill-folder treatment because of its name alone, and write and upload decide by the same mark. The tests cover both directions. `Write_keeps_refusing_what_upload_refuses_in_a_marked_vault` checks that `/Tools/run.py` (in the location, not in a skill's folder) and `/Tools/x/tool.bin` stay refused, with nothing stored. `Write_refuses_a_text_name_in_an_unmarked_vault_named_Skills_as_anywhere` checks the other direction. `src/Kumbu.Server/Auth`, `OAuth`, `Sharing` and `Vaults/EncryptedNoteStore.cs` are untouched (`git diff --stat 6f50780..HEAD` on them is empty).

## Found on the way

Fixed, being texts that said less than the code, or something else:

- **Archive topic:** rg also leaves archived text files out (`Rg.cs:269,817-818`), and graph leaves archived attachments out too (`GraphQueries.cs:200`). rg's and graph's own `--archive` lines now say the same.
- **html:** "A call stops after 10 seconds" was the same claim as xml's. It now says what stops (`Html.cs:80-98`).
- **skills:** the error for an unknown subcommand said "reads and finds"; it now names copy. A stale duplicate `<summary>` on `CalendarCommand.WriteVerb` is gone.
- **pdfimages:** a storing call lists its pages first, within the same 10 seconds as `--list` (`PdfImages.cs:221,300`). Its "15 MB" now comes from `AttachmentFormat.MaxBytes`; the sidecar's JPEG fallback uses the same limit (`pdf/src/extract.ts:23-24`).
- **rm, mv, restore:** their helps said an attachment is "an image or a PDF"; they take any file.
- **trash:** the Summary names folders.
- **history and semsearch:** history's "last 50" and semsearch's "default 10" come from their constants.
- **rg:** "8 PDFs", "15 MB" and "64 MB" come from constants; rg's own `-m` names text files.
- **calendar:** "366 days" comes from `MaxRangeDays`.
- **The write tool's parameters** (`WriteTools.cs:19-20`) spoke of notes only; they now name an SVG and a skill's text file too.
- **README.md:** `:26` mv, `:27` rm, `:36` pdfimages' time limits and `.jpg`, `:43` write's message, and `:426` graph and calendar hiding the archive.
- **Manual.cs:13:** the `rg` line said "search notes and PDF text"; it now names text files. It said less than the code, which the brief's open point 6 allows to change.

Recorded, not fixed, because they would change behaviour or lie outside the scope (rows in `docs/superpowers/open-items.md`):

- `AttachmentFormat.KindOf`'s other callers still decide "text" by the name Skills (row of `b2653ca`; `HistoryApi` is named as the easy first follow-up, `4098af0`).
- `blame` on a folder holding only an image (or only a folder marker) still answers "No such file or directory", because its folder check doesn't count attachments (`Blame.cs:65-66`). Changing that is a behaviour change outside §3.10; it has a row in `open-items.md`.

## Decisions made without Chris

1. **`KindOf` takes the mark optionally, and only `SyncApi` passes it.** Options: leave `KindOf` as the brief says, or give it an optional argument. With only sync's `IsText` fixed, an old client got a file whose kind read "text": the filter and the kind disagreed (a test showed it). The default stays the same for every other caller, nothing breaks, and the rest is a row in `open-items.md`.
2. **No `Usage` line longer than graph's (118 characters).** The `help` list pads every row to the longest `Usage` (`Help.cs:151-152`), so the spec's literal publish and calendar forms (162 and 156 characters) would have pushed every summary past column 160. The full shapes are in each help's synopsis instead. publish's `Usage` is `publish ls [path] | add|update <note> [--depth N] [--without <note>]... | rm <note>`, and calendar's ends in `set|move|… <note>`, with the summary beside it naming all five verbs. `HelpTests.No_usage_is_longer_than_graphs` holds the rule; it doesn't see img, pdf and pdfimages (not in `TestShell.Of()`), which are 118 characters or shorter.
3. **"A folder exists" for `links`:** the folder is a vault in `ListVaultsAsync`, or `StoreAttachments.UnderAsync` finds attachments or a marker below it, as `ArchiveCommand` and `NoteMover` decide. The brief's pointer to `Blame.cs:58-63` would have missed attachments and markers.
4. **§1's line only when a version is written:** an unchanged write keeps no version, so it keeps no message for anything, as `WriteDescription` already says.
5. **pdfimages follows the code, not the spec's "a page started finishes within 50"** (brief, open point 7): "storing stops at the first page boundary after 20 seconds; after 50 it stops even within a page, and names that page as partly stored".
6. **skills' Summary is "find, read, check and copy skills"**, without "the user's": copy copies a built-in skill.
7. **Calendar's checkpoint sentence sits in `Help.Of`**, right after the `--checkpoint` line it appends for every reader. The command's own help ends before that line, so it can't print the sentence where §3.11 wants it.
8. **The tool description's list** stays as it is for §4 (brief, open point 6), including `tail [-n N|+N]` and `pdfimages <pdf> [pages] <dir>/`. Neither line is false. The `rg` line changed because it said less than the code.
9. **Numbers without a Core constant stay typed out:** "1 GiB" (its constant is in Kumbu.Server), publish's depth "0 to 5" (`Bundles.MaxDepth`, in Kumbu.Server), and pdfimages' "8 megapixels", "50 megapixels" and "1 %". `Manual.cs`'s numbers stay typed out as the brief says, each held by a test against its constant: 30 days, 8000 tokens, 15 MB and one hour in `ManualTests`, and 10 minutes and 1 GiB in the server's `UploadToolTests`.
10. **A literal brace in a `$"""` help** is written `{"{a,b}"}`, because doubling a brace doesn't compile in a raw interpolated string (CS9006). A test checks that rg's help prints `{a,b}`. My own context file had said to double them, which was wrong.
11. **Running the tests:** MongoDB by hand with `KUMBU_TEST_MONGO` (see Environment), and the phase gates in a second worktree beside the next task, to save about an hour of waiting.
12. **The review of small fix rounds:** for the fix rounds of Tasks 5+6, 7+8 and 10+11, and for Task 9 (each a few lines of text), I read the diff against the finding and the code myself instead of sending a fresh reviewer. Each task's first review was by a fresh reviewer.

## Deviations, surprises and open points for Chris

- **Spec §3.5 is wrong about pdfimages' 50 seconds.** The code stops storing even within a page after `Ceiling`, as the brief's open point 7 says; the help follows the code.
- **Another spec still says the old xml/html limit.** `docs/superpowers/specs/2026-11-19-reading-csv-html-xml-design.md:35` says "A call stops after 10 seconds", while the xml and html helps now say exactly what stops: the XPath or selector and its printing, not reading the file. That spec stays as written (brief). `docs/superpowers/specs/2026-11-18-blame-design.md` likewise still quotes blame's old help.
- **The brief's pointer for §3.9** (`Blame.cs:58-63`) doesn't count attachments or markers; see decision 3.
- **The MCP suite and Playwright from `master`'s #261** came in with the merge; the final gate runs the server and web suites as the brief defines them, not the Playwright suites.
- **Docker in the cloud:** see Environment. A later cloud run could set `KUMBU_TEST_MONGO` from the start, or the tests could ask for a `nofile` within the host's hard limit. That decision is Chris'; nothing changed in the repository.
- **The known flaky test** `MembershipApiTests.Last_use_is_the_use_on_this_vault_not_the_keys_global_one` failed in both phase gates under load, and passed six runs alone.

## Proposed changelog entry

Under **Better**:

- **Help that says what the commands do.** `help`, every command's help, the tool descriptions and the web console's help now match what the server does. Every option a command takes is listed, each limit is taken from the setting that enforces it, and nothing is promised that isn't there. Examples: graph and calendar hide the archive too, `rg --files` lists text files as well as notes, pdfimages states its real time limits, and the console names calendar's write verbs among what it refuses. Agents find features they missed before and no longer plan around behaviour that isn't there.

Under **Fixed**:

- **A skills vault of any name.** `write` and `edit` now recognise a skill's text file by the vault marked as your skills vault, not by the name "Skills". In a skills vault with another name, a script in a skill could be written neither by `write` nor by `upload`; now `write` creates it. A vault that is merely called Skills keeps files like any other. The files API, WebDAV, sync, skill updates, an agent's upload and publishing decide the same way.
- **write says when a message isn't kept.** A message on a write of an SVG or a skill's text file used to be dropped without a word. The write still succeeds, and its answer now says `message not kept: an SVG's or a skill file's history keeps no messages`.
- **links on an empty folder.** `links` on a vault or folder that holds no notes, only images or nothing at all, answers that no links are broken, instead of "No such file or directory".
- **blame --lines on a folder** is now refused ("blame: --lines takes a note, not a folder") instead of being ignored.

## Hand-check list (web console or an agent, on staging)

| Line | Should print |
|---|---|
| `help archive` | the list includes `graph      leaves its notes and attachments out; --archive takes them in` and `calendar   leaves archived notes out`, and the sentence "calendar has no --archive; a note in the archive has no place in the calendar" |
| `help` | one row per command, the summaries in one column, no row wider than graph's; `tail [-n N|-n +N] [--version V] [path]` among them |
| `rg --files /<vault with a CSV>` | the CSV's path among the notes' paths |
| `links /<an empty vault or a folder holding only an image>` | `(no broken or ambiguous links below /…)` |
| `blame --lines /<vault>` | `blame: --lines takes a note, not a folder` |
| `help calendar` | right after the `--checkpoint <name>` line: "the reading forms only; set, move, close, reopen and unset are refused there" |
| the console's `help` | "…publish add, update and rm, skills copy, and calendar set, move, close, reopen and unset are refused…" |
| write tool: `/<vault>/diagram.svg`, `<svg xmlns="http://www.w3.org/2000/svg"/>`, message `first sketch` | `created /<vault>/diagram.svg` and on the next line `message not kept: an SVG's or a skill file's history keeps no messages`; `history /<vault>/diagram.svg` lists the version without a message |
| `help pdfimages` (live shell, with the sidecar) | "--list stops after about 10 seconds; a call that stores lists its pages first, also for about 10 seconds, …; storing stops at the first page boundary after 20 seconds; after 50 it stops even within a page" |

## Appendix: the task reports

Each implementer's report as written, unchanged, with the RED and GREEN outputs and each mutation.

### Task 1

### Task 1 report: §1, `write` says when it can't keep a message

Status: DONE_WITH_CONCERNS (one small concern, see the end)

#### What I implemented

- `src/Kumbu.Core/Notes/NoteWriter.cs`
  - `private const string MessageNotKept = "\nmessage not kept: an SVG's or a skill file's history keeps no messages"`, next to `MaxMessage` (one place only).
  - `WriteAsync`, SVG / text-file branch: deconstructs `AttachmentWriter.WriteAsync`'s `WriteResult` into `(written, stored)`. A failed write returns from its own `if (written.IsError)` block (the "upload:" to "write:" rename as before). After it, the line is appended when `message is not null` (after `TryMessage`, so a blank message is null) and `stored?.Version != version`.
  - How the "is unchanged" case is told apart: by a field, not by text. `AttachmentWriter.CommitAsync` (`AttachmentWriter.cs:158`) answers "is unchanged" with the existing attachment, whose `Version` equals the version passed in. A create (version null) or a replace returns the new attachment, whose version is different.
- `src/Kumbu.Server/Mcp/WriteTools.cs:22`: the `message` parameter of `write` now reads "Optional: one line on why, kept with this version in the note's history; an SVG's or a skill file's history keeps none (at most 200 characters)". `edit`'s parameter is unchanged, since edit refuses SVGs and has no attachment branch.
- `src/Kumbu.Core/Sh/Manual.cs` (`WriteDescription`): "message: optionally one line on why you wrote, kept with this version in the note's history; an SVG's or a skill file's history keeps none. Shown by history. A write that changes nothing makes no version and so keeps no message." "shown by history" and the no-change sentence are kept.
- I didn't touch `AttachmentWriter`'s answer, `ShellResult.Notice` or `Mcp/Notices.cs`.

Tests added:
- `tests/Kumbu.Core.Tests/Attachments/SvgWriteTests.cs`: `A_message_on_an_SVG_write_is_not_kept_and_the_answer_says_so`, `A_failed_SVG_write_with_a_message_answers_the_error_alone`, `An_SVG_write_without_a_message_answers_as_before` (no message, and `"  "`), `An_unchanged_SVG_write_with_a_message_answers_as_before`.
- `tests/Kumbu.Core.Tests/Attachments/TextAttachmentTests.cs`: `A_message_on_a_skill_text_file_write_is_not_kept_and_the_answer_says_so`.
- `tests/Kumbu.Core.Tests/Sh/HelpTests.cs`: `Write_description_says_an_SVG_and_a_skill_file_keep_no_message`.
- `tests/Kumbu.Server.Tests/Mcp/McpClientTests.cs`: `Writes_message_parameter_says_an_SVG_and_a_skill_file_keep_none`. No server test read parameter descriptions by reflection, so I put it in the class that already checks the tools' descriptions. It uses reflection on `WriteTools.Write`'s `message` parameter `DescriptionAttribute` and needs no app.

Environment for every command: `export DOTNET_CLI_TELEMETRY_OPTOUT=1 KUMBU_TEST_MONGO=mongodb://127.0.0.1:27017`.

#### RED

```
dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*SvgWriteTests"
failed Kumbu.Core.Tests.Attachments.SvgWriteTests.A_message_on_an_SVG_write_is_not_kept_and_the_answer_says_so
  Assert.Equal() Failure: Strings differ
  Expected: "created /Work/diagram.svg\nmessage not kept: an SVG"···
  Actual:   "created /Work/diagram.svg"
  total: 12, failed: 1, succeeded: 11

dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*TextAttachmentTests"
failed Kumbu.Core.Tests.Attachments.TextAttachmentTests.A_message_on_a_skill_text_file_write_is_not_kept_and_the_answer_says_so
  Expected: "wrote /Skills/pdf/run.py\nmessage not kept: an SVG'"···
  Actual:   "wrote /Skills/pdf/run.py"
  total: 13, failed: 1, succeeded: 12

dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"
failed Kumbu.Core.Tests.Sh.HelpTests.Write_description_says_an_SVG_and_a_skill_file_keep_no_message
  Assert.Contains() Failure: Sub-string not found
  total: 26, failed: 1, succeeded: 25

dotnet test --project tests/Kumbu.Server.Tests -- --filter-method "*Writes_message_parameter_says_an_SVG_and_a_skill_file_keep_none"
failed Kumbu.Server.Tests.Mcp.McpClientTests.Writes_message_parameter_says_an_SVG_and_a_skill_file_keep_none
  Assert.Contains() Failure: Sub-string not found
  total: 1, failed: 1, succeeded: 0
```

Why these failures were expected: the message was dropped without a word, and both texts still promised it was kept. The three guard tests (failed write, no or blank message, unchanged write) passed against the old code as well. That is right, because they pin today's behaviour that the fix must keep. The mutations below show that they bite.

#### GREEN

```
dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*SvgWriteTests"        total: 12, failed: 0, succeeded: 12
dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*TextAttachmentTests"  total: 13, failed: 0, succeeded: 13
dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"            total: 26, failed: 0, succeeded: 26
dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*McpClientTests"     total: 32, failed: 0, succeeded: 32
dotnet test --project tests/Kumbu.Core.Tests (full)                                    total: 3759, failed: 0, succeeded: 3689, skipped: 70
```

#### Mutations (each restored with `git checkout -- <file>`)

- (a) I dropped the appended line (`? written with { Output = written.Output + MessageNotKept }` became `? written`). `SvgWriteTests.A_message_on_an_SVG_write_is_not_kept_and_the_answer_says_so` failed with `Expected "created /Work/diagram.svg\nmessage not kept…" Actual "created /Work/diagram.svg"`. `TextAttachmentTests.A_message_on_a_skill_text_file_write_is_not_kept_and_the_answer_says_so` failed the same way with `wrote /Skills/pdf/run.py`.
- (b) I appended the line on error too.
  - First attempt, against commit 1: I removed `!written.IsError &&`. **No test failed.** Every error from `AttachmentWriter` starts with "upload:" (`ToolOf`, `AttachmentWriter.cs:468`), so the rename branch returned first and the guard could not be reached.
  - I restructured the code so that the error case returns from its own block (commit 2), then mutated it to append `MessageNotKept` to the renamed error. `SvgWriteTests.A_failed_SVG_write_with_a_message_answers_the_error_alone` failed with `Assert.DoesNotContain() Failure: Sub-string found`.
- (c) I restored the old `Manual.cs` (`git show HEAD~1:…`). `HelpTests.Write_description_says_an_SVG_and_a_skill_file_keep_no_message` failed with `Assert.Contains() Failure: Sub-string not found`.
- (d) I restored the old `WriteTools.cs`. `McpClientTests.Writes_message_parameter_says_an_SVG_and_a_skill_file_keep_none` failed with `Assert.Contains() Failure: Sub-string not found`.
- (e) I dropped the unchanged guard `&& stored?.Version != version`. `SvgWriteTests.An_unchanged_SVG_write_with_a_message_answers_as_before` failed with `Expected ···"/diagram.svg is unchanged" Actual ···"/diagram.svg is unchanged\nmessage not kept: an SVG"···`.
- (f) I dropped the message check `message is not null &&`. `SvgWriteTests.An_SVG_write_without_a_message_answers_as_before` failed, and so did two existing SvgWriteTests (`Write_creates_an_svg_…`, `Cat_gives_the_version_…`), all with `Strings differ`.

#### Commits

- `99080f0` Say in write's answer that an SVG or a skill file keeps no message
- `896b83f` Keep write's failed SVG answer apart from the message line (restructure after mutation b; no change in behaviour)

#### Files changed

- /home/user/kumbukumbu/src/Kumbu.Core/Notes/NoteWriter.cs
- /home/user/kumbukumbu/src/Kumbu.Core/Sh/Manual.cs
- /home/user/kumbukumbu/src/Kumbu.Server/Mcp/WriteTools.cs
- /home/user/kumbukumbu/tests/Kumbu.Core.Tests/Attachments/SvgWriteTests.cs
- /home/user/kumbukumbu/tests/Kumbu.Core.Tests/Attachments/TextAttachmentTests.cs
- /home/user/kumbukumbu/tests/Kumbu.Core.Tests/Sh/HelpTests.cs
- /home/user/kumbukumbu/tests/Kumbu.Server.Tests/Mcp/McpClientTests.cs

#### Concerns

- `README.md:43` still says the message "is kept with the version and shown by `history`". That is true for notes but doesn't mention SVGs or skill files. This task didn't name README, so I left it. A later task, or the final review, may want to add "(not for an SVG or a skill's text file)".
- The work is in two commits instead of one, because of the restructure after mutation (b). They squash on merge.

### Task 2

### Task 2 report: §2, `write` and `edit` ask for the marked skills vault

Base `896b83f`. Environment: `DOTNET_CLI_TELEMETRY_OPTOUT=1 PATH=/opt/node26/bin:$PATH KUMBU_TEST_MONGO=mongodb://127.0.0.1:27017`.

#### Commits

| SHA | Subject |
|---|---|
| `09edff7` | Let write and edit find a skill's text file in a skills vault of any name |
| `ea39479` | Decide a skill's text file by the mark in the files API |
| `28c0679` | Decide a skill's text file by the mark in WebDAV |
| `7b048ba` | Decide a skill's text file by the mark in sync |
| `d41b324` | Decide a skill's text file by the mark in a skill update's review |
| `c60beba` | Decide a skill's text file by the mark in an agent's upload answer |
| `8f0afa3` | Decide a skill's text file by the mark in a published copy's images |
| `b2653ca` | Note that KindOf still names a skill's text file by the name Skills |

#### RED

##### Core: `TextAttachmentTests` (new tests, `Make` now adds the vault `Tools` too)

Command: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*TextAttachmentTests"` → total 19, failed 4, succeeded 15.

- `Write_creates_a_text_file_in_a_skill_of_a_marked_vault_of_another_name`: `write: /Tools/x/scripts/run.py: note names end in .md; write keeps notes, SVGs and a skill's text files, and other files are uploaded (the upload tool)`. Expected: `ForPath` without the mark sees a File outside `/Skills`, `NotText` refuses it as a note name.
- `Edit_refuses_a_text_file_in_a_skill_of_a_marked_vault_of_another_name`: expected `"edit: run.py is a text file in a skill; replace it"···`, actual `"edit: /Tools/x/run.py: note names end in .md; edit"···`. Expected: `NotText` without the mark.
- `Write_refuses_a_text_name_in_an_unmarked_vault_named_Skills_as_anywhere`: expected `"write: /Skills/x/run.py: note names end in .md; wr"···`, actual `"created /Skills/x/run.py"`. Expected: the default name `Skills` sent it to the attachment writer as text.
- `Replace_refuses_a_text_file_in_a_skill_of_a_marked_vault_of_another_name` (added beyond the brief, for `Replacer`'s `NotText` call): expected `"replace: run.py is a text file in a skill; replace"···`, actual `"replace: /Tools/x/run.py: note names end in .md; r"···`.
- `Write_keeps_refusing_what_upload_refuses_in_a_marked_vault` (`/Tools/run.py`, `/Tools/x/tool.bin`): a guard, green before and after (asserts the exact refusal, and that neither a note nor an attachment is stored).

##### Server (each with `dotnet test --project tests/Kumbu.Server.Tests -- --filter-method "*<name>"`, 1 test, 1 failed)

All use the new test helper `RestClient.MarkedUnderAnotherNameAsync` (`tests/Kumbu.Server.Tests/Api/RestClient.cs`): a user whose marked skills vault is named `Tools`, beside an unmarked vault named `Skills`, created through the catalog before the first sign-in, so the sign-in neither makes a vault Skills nor renames the unmarked one.

- `FileApiTests.An_upload_finds_a_skills_folder_by_the_mark_not_the_name`: `Expected: Created, Actual: BadRequest` for `PUT /api/files/Skills/x/tool.bin`. Expected: the pre-check refused a binary below a folder of the unmarked `Skills`, which the writer would keep.
- `LargeFileTests.A_large_file_finds_a_skills_folder_by_the_mark_not_the_name`: `413 {"error":"big.bin is larger than 15 MB; a skill's folder keeps files up to 15 MB, …"}` for 16 MiB to `/Skills/x/big.bin`. Expected: `TooLargeToCheck` by name. (Checked separately: with the old code `/Tools/x/big.bin` was a 400 after streaming the whole body, the writer's refusal, instead of a 413 before.)
- `DavSkillTextTests.Put_finds_a_skills_folder_by_the_mark_not_the_name`: `Expected: UnsupportedMediaType, Actual: BadRequest` for `PUT /dav/Tools/x/notes`. Expected: `Problem` by name said the bytes matched, so the writer's refusal became a 400.
- `DavLargeFileTests.A_large_put_finds_a_skills_folder_by_the_mark_not_the_name`: the 413 message as above for `/dav/Skills/x/big.bin`. (Checked separately: old code gave `/dav/Tools/x/big.bin` a 400 after streaming, not a 413.)
- `SyncApiTests.Text_files_are_told_apart_by_the_mark_not_the_name`: `Assert.Empty() Failure: Collection: ["/Tools/x/run.py"]`. Expected: `IsText` by name let a skill's text file go to a client that didn't ask for text. After fixing `IsText` alone, the same test failed on the kind: `Expected: "file", Actual: "text"` for `/Skills/x/run.py`, so `KindOf` there takes the mark too (see Concerns).
- `SkillUpdateTakeTests.Review_diffs_text_files_in_a_skills_vault_marked_under_another_name`: `Assert.Contains() Failure: Collection: [] Not found: "+print('new')"`. Expected: `IsText` by name gave a script in `/Tools/pdf` no hunks.
- `AgentUploadApiTests.An_svg_in_a_vault_named_Skills_that_isnt_marked_is_embedded_as_an_image`: `Expected: "![diagram.svg](/x/diagram.svg)", Actual: "[diagram.svg](/x/diagram.svg)"`. Expected: `ForPath` by name took the SVG for a skill's text file.
- `PublishedFilesTests.An_svg_in_a_vault_named_Skills_that_isnt_marked_goes_with_its_note` and `…_in_a_publication_of_several`: both `Assert.Single() Failure: The collection was empty`. Expected: `MayCopy` by name took the SVG for a skill's text file and left it out.

#### GREEN

- `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*TextAttachmentTests"`: total 19, failed 0, succeeded 19.
- `dotnet test --project tests/Kumbu.Core.Tests` (after commit 1): total 3765, failed 0, succeeded 3695, skipped 70.
- Classes run after commit 1 (each passed, 0 failed): `SkillCopyTests` 37, `SkillPublicationTests` 11, `FileDownloadTests` 5, `McpClientTests` 32, `UploadToolTests` 19, `DeepLinkTests` 8 (the `Mcp` classes that write: `rg -l 'WriteAsync|"write"' tests/Kumbu.Server.Tests/Mcp`), and the classes writing `/Skills/…` text paths: `HandshakeInstructionsTests` 16, `SkillLinkWithdrawalTests` 12, `AddSkillTests` 22, `SkillUpdateCheckTests` 20, `SkillsShellTests` 7, `SkillUpdateTakeTests` 14, `MarketplaceApiTests` 23, `SkillPageApiTests` 42.
- After the caller commits: `FileApiTests` 32, `LargeFileTests` 21, `LargeFileRefusalTests` 3, `AgentUploadApiTests` 21, `UploadToolTests` 19, `--filter-namespace "Kumbu.Server.Tests.Dav"` 299, `SyncApiTests` 23, `SkillUpdateTakeTests` 15, `--filter-namespace "Kumbu.Server.Tests.Publishing"` 214; all passed.
- Final full runs at `b2653ca`:
  - `dotnet test --project tests/Kumbu.Core.Tests`: total 3765, failed 0, succeeded 3695, skipped 70 (28 s).
  - `dotnet test --project tests/Kumbu.Server.Tests`: total 4037, failed 0, succeeded 4022, skipped 15 (25 min 44 s).
  - Together 7802 tests, 85 skipped, 0 failed (baseline in the ledger: 7780, 85 skipped, 0 failed); the new tests are 6 in Core (the theory counts twice) and 10 in the server, 16 in all, the difference to 22 being whatever Task 1 added.

#### Existing tests changed

None changed in what they assert. `TextAttachmentTests.Make` now also adds the vault `Tools` (`store.AddVault("Tools")`), which no existing test there reads. No test relied on the default name: the fake already set `SkillsVault = "Skills"` where it wrote into `/Skills/…`, and the server's store marks its vault Skills.

#### Every caller and its verdict

`rg -n 'ForPath\(' src` and `rg -n 'AttachmentFormat\.(Problem|LargeProblem|TooLargeToCheck|RenameProblem|IsPdf|KindOf)\(' src` at `896b83f`.

| Caller | Verdict |
|---|---|
| `NoteWriter.cs:129` (`WriteAsync`), `:612-616` (`NotText`) and its callers `:147,198,333`, `Replacer.cs:32` | changed (`09edff7`): the mark from `store.SkillsVaultAsync` |
| `FileApi.cs:194` (`Problem`), `:213` (`TooLargeToCheck`), `:214` (`LargeProblem`) | changed (`ea39479`) |
| `FileApi.cs:215` (large image's dimensions) | stays: a large `.svg` is refused just before by `TooLargeToCheck` as SVG and as a skill's text file alike, so `ForPath` meets no `.svg` there; for other names the skill rule can't make an image |
| `FileApi.cs:282` (`Dimensions`) | stays: an `.svg` is Svg (excluded by `!= Svg`) or Text (not an image), null either way; the writer reads an SVG's size itself |
| `FileApi.cs:141` (`OnlyAsDownload`) | stays: already passes the mark |
| `DavWrites.cs:623,624,634` | changed (`28c0679`), the mark through a new one-line `DavTree.SkillsVaultAsync` (the tree already holds the share's store and asks it at `DavTree.cs:82`) |
| `DavWrites.cs:760` (`Kept`) | stays: its format only refuses a marker and decides image dimensions; an `.svg` gets none as Svg (`Dimensions` excludes it, a large one is refused first) or as Text, and the writer decides the format by the mark |
| `SyncApi.cs:186` (`IsText`), and `:189` (`KindOf`) | changed (`7b048ba`): `access.IsSkillsVault ? unlocked.Name : null`; `KindOf` had to change with it (see Concerns) |
| `SkillUpdateTaker.cs:264` (`IsText`) | changed (`d41b324`): the copy's `Access.IsSkillsVault` |
| `AgentUploadApi.cs:60` | changed (`c60beba`): can differ for `.svg` (Svg, an image, vs Text); only the unmarked-`Skills` case reaches it, as the upload tool refuses a skill's text file before issuing a URL |
| `Publications.cs:909`, `Bundles.cs:185` (`MayCopy`) | changed (`8f0afa3`): can differ for `.svg`; a marked vault's whole content is excluded by `InSkillPlace`, so only the unmarked `Skills` was wrong |
| `SkillCopier.cs:175` (`Dimensions`) | stays: an `.svg` is Svg or Text, and the writer ignores given dimensions for both (it reads an SVG's size itself, `AttachmentWriter.cs:72`) |
| `MarketplaceIndex.cs:275,276` (`IsStorable`) | stays: the path is built in a vault named `SkillPaths.SkillsVault` (`:265`), so the default name is the right mark by construction; it asks whether a file fits any skill's folder, not a caller's vault |
| `AttachmentFormat.cs:100` (`IsPdf(VaultPath)`) and its callers `SectionSearch.cs:213,250`, `Semsearch.cs:105`, `EncryptedNoteStore.cs:1261,1348` | stays: `.pdf` is no text name, so the skill rule can't change the answer |
| `AttachmentFormat.cs:153` (`KindOf`) and its callers `NoteApi.cs:586,652,913`, `HistoryApi.cs:240,265,464`, `RecentApi.cs:76`, `Activity.cs:183` | left, open-items row (`b2653ca`): `KindOf` got an optional `skillsVault` (default unchanged) for `SyncApi`; the others don't all hold the mark (`NoteApi`'s file view is a static mapper, `Activity` builds rows in Core) |
| `AttachmentFormat.cs:130,131,146,167,192,197` (inside `RenameProblem`, `Problem`, `LargeProblem`, `TooLargeToCheck`) | stays: they pass their own `skillsVault` on |
| `AttachmentWriter.cs:63,70,102,103,125`, `UploadTool.cs:67,71`, `NoteMover.cs:189`, `TreeWriter.cs:112`, `NoteApi.cs:797` | stays: already pass the mark |

`IsPdf(AttachmentState)` callers (`EncryptedNoteStore.cs:963`, `PdfAnalysisRunner.cs:81`, `Csv.cs:174`, `SearchIndex.cs:205,295`, `AttachmentText.cs:27`) go by the stored media type, not `ForPath`: not in scope.

#### Mutations

Each: break one line, run the test, restore with `git checkout -- <file>`.

- (a) `NoteWriter.WriteAsync`: `var skillsVault = Kumbu.Core.Skills.SkillPaths.SkillsVault;` → `TextAttachmentTests` failed 2: `Write_refuses_a_text_name_in_an_unmarked_vault_named_Skills_as_anywhere` (actual `"created /Skills/x/run.py"`), `Write_creates_a_text_file_in_a_skill_of_a_marked_vault_of_another_name` (`write: /Tools/x/scripts/run.py: note names end in .md; …`).
- (b) `NotText`: `ForPath(path, SkillPaths.SkillsVault)` in the Text and File arms → failed 3: `Edit_refuses_…` (`"edit: /Tools/x/run.py: note names end in .md; edit"···`), `Replace_refuses_…`, `Write_refuses_a_text_name_in_an_unmarked_vault_named_Skills_as_anywhere` (`"write: run.py is a text file in a skill; replace i"···`).
- (c1) `Replacer.cs:32`: `NotText(tool, path, SkillPaths.SkillsVault)` → `Replace_refuses_…` failed (`"replace: /Tools/x/run.py: note names end in .md; r"···`).
- (c2) `FileApi.cs` small path: `Problem(filePath, bytes.Span)` → `An_upload_finds_a_skills_folder_by_the_mark_not_the_name` failed (`Expected: Created, Actual: BadRequest`).
- (c3) `FileApi.cs` large path: `TooLargeToCheck(filePath)` → `A_large_file_finds_…` failed (413 for `/Skills/x/big.bin`).
- (c4) `DavWrites.cs` `matches = Problem(path, bytes.Span)` → `Put_finds_…` failed (`Expected: UnsupportedMediaType, Actual: BadRequest`).
- (c5) `DavWrites.cs` `TooLargeToCheck(path)` → `A_large_put_finds_…` failed (413 for `/dav/Skills/x/big.bin`).
- (c6) `SyncApi.IsText` without the mark → `Text_files_are_told_apart_…` failed (`Collection: ["/Tools/x/run.py"]`).
- (c7) `SyncApi.Entry` `KindOf(path.Value)` → the same test failed (`Expected: "file", Actual: "text"`).
- (c8) `SkillUpdateTaker.IsText` without the mark → `Review_diffs_…` failed (`Not found: "+print('new')"`).
- (c9) `AgentUploadApi.View` without the mark → `An_svg_in_…` failed (`Actual: "[diagram.svg](/x/diagram.svg)"`).
- (c10) `Publications.cs` `MayCopy` without the mark → `An_svg_…_goes_with_its_note` failed, the bundle test passed.
- (c11) `Bundles.cs` `MayCopy` without the mark → `…_in_a_publication_of_several` failed, the single-note test passed.

#### Files changed

- `src/Kumbu.Core/Notes/NoteWriter.cs`, `src/Kumbu.Core/Notes/Replacer.cs`, `src/Kumbu.Core/Attachments/AttachmentFormat.cs` (`KindOf`'s optional argument)
- `src/Kumbu.Server/Api/FileApi.cs`, `src/Kumbu.Server/Api/SyncApi.cs`, `src/Kumbu.Server/Api/AgentUploadApi.cs`
- `src/Kumbu.Server/Dav/DavTree.cs`, `src/Kumbu.Server/Dav/DavWrites.cs`
- `src/Kumbu.Server/Skills/SkillUpdateTaker.cs`
- `src/Kumbu.Server/Publishing/Publications.cs`, `src/Kumbu.Server/Publishing/Bundles.cs`
- `tests/Kumbu.Core.Tests/Attachments/TextAttachmentTests.cs`
- `tests/Kumbu.Server.Tests/Api/RestClient.cs` (helper), `FileApiTests.cs`, `LargeFileTests.cs`, `SyncApiTests.cs`, `AgentUploadApiTests.cs`
- `tests/Kumbu.Server.Tests/Dav/DavSkillTextTests.cs`, `DavLargeFileTests.cs`
- `tests/Kumbu.Server.Tests/Skills/SkillUpdateTakeTests.cs`, `tests/Kumbu.Server.Tests/Publishing/PublishedFilesTests.cs`
- `docs/superpowers/open-items.md` (one row)

Nothing under `src/Kumbu.Server/Auth`, `OAuth`, `Sharing` or `Vaults/EncryptedNoteStore.cs` changed.

#### Concerns

1. **`KindOf` got an optional argument.** The brief says `KindOf` stays unless the mark is in scope at each caller. In `SyncApi` the text filter and the entry's kind have to agree: with `IsText` fixed alone, a file below a folder of an unmarked `Skills` went to a client that didn't ask for text files with the kind `"text"` (the test showed it), worse than before. So `KindOf(path, skillsVault = SkillPaths.SkillsVault)` takes the mark, `SyncApi` passes it, and every other caller is unchanged (open-items row). Revert to the row only if you'd rather leave `SyncApi` as a whole.
2. **`DavTree.SkillsVaultAsync`** is a new one-line public member forwarding to the store the tree already holds; no new dependency. The alternative was a row for DAV.
3. **How real the cases are.** The server names every marked vault Skills and renames an unmarked one at sign-in (`SkillsVaults.EnsureAsync`), so a marked vault of another name exists only when made by hand, and an unmarked `Skills` only for a user not signed in since the upgrade (her API keys and DAV shares still reach it). The fixes are about agreeing with the writers, not about a common path.
4. **Extra cost per call:** `write`, `edit` and `replace` now call `SkillsVaultAsync` once each, the files API and a DAV `PUT` once more. In the server it reads the store's vault list (`EncryptedNoteStore.cs:126`, `VaultsAsync`), which the same request opens anyway to write.
5. The red `Replace_refuses_…` test is beyond the brief's four, to cover `Replacer`'s call.

### Task 3

### Task 3 report: §3.9 `links` on an empty folder

Commit: 634e615 "Answer links on an empty folder as on a folder with no broken links"

#### RED

Command: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*LinkCommandsTests"`

Summary: total 90, failed 5, succeeded 85.

- `Links_on_an_empty_vault_finds_nothing_broken`: Actual `links: /Home: Is a directory` (IsError).
- `Links_on_a_folder_holding_only_an_image_finds_nothing_broken("links /Work/Screens")` and `("links /Work/Screens/")`: Actual `links: /Work/Screens: No such file or directory`.
- `Links_on_a_folder_made_by_mkdir_finds_nothing_broken`: Actual `links: /Work/Drafts: No such file or directory`.
- `Links_with_a_version_on_an_empty_folder_keeps_its_error`: Actual `links: /Home: Is a directory` (expected the directory-has-no-versions error; today's code reaches it only when notes are below).

Expected: `LinksCommand.cs:70` took the folder form only when `ListNotesUnderAsync` returned notes. `Links_on_a_missing_folder_is_no_such_file` (`/Work/nothing`, `/Work/nothing/`, `/Nope`) passed already, as it must.

#### GREEN

`LinksCommand.IsFolderAsync`: notes below, or the path is a vault in `ListVaultsAsync`, or `StoreAttachments.UnderAsync(...)` is non-empty (attachments or a folder marker), as `ArchiveCommand.IsFolderAsync` decides plus the vault check. Used in place of the notes-only check; `CheckDirectoryAsync` answers `(no broken or ambiguous links below <dir>)` when nothing is below.

- `--filter-class "*LinkCommandsTests"`: total 90, failed 0, succeeded 90.
- Whole `dotnet test --project tests/Kumbu.Core.Tests` (with `KUMBU_TEST_MONGO`): total 3773, failed 0, succeeded 3703, skipped 70.

#### Mutations (each restored with `git checkout -- src/Kumbu.Core/Sh/Commands/LinksCommand.cs`)

- (a) vault check replaced by `false`: `Links_on_an_empty_vault_finds_nothing_broken` and `Links_with_a_version_on_an_empty_folder_keeps_its_error` fail, Actual `links: /Home: Is a directory` (2 failed).
- (b) attachments/marker check replaced by `false`: both `Links_on_a_folder_holding_only_an_image_finds_nothing_broken` cases (Actual `links: /Work/Screens: No such file or directory`) and `Links_on_a_folder_made_by_mkdir_finds_nothing_broken` (Actual `links: /Work/Drafts: No such file or directory`) fail (3 failed).
- (c) `IsFolderAsync(...)` replaced by `true`: the three `Links_on_a_missing_folder_is_no_such_file` cases (Actual `(no broken or ambiguous links below /Work/nothing)` / `/Nope`) and the existing `LinkErrors("links /Demo/nope.md")` fail (4 failed).

#### Files changed

- `src/Kumbu.Core/Sh/Commands/LinksCommand.cs` (the folder check at `:70`, new `IsFolderAsync`)
- `tests/Kumbu.Core.Tests/Sh/LinkCommandsTests.cs` (five new tests after `LinksOfADirectoryWithoutProblems`)

#### Notes and concerns

- The help (`LinksCommand.cs:39-41`, "Check every note below a directory (or a whole vault) …") stays true for an empty folder, so it is unchanged; the synopsis is untouched (a later task adds `--version V`).
- The brief's open point 3 points at `Blame.cs:58-63` (now `:65-66`) for the rule; blame checks the vault list but not attachments or markers. Here the rule follows `ArchiveCommand.cs:110-111` plus the vault check, as the plan says. blame on a folder holding only an image still answers "No such file or directory"; not in scope, worth a row if wanted.
- `--version` test uses token `3`; the error comes from the folder branch, so any token gives it.

### Task 4

### Task 4 report: §3.10 `blame` on a folder

Commit: 92a7cc5 "Refuse blame --lines on a folder and say in help what blame does there"

#### RED

Commands: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*BlameTests"` and `-- --filter-class "*HelpTests"`

- BlameTests: total 19, failed 4.
  - `Blame_lines_on_a_folder_is_refused` (`blame --lines /Work`, `blame --lines /Work/Protocols/`, `blame /Work/Protocols --lines`): Actual the folder counts (`/Work/Protocols/a.md  Veyalt 1 / total: …`), IsError False: `--lines` was ignored.
  - `Its_help_says_who_wrote_what` (updated to the new synopsis): Actual starts `"blame [--lines] <path>\n  Who wrote what: …"`.
- HelpTests: total 27, failed 1: `Help_blame_names_the_folder_limit_and_the_section_forms`, Actual starts `"blame [--lines] <path>\n  Who wrote what: …"`.

Expected: no refusal existed, and the help lacked `[#section]`, `#^block`, the limit and the "… and N more notes" line.

#### GREEN

- `Blame.cs`: `if (options.Has("lines")) return ShellResult.Error("blame: --lines takes a note, not a folder");` inside the folder branch, before `FolderAsync` (a missing path with `--lines` keeps "No such file or directory").
- `MaxNotes` `private const` → `internal const` (Core.Tests has InternalsVisibleTo).
- Help: synopsis `blame [--lines] <path>[#section]`; "A section (note.md#Heading) or a block (note.md#^block) limits it to that part."; folder: "At most {MaxNotes} notes, fewer if they don't fit one call; the rest are counted before the total: "… and N more notes; blame a folder below it". --lines takes a note, not a folder." (matches `FolderAsync`, which also cuts at the output limit). No literal braces in the interpolated help; all lines under 120 characters.
- `Usage` (already `[#section]`) and the `Manual.cs:33` tool-description line (says "#section works") unchanged.

Results:
- BlameTests: total 19, failed 0. HelpTests: total 27, failed 0.
- Whole `dotnet test --project tests/Kumbu.Core.Tests` (with `KUMBU_TEST_MONGO`): total 3777, failed 0, succeeded 3707, skipped 70.

#### Mutations (each restored with `git checkout -- src/Kumbu.Core/Sh/Commands/Blame.cs`)

- (a) refusal line deleted: the three `Blame_lines_on_a_folder_is_refused` cases fail, Actual the folder counts (`/Work/protocol.md     seed 4`, `/Work/Protocols/a.md  Veyalt 1`).
- (b) refusal moved before `notes.GetAsync`, for notes too: `Lines_prints_every_line_with_its_own_time_and_text`, `A_section_limits_it_and_keeps_the_notes_line_numbers`, `Lines_too_long_for_one_call_ask_for_a_section`, `A_first_line_too_long_for_one_call_gives_the_hint_alone`, `Lines_too_many_for_one_call_keep_the_restart_under_the_limit` fail, Actual `"blame: --lines takes a note, not a folder"` (5 failed).
- (c) help hard-codes `At most 300 notes`: `Help_blame_names_the_folder_limit_and_the_section_forms` fails, `Not found: "At most 200 notes"`.

#### Files changed

- `src/Kumbu.Core/Sh/Commands/Blame.cs`
- `tests/Kumbu.Core.Tests/Sh/BlameTests.cs` (new `Blame_lines_on_a_folder_is_refused`; `Its_help_says_who_wrote_what` updated to the new synopsis)
- `tests/Kumbu.Core.Tests/Sh/HelpTests.cs` (new `Help_blame_names_the_folder_limit_and_the_section_forms`)

#### Notes and concerns

- `Blame_lines_on_a_note_still_works` was not added: `Lines_prints_every_line_with_its_own_time_and_text` already runs `blame --lines /Work/protocol.md` and asserts its output (mutation (b) shows it guards this); a comment above the new test names it.
- `docs/superpowers/specs/2026-11-18-blame-design.md:155-160` quotes the old help; other specs stay as written (spec, out of scope).

### Task 5

### Task 5 report: §3.1, §3.2, §3.3 in Help.cs, archive's help, rg's pipe error

Commit: b378dcf "Say in help that graph and calendar hide the archive too, and what rg --files and write take"

#### Code read for each text
- graph: `GraphCommand.cs:19` (`--archive`), `:54`, `:118` (`ArchiveFolder.Shown(scope, --archive)`; a path inside the archive shows it).
- calendar: `EntryReader.cs:60` (archived path is never an entry), `CalendarCommand.cs:351`; no `--archive` in any of its option specs.
- rg --files: `Rg.cs:807-818` (notes + text files, archive filter on both), `:788-799` (with --tag: notes only); rg search hides archived text files too (`Rg.cs:259-273`).
- write/upload: `NoteWriter.cs:127-146` (SVG and skill text file through the attachment writer), `NoteWriter.NotText` (`:614-621`: edit/replace refuse every attachment), `UploadTool.cs:66-72` (refuses a note, a skill's text file, a marker; takes an SVG).
- 15 MB: `AttachmentFormat.MaxBytes = 15 * 1024 * 1024`; the help writes it as `{AttachmentFormat.MaxBytes / (1024 * 1024)} MB` (as `DavWrites.cs:530` does for MiB); `AttachmentText.Size` would print "15.0 MB".

#### RED
`dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"`: total 31, failed 5, succeeded 26
- Archive_topic_names_graph_and_calendar, Archive_help_names_graph_and_calendar, Attachments_topic_says_rg_files_lists_text_files_too, Attachments_topic_says_what_write_takes (new), Help_archive_shows_the_command_and_then_the_archive_topic (pin updated to the corrected rg line). All "Assert.Contains() Failure: Sub-string not found".
`dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*RgTests"`: total 55, failed 1, succeeded 54
- Errors(line: "ls / | rg --files"): Expected "rg: --files lists notes and text files; it does no…", Actual "rg: --files lists notes; it does not read from a p…"

#### GREEN
- `--filter-class "*HelpTests"`: total 31, failed 0, succeeded 31
- `--filter-class "*RgTests"`: total 55, failed 0, succeeded 55
- `--filter-class "*ArchiveCommandTests"`: total 33, failed 0, succeeded 33 (its pins "unless given --archive or a path" and "hiding the archive is about attention, not access" kept on one line each)
- `--filter-class "*ArchiveHiddenTests"`: total 25, failed 0, succeeded 25
- `dotnet test --project tests/Kumbu.Core.Tests` (before commit): total 3781, failed 0, succeeded 3711, skipped 70

#### Mutations (each restored with `git checkout -- <file>`; tree clean afterwards)
1. Removed the topic's `graph` line → Archive_topic_names_graph_and_calendar failed: Sub-string not found.
2. Removed the topic's `calendar` line → Archive_topic_names_graph_and_calendar failed: Sub-string not found.
3. Restored the old "Asking" sentence (no calendar sentence) → Archive_topic_names_graph_and_calendar failed: Sub-string not found.
4. Restored "'rg --files' lists notes only;" → Attachments_topic_says_rg_files_lists_text_files_too failed: Sub-string not found.
5. Restored the old write/SVG paragraph → Attachments_topic_says_what_write_takes failed: Sub-string not found.
6. Restored the old rg line of the archive topic → Help_archive_shows_the_command_and_then_the_archive_topic failed: Sub-string not found.
7. `{AttachmentFormat.MaxBytes / (1024 * 1024) + 1} MB` → Help_lists_the_attachments_topic failed: Not found: "larger than 15 MB is kept". (A first try with `/ 1000000` still gave 15 and passed: not a mutation; redone.)
8. Restored archive's old help sentence → Archive_help_names_graph_and_calendar failed: Sub-string not found.
9. Restored the old pipe error → RgTests.Errors(line: "ls / | rg --files") failed: Expected "…notes and text files…", Actual "rg: --files lists notes; it does not read from a p…".

#### Files changed
- src/Kumbu.Core/Sh/Commands/Help.cs (attachments topic: write paragraph, rg --files, 15 MB from the constant; archive topic: graph and calendar lines, calendar sentence, rg line)
- src/Kumbu.Core/Sh/Commands/ArchiveCommand.cs (:35-37)
- src/Kumbu.Core/Sh/Commands/Rg.cs (:787)
- tests/Kumbu.Core.Tests/Sh/HelpTests.cs (4 new tests; pin of the rg archive line updated)
- tests/Kumbu.Core.Tests/Sh/RgTests.cs (:158 updated)

#### Found on the way
- Fixed: the archive topic's rg line said rg leaves out the archive's notes and PDFs; it leaves out its text files too, in a search and in --files (`Rg.cs:269,817-818`). Now "searches none of its notes, text files or PDFs, and --files lists none of them"; HelpTests' pin updated to it.
- Not fixed (files outside this task): `Rm.cs:18` help says rm moves "notes, images or PDFs", but it removes any attachment (`Rm.cs:58-61`); `Mv.cs` help says "An attachment (an image or a PDF)"; `Trash.cs` Summary and help leave out folders, though trash lists removed folders (`Trash.cs:51-52`). `Rg.cs:774` and `:84-85` (-l/-c) are Task 11's.
- Left: "1 GiB" in the attachments topic stays literal: its constant `VaultOptions.DefaultMaxFileBytes` lives in Kumbu.Server, which Kumbu.Core can't reference.

#### Concerns
- The graph line keeps the brief's "--archive takes them in" although the "Asking" line below says the same for all; kept as the brief asks.
- "the vault Skills" in archive's help (`:33`) is right as the README's name for the marked vault; unchanged.

### Task 6

### Task 6 report: §3.2, §3.3, §3.13, §3.14 in Manual.cs

Commit: 0787477 "Say in the tool descriptions what rg --files, write, upload, rm, trash and mv take"

#### Code read
- rg --files: `Rg.cs:807-818`; in a checkpoint rg searches notes and text files, not PDFs: a checkpoint's Rg gets no pixels (`Shell.cs:86`, `live ? pixels : null`), so its PDF list is empty (`Rg.cs:223`), while text files come from the view's attachments (`Notes/CheckpointView.cs:49-52`, `Rg.cs:225`).
- rm: `Rm.cs:56-61` (any attachment, empty folder); trash: `Trash.cs:40-55` (notes, attachments, folders); mv: `Mv.cs` (notes, attachments, folders).
- write/upload: `NoteWriter.cs:127-146`, `UploadTool.cs:66-72` (as in Task 5).
- Numbers: `NoteWriter.TrashRetention` (30 days), `OutputLimit.MaxChars / CharsPerToken` (24 000 / 3 = 8000 tokens), `AttachmentFormat.MaxBytes` (15 MiB, "15 MB"), `Plans.Lifetime` (1 hour, ReplaceDescription), and in Kumbu.Server `UploadTickets.Lifetime` (10 minutes) and `VaultOptions.DefaultMaxFileBytes` (1 GiB, shown by `FileTooLargeException.Shown`). Head/tail's "default 10" has no constant (`Head.cs:31`, `Tail.cs:32`): no test.

#### RED
`dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ManualTests"`: total 9, failed 5, succeeded 4
- failed: Manual_write_says_what_it_takes, Manual_rg_files_lists_notes_and_text_files, Manual_says_rg_searches_text_files_in_a_checkpoint, Manual_upload_says_an_SVG_goes_either_way, Manual_rm_trash_and_mv_name_attachments.
- The 4 number tests pass from the start (the numbers are right today); their mutations below show they hold.
`dotnet test --project tests/Kumbu.Server.Tests -- --filter-method "*The_description_takes_its_numbers_from_the_constants"`: total 1, succeeded 1 (number test, as above).

#### GREEN
- `--filter-class "*ManualTests"`: total 9, failed 0, succeeded 9
- `--filter-class "*HelpTests"`: total 31, failed 0, succeeded 31 (The_readers_that_hide_the_archive_take_archive, ToolDescriptionMentionsEveryCommand green)
- `--filter-class "*ArchiveHiddenTests"`: total 25, failed 0 (column alignment of the list: summaries at column 34)
- `--filter-class "*ShellTests"`: total 27, failed 0
- `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*UploadToolTests"`: total 20, failed 0, succeeded 20
- `dotnet test --project tests/Kumbu.Core.Tests` (before commit): total 3790, failed 0, succeeded 3720, skipped 70

#### Mutations (each restored with `git checkout -- <file>`; tree clean afterwards)
1. Old rg --files line → Manual_rg_files_lists_notes_and_text_files: Expected "…list note and text file paths; find", Actual "…list note paths; find a note by nam".
2. Old Checkpoints sentence → Manual_says_rg_searches_text_files_in_a_checkpoint: Sub-string not found.
3. Old rm line → Manual_rm_trash_and_mv_name_attachments: Expected "move notes, attachments or empty fo…", Actual "move notes or empty folders to the …".
4. Old trash line → same test: Expected "list removed notes, attachments and folders", Actual "list removed notes and folders".
5. Old mv line → same test: Expected "move or rename notes, attachments or folders (/", Actual "move or rename notes, or folders (/Work/old/); ".
6. Old "The path must end in .md" sentence → Manual_write_says_what_it_takes: DoesNotContain() Failure: Sub-string found.
7. Old upload sentence → Manual_upload_says_an_SVG_goes_either_way: Sub-string not found.
8. `TrashRetention` 31 days → Manual_trash_retention_is_the_constants: Sub-string not found.
9. `OutputLimit.MaxChars` 30 000 → Manual_output_limit_is_the_constants: Sub-string not found.
10. `AttachmentFormat.MaxBytes` 20 MiB → Manual_upload_size_is_the_constants: Sub-string not found.
11. `Plans.Lifetime` 2 hours → Manual_replace_plan_lifetime_is_the_constants: Expected 01:00:00, Actual 02:00:00.
12. `UploadTickets.Lifetime` 15 minutes → UploadToolTests.The_description_takes_its_numbers_from_the_constants: Sub-string not found.
13. `VaultOptions.DefaultMaxFileBytes` 2 GiB → same test: Sub-string not found.

#### Files changed
- src/Kumbu.Core/Sh/Manual.cs (rg --files, mv, rm, trash lines, Checkpoints sentence; WriteDescription's create paragraph; UploadDescription's second sentence)
- tests/Kumbu.Core.Tests/Sh/ManualTests.cs (new, SPDX header)
- tests/Kumbu.Server.Tests/Mcp/UploadToolTests.cs (one test: 10 minutes and 1 GiB, whose constants live in Kumbu.Server)

#### Found on the way
- Not fixed: `Manual.cs:13` says rg searches "notes and PDF text"; it searches text files too (Rg's Summary names them). It says less rather than something false, and the list changes only where §3 names it, so it stays; worth a decision.
- Not fixed: WriteDescription's first line, "Create a note, or replace a note's whole content", names notes only; the paragraph on SVGs and now the create paragraph cover the rest.

#### Concerns
- The number tests pass before the change by nature (the numbers are right); the constant mutations are their evidence.
- The server test sits in UploadToolTests, whose collection fixture starts the app, so it needs KUMBU_TEST_MONGO like its neighbours.

#### Fix after review (Tasks 5+6, three Minor findings)

Commit: 6602345 "Say that graph leaves archived attachments out too, and that rg searches text files"

What changed:
1. Help.cs archive topic, graph row: "leaves its notes and attachments out; --archive takes them in". Graph filters both by `node.Archived` (`GraphQueries.cs:187,200`; attachments are marked at `LinkGraph.cs:135`). The HelpTests pin is updated.
2. Re-wrapped to the paragraph width (about 100 characters): archive's help (`ArchiveCommand.cs:35-37`, now lines of 88, 89 and 80 characters) and the attachments topic's "What doesn't" paragraph, so "only (with --tag, notes only);" is no longer a line on its own. `Archive_help_names_graph_and_calendar` now asserts the three wrapped lines exactly. `Attachments_topic_says_rg_files_lists_text_files_too` asserts the re-wrapped line. `ArchiveCommandTests.Help_says_how_to_restore_and_that_the_archive_is_not_access_control` now checks its two phrases against the help with whitespace collapsed, because the new wrap splits them across lines. The phrases themselves are unchanged.
3. Manual.cs:13: "search notes, text files and PDF text, prints path:line:text; …". The rest of the line and the column are unchanged. The new `ManualTests.Manual_rg_searches_notes_text_files_and_PDF_text` asserts the whole line.

RED (before the text changes):
- `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"`: total 31, failed 3 (Archive_help_names_graph_and_calendar, Attachments_topic_says_rg_files_lists_text_files_too, Archive_topic_names_graph_and_calendar)
- `--filter-class "*ManualTests"`: total 10, failed 1 (Manual_rg_searches_notes_text_files_and_PDF_text)
- `--filter-class "*ArchiveCommandTests"`: total 33, failed 0 (only the whitespace of its check changed)

GREEN:
- HelpTests 31/31, ManualTests 10/10, ArchiveCommandTests 33/33, ArchiveHiddenTests 25/25 (column alignment of the list)
- `dotnet test --project tests/Kumbu.Core.Tests`: total 3791, failed 0, succeeded 3721, skipped 70

Mutations (each restored with `git checkout -- <file>`; tree clean afterwards):
1. Graph row back to "leaves its notes out;": Archive_topic_names_graph_and_calendar failed: Sub-string not found.
2. Archive help back to the ragged wrap: Archive_help_names_graph_and_calendar failed: Sub-string not found.
3. Attachments topic back to the orphan line: Attachments_topic_says_rg_files_lists_text_files_too failed: Sub-string not found.
4. Manual rg line back to "search notes and PDF text": Manual_rg_searches_notes_text_files_and_PDF_text failed: Expected "…search notes, text files and PDF text…", Actual "…search notes and PDF text, prints pat…".

The "Found on the way" item about Manual.cs:13 above is now fixed by this ruling.

### Task 7

### Task 7 report: skills, pdfimages, xml (and html)

Commit: `6b75623` Say in help what skills, pdfimages and xml do, with pdfimages' real time limits

#### RED

Commands (each run on its own):

    dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*SkillsCommandTests"
    dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*PdfImagesTests"
    dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*XmlTests"
    dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HtmlTests"

(`PdfImages.MaxImages` was made `internal` before the red run, so the red is an assertion failure, not a compile error.)

    failed SkillsCommandTests.Help_skills_explains_them                   Assert.Contains() Failure: Sub-string not found
    failed SkillsCommandTests.An_unknown_subcommand_points_to_the_Skills_screen
           Expected: ···"d 'install'; skills reads, finds and copies a buil"···  Actual: ···"d 'install'; skills reads and finds (ls, find, cat"···
      succeeded: 36 (2 failed)
    failed PdfImagesTests.Help_lists_pdfimages_after_pdf                   Assert.True() Failure
    failed PdfImagesTests.Its_usage_shows_the_three_forms                  Expected "pdfimages <pdf> [pages] <folder>/ | <pdf> <page> -"··· Actual "pdfimages <pdf> [pages] <dir>/"
    failed PdfImagesTests.Help_gives_the_time_limits_names_and_the_crop_hint_from_their_constants   Sub-string not found
      succeeded: 73 (3 failed)
    failed XmlTests.Help_says_what_its_budget_stops                        Sub-string not found
      succeeded: 35 (1 failed)
    failed HtmlTests.Help_says_what_its_budget_stops                       Sub-string not found
      succeeded: 56 (1 failed)

#### GREEN

    SkillsCommandTests  total: 38  succeeded: 38
    PdfImagesTests      total: 76  succeeded: 76
    XmlTests            total: 36  succeeded: 36
    HtmlTests           total: 57  succeeded: 57
    HelpTests           total: 31  succeeded: 31

Whole core suite before the commit, `dotnet test --project tests/Kumbu.Core.Tests`:

    Test run summary: Passed!  total: 3795  failed: 0  succeeded: 3725  skipped: 70

#### Mutations (each restored with `git checkout -- <file>`)

- skills help back to "skills only reads and finds:" -> `Help_skills_explains_them` fails: Assert.Contains() Failure: Sub-string not found
- skills "Change a skill's files with write and edit." back -> `Help_skills_explains_them` fails: Sub-string not found
- skills Summary back to "find, read and check the user's skills" -> `Help_skills_explains_them` fails: Sub-string not found
- skills error back to "skills reads and finds (...)" -> `An_unknown_subcommand_points_to_the_Skills_screen` fails: Strings differ
- pdfimages Usage back to `pdfimages <pdf> [pages] <dir>/` -> `Its_usage_shows_the_three_forms` (Strings differ) and `Help_lists_pdfimages_after_pdf` (Assert.True() Failure) fail
- pdfimages "--list stops after about 10 seconds" replaced by "it stops after about 20 seconds" -> `Help_gives_the_time_limits_…` fails: Sub-string not found
- pdfimages ceiling clause replaced by the spec's "a page started finishes within 50 seconds" -> same test fails: Sub-string not found
- pdfimages ".png (.jpg where it is stored as a JPEG)" back to ".png" wording -> same test fails: Sub-string not found
- pdfimages --crop hint removed -> same test fails: Sub-string not found
- pdfimages "after 20 seconds" hard-coded and `Budget` default changed to 30 -> same test fails: Assert.Equal() Failure: Expected 00:00:20 Actual 00:00:30 (and three timing tests fail)
- pdfimages "500" hard-coded twice and `MaxImages` changed to 400 -> same test fails: Expected 500 Actual 400 (and the two 500-refusal tests)
- xml "10 seconds" hard-coded and `Budget` default changed to 12 -> `XmlTests.Help_says_what_its_budget_stops` fails: Expected 00:00:10 Actual 00:00:12
- xml old sentence "A call stops after 10 seconds" restored -> `XmlTests.Help_says_what_its_budget_stops` fails: Sub-string not found
- html old sentence "A call stops after 10 seconds" restored -> `HtmlTests.Help_says_what_its_budget_stops` fails: Sub-string not found

#### Files changed

- src/Kumbu.Core/Sh/Commands/SkillsCommand.cs — Summary "find, read, check and copy skills"; help's two sentences; the unknown-subcommand error.
- src/Kumbu.Core/Sh/Commands/PdfImages.cs — `Usage => Forms`; help interpolated: file name .png or .jpg, the --crop hint over `MaxImages` on the first page, --list `Budget / 2`, storing `Budget`, within a page `Ceiling`, `StoredDpi`; `MaxImages` internal; a `Seconds(TimeSpan)` formatter (invariant culture).
- src/Kumbu.Core/Sh/Commands/Xml.cs — help interpolated: "Reading the file and the overview have no time limit; an XPath and its printing stop after {Budget} seconds", `MaxLength`, `MaxConcatArguments`; a `Seconds` property shared with the time-out error.
- src/Kumbu.Core/Sh/Commands/Html.cs — help interpolated: "Parsing the page, and a selector's matching and printing, stop after 10 seconds; reading the file and the outline have no time limit"; the "10-second budget" became "that time limit"; `CssSelection.MaxLength`, `MaxAlternatives`.
- tests/Kumbu.Core.Tests/Sh/SkillsCommandTests.cs, PdfImagesTests.cs, XmlTests.cs, HtmlTests.cs.

#### Found on the way

- **skills' unknown-subcommand error** (`SkillsCommand.cs:77`) said "skills reads and finds", the same claim as the help: now "skills reads, finds and copies a built-in skill (ls, find, cat, copy, check)"; its test updated to the new wording.
- **html** (`Html.cs:42`): the same case as xml. The budget (`Html.cs:79-80`) is started after the file is read and covers parsing and a selector's matching and printing; `Overview` gets no token. Fixed, with a test.
- **Numbers in the touched helps** now come from their constants: pdfimages' 300 dpi (`StoredDpi`), xml's 1,000 and 8 (`MaxLength`, `MaxConcatArguments`), html's 1,000 and 100 (`CssSelection.MaxLength`, `MaxAlternatives`).
- **Not fixed (outside this task's files):** `README.md:36` still says pdfimages "stops a long PDF after about 20 seconds" and names the file only `.png`. Left for the run's coordinator.
- **Not fixed:** pdfimages' help keeps "8 megapixels", "50 megapixels" and "15 MB" typed out: they are the sidecar's limits, with no constant in Kumbu.Core to take them from (`AttachmentFormat.MaxBytes` is the upload limit, and I couldn't verify from Core that the JPEG fallback is decided by it).

#### Decisions

- **pdfimages' 50 seconds follows the code, not the spec** (brief open point 7): `PdfImages.cs:343-353` stops even within a page after `Ceiling` and names that page as partly stored; the spec's "a page started finishes within 50" is wrong. The help uses the brief's sentence.
- **skills' Summary** is "find, read, check and copy skills", not "... the user's skills": copy copies a built-in skill, so "the user's" would be false for it.
- **The new help tests collapse whitespace** (`Regex.Replace(…, @"\s+", " ")`) for the long sentences, so line breaks in the help don't decide the test; existing tests kept their exact assertions.
- **`Help_lists_pdfimages_after_pdf`** now finds the help-list row by the new Usage (taken from the test's `UsageLine`); its `Manual.ToolDescription` assertion (`pdfimages <pdf> [pages] <dir>/`) stays, as that line is still true (one form, not false) and the brief says not to touch Manual's list unless false.
- Seconds are formatted with `"0.###"` and the invariant culture, as the commands' time-out errors already do.

#### Concerns

- The number mutations fail first on the test's assertion of the default (`Assert.Equal(TimeSpan.FromSeconds(20), command.Budget)`), as the brief asks the tests to assert, rather than on the help text itself.

---

### Fix report: review findings on Tasks 7+8

Commit: `c8b673f` Say in pdfimages' help that a storing call lists its pages first, within the same time as --list

1. **Storing lists first, too.** A storing call runs `ListPagesAsync` (`PdfImages.cs`, the `Budget / 2` check) before it stores, so it stores only the pages it listed in about 10 seconds. The help now says: "a call that stores lists its pages first, also for about {Budget/2} seconds, and stores only those listed". The test asserts it, whitespace collapsed.
2. **15 MB from `AttachmentFormat.MaxBytes`.** This is the same limit. The sidecar's JPEG fallback encodes against `MAX_BODY_STORED` (`pdf/src/extract.ts:23-24`, documented as "The attachment limit (AttachmentFormat.MaxBytes)"; used by `encodeStored`, `pdf/src/render.ts:45`, `extract.ts:497`). The help takes it as `{AttachmentFormat.MaxBytes / (1024 * 1024)} MB`, the way `Help.cs:48` does. The test asserts `MaxBytes / MiB == 15` and the text "a JPEG where the PNG would pass 15 MB". (The output lines "over 15 MB even as JPEG" and "(JPEG: PNG over 15 MB)" in `PdfImages.cs` are command output, not help. I left them.)
3. **html rewrapped.** The ragged "…only >" line in `Html.cs` is reflowed to the surrounding width (about 105 printed characters). Wording only, no test.

The paragraph in pdfimages' help was reflowed as well. The existing raw-text assertion "A call stopped within a page says which of its" still holds on one line.

#### RED

    dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*PdfImagesTests"
    failed PdfImagesTests.Help_gives_the_time_limits_names_and_the_crop_hint_from_their_constants   Assert.Contains() Failure: Sub-string not found
      total: 76  failed: 1  succeeded: 75

The 15 MB assertion passed already, since the text was right. The fix moves the number to the constant, and the mutation below shows that the test catches it.

#### GREEN

    PdfImagesTests  total: 76  failed: 0
    HtmlTests       total: 57  failed: 0
    dotnet test --project tests/Kumbu.Core.Tests -> Test run summary: Passed!  total: 3797  failed: 0  succeeded: 3727  skipped: 70

#### Mutations (each restored with `git checkout -- <file>`)

- storing-lists clause removed -> `Help_gives_the_time_limits_…` fails: Assert.Contains() Failure: Sub-string not found
- "15 MB" hard-coded back and `AttachmentFormat.MaxBytes` set to 16 MiB -> same test fails: Assert.Equal() Failure: Expected 15, Actual 16
- html rewrap: none, wording only

Files: src/Kumbu.Core/Sh/Commands/PdfImages.cs, src/Kumbu.Core/Sh/Commands/Html.cs, tests/Kumbu.Core.Tests/Sh/PdfImagesTests.cs

### Task 8

### Task 8 report: publish, cat, calendar through a checkpoint, the Usage width

Commit: `feb2dc1` Say in help what publish's subcommands take, what cat prints, and that calendar only reads in a checkpoint

#### RED

    dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"
    dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*PublishCommandTests"
    dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*CalendarCommandTests"

    failed HelpTests.Help_cat_says_what_each_kind_of_file_prints_and_when_the_embeds_block_is_left_out
           Assert.StartsWith() Failure: Expected start: "cat [--version V] <path>[#section] [<path>…]\n"
      total: 33  failed: 1  succeeded: 32
    failed PublishCommandTests.Help_names_every_subcommand
           Strings differ: Expected ···"again\npublish add <note> [--depth N] [--without <n"··· Actual ···"again\npublish add <note>       publish a copy of t"···
      total: 26  failed: 1  succeeded: 25
    failed CalendarCommandTests.Help_names_every_form
           Assert.EndsWith() Failure: Expected end: "\n  --checkpoint <name>: as a checkpoint holds it; "···
      total: 62  failed: 1  succeeded: 61

`HelpTests.No_usage_is_longer_than_graphs` passed before the change: it is a guard (publish's old Usage was 78 characters, the new one 83; graph's is 119). Its mutation below shows it fails when a Usage grows past graph's.

#### GREEN

    HelpTests             total: 33  failed: 0
    PublishCommandTests   total: 26  failed: 0
    CalendarCommandTests  total: 62  failed: 0
    CatTests              total: 28  failed: 0
    ManualTests           total: 10  failed: 0

Whole core suite before the commit, `dotnet test --project tests/Kumbu.Core.Tests`:

    Test run summary: Passed!  total: 3797  failed: 0  succeeded: 3727  skipped: 70

#### Mutations (each restored with `git checkout -- <file>`)

- publish Usage back to `publish ls|add|update|rm <note> [--depth N] [--without <note>]... [--dry-run]` -> `Help_names_every_subcommand` fails: Strings differ (Expected "publish ls [path] | add|update <note> [--depth N] "···)
- publish "update keeps the stored depth and --without list unless given." removed -> `Help_names_every_subcommand` fails: Strings differ
- publish rm line moved back above the option lines -> `Help_names_every_subcommand` fails: Strings differ
- publish Usage made longer than graph's (146 characters) -> `HelpTests.No_usage_is_longer_than_graphs` fails: Assert.Contains() Failure: Sub-string not found
- cat synopsis back to `cat <path>[#section] [<path>…]` -> `Help_cat_says_…` fails: Assert.StartsWith() Failure
- cat "No block in a pipeline or with --version." restored -> `Help_cat_says_…` fails: Sub-string not found
- cat text-file sentence back to "A text file in a skill's folder prints as it is." -> `Help_cat_says_…` fails: Sub-string not found
- cat SVG sentence cut -> `Help_cat_says_…` fails: Sub-string not found
- cat "Any other file prints one line" removed -> `Help_cat_says_…` fails: Sub-string not found
- cat "An attachment (an image or a PDF) prints one line" restored -> `Help_cat_says_…` fails: Sub-string not found
- calendar sentence switched off in `Help.Of` -> `CalendarCommandTests.Help_names_every_form` fails: Assert.EndsWith() Failure

#### Files changed

- src/Kumbu.Core/Sh/Commands/PublishCommand.cs — Usage `publish ls [path] | add|update <note> [--depth N] [--without <note>]... | rm <note>` (83 characters; `[--dry-run]` only in the synopsis); help: each subcommand's own synopsis line, the option lines under add and update, "update keeps the stored depth and --without list unless given.", then rm; --depth says "(0 to 5; for add, default 0)".
- src/Kumbu.Core/Sh/Commands/Cat.cs — synopsis `cat [--version V] <path>[#section] [<path>…]`; an image or a PDF one line, an SVG its line and then its source (the current one with a version token for write), a text file its content wherever it is, any other file one line; "No block in a pipeline, with --version or with --checkpoint."; the old "A text file in a skill's folder prints as it is." is gone (the new sentence replaces it).
- src/Kumbu.Core/Sh/Commands/Help.cs — `Help.Of` adds, for calendar only, the line "the reading forms only; set, move, close, reopen and unset are refused there" right after the `--checkpoint` line; its summary says so.
- src/Kumbu.Core/Sh/Commands/CalendarCommand.cs — a stale duplicate `<summary>` on `WriteVerb` removed (found on the way).
- tests/Kumbu.Core.Tests/Sh/HelpTests.cs (two new tests), PublishCommandTests.cs (exact help updated, order and Usage asserted), CalendarCommandTests.cs (EndsWith updated to the new end).

#### Code read for each sentence

- publish: `Publications.cs:161-162` (`depth ?? entry.Depth ?? 0`, `without ?? entry.Without`), `PublishCommand.cs:144-147` (options only on add and update, --dry-run only on add), `:173` (no --without given is null, so the stored list is kept).
- cat: `Cat.cs:184-189` (any `text/plain` attachment, no folder check), `:193-199` (SVG line then source; a version token only without --version), `:202` and `AttachmentText.Line` (other files: name, kind, size); `Shell.cs:144-149` (the embeds block only when the line has one stage and cat one argument, so neither `--version V` nor `--checkpoint N`, both extra arguments, get it; `RunInCheckpointAsync` at `:238` never appends it).
- calendar: `Shell.cs:199-200` and `CalendarCommand.cs:110-111` (a write verb with --checkpoint is refused).

#### Found on the way

- `CalendarCommand.cs:94-95`: `WriteVerb` had two `<summary>` comments, the first stale; removed (no behaviour change).
- Not fixed: publish's "(0 to 5" is typed in Core while the limit is `Bundles.MaxDepth` in Kumbu.Server (internal, not reachable from Core); `PublishCommand.cs:154,159` type the 5 too. Taking it from a constant would need a constant in Core; left as it is.
- Not fixed: the `help` list has no row from `pdf`, `img` or `pdfimages` in `TestShell.Of()` (they need the pixels), so `No_usage_is_longer_than_graphs` doesn't cover them; today their Usage lines are well under 119 (pdfimages 97).

#### Decisions

- **Calendar's sentence in `Help.Of`, not in `CalendarCommand.Help`:** the `--checkpoint` line is the one `Help.Of` appends to every reader, after the command's own help, so only `Help.Of` can print the sentence right after it. A one-line conditional for calendar is the smaller change. Said in the commit body.
- **The calendar line's layout:** on its own line, indented to the description column of the `--checkpoint` line, as a continuation; one line would pass 120 characters.
- **publish's Usage** is the brief's suggested shape exactly (83 characters, within graph's 119); `[--dry-run]` only in the synopsis. `Manual.cs`'s line `publish ls|add|update|rm <note>` stays (still true).
- **The usage-width test** reads the `help` list: help pads every row to the longest Usage + 2, so graph's row reads `graph-usage`, two spaces, its summary exactly when no Usage is longer. No reflection on the shell's private command table.
- **"A text file (a script, a CSV, an HTML or XML file)"**: examples of what a text file is, as `help attachments` defines it (UTF-8 without NUL bytes, `Help.cs:58`).

#### Concerns

- None blocking. The checkpoints topic (`Help.cs:29`) says "Every reader works on it as on the current state" and lists calendar; with calendar's help now naming the refused write forms that reads true for its reading forms, so I left the topic.

### Task 9

### Task 9 report

Commit: 4af5b13 "Name calendar's write verbs among what the console refuses". Files: web/src/console/help.ts, web/src/console/ConsolePage.test.tsx.

New line: "It only reads: pdfimages, restore, mv, archive, mkdir, rm, mkcp, rmcp, publish add, update and rm, skills copy, and calendar set, move, close, reopen and unset are refused, and the write tools aren't here."

RED: `pnpm -C web test --run src/console/ConsolePage.test.tsx` -> Tests 2 failed | 112 passed (114) (whole-line test and the exact refused-list test; old line lacked calendar).
GREEN: same command -> 114 passed (114). `pnpm -C web test --run src/console` -> 5 files, 328 passed. `pnpm -C web build` -> built OK.

Mutations (restored with git checkout):
- removed ", and calendar set, move, close, reopen and unset": 2 failed (shows its own block for help...; names exactly the refused commands of the server in its help).
- removed "skills copy,": same 2 tests failed.

Concerns: none. Test comment at the refused list updated to mention calendar; the refused list in the test is copied from ConsoleApi.ShAsync (ConsoleApi.cs:89-94).

### Task 10

### Task 10 report: §4 part 1, the options of the line readers and others

Commit: `9dc463d` Show in help every option head, tail, wc, sort, outline, links, semsearch, restore, pdf, df, du, backlinks and diff take (BASE c8b673f).

#### Read first (each claim checked in the code)

- `--version`: `Head.cs:8`, `Tail.cs:8`, `Wc.cs:11`, `Sort.cs:12` take it through `Options` and pass it to `CommandArgs.FilterInputAsync` (`CommandArgs.cs:142-159`: refused with piped input, so the help row says "of an earlier version of the note"). `OutlineCommand.cs:30`, `LinksCommand.cs:52` through `TryTakeVersion`.
- semsearch: `new('n', "max", TakesValue: true)` (`Semsearch.cs:28`); `--max=N` and `--max N` both parse (`Options.cs:57`). Default from `DefaultMax` (`:21`).
- restore: `--plan` in `Spec` (`Restore.cs:9`), only with `--checkpoint` (`:48,51`).
- df/du: `new('h', "human-readable")` (`Df.cs:15`, `Du.cs:20`).
- backlinks `--unlinked` (`Backlinks.cs:14,53`); diff `--word` with `--checkpoint` (`DiffCommand.cs:69`).
- links: `LinkKind.Attachment` without a target prints `attachment (not a note)` (`LinksCommand.cs:200`).
- pdf: options in `Spec` (`PdfCommand.cs:25`) plus `--images=all` and the `info` form.

#### RED

`dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"`: total 49, failed 17, succeeded 32. The 16 rows of `Usage_and_help_name_the_options_the_command_takes` and `The_readers_that_hide_the_archive_take_archive(backlinks)` (listUsage) failed, e.g.

    failed ...Usage_and_help_name_the_options_the_command_takes(command: "head", fragment: "[--version V]", synopsis: True)
      Assert.Contains() Failure: Sub-string not found  Not found: "[--version V]"

`dotnet test --project tests/Kumbu.Core.Tests -- --filter-method "*The_usage_names_the_options_of_the_help_synopsis"`: total 1, failed 1 (`Assert.Equal() Failure: Strings differ`, expected the help's two synopsis lines joined by " | ").

#### GREEN

- `--filter-class "*HelpTests"`: total 49, failed 0, succeeded 49.
- `--filter-class "*PdfCommandTests"`: total 74, succeeded 74.
- `--filter-class "*SemsearchTests"`: total 13, succeeded 13 (after the fix below).
- `--filter-class "*DfTests"`: total 109, succeeded 109. `--filter-class "*ArchiveHiddenTests"`: total 25, succeeded 25.
- Whole `dotnet test --project tests/Kumbu.Core.Tests`: total 3814, failed 0, succeeded 3744, skipped 70.

`EveryCommandHasHelpThatStartsWithItsName` is in HelpTests and passed; semsearch's help became `$"""` and holds no literal brace (checked with `rg '[{}]'`).

#### Mutations (each restored with `git checkout -- <file>`)

1. Head Usage back to `head [-n N] [path]` → `Usage_and_help_name_the_options_the_command_takes(head, [--version V])` failed: `Not found: "[--version V]"`.
2. Restore help synopsis without `[--plan <token>]` → restore row failed: `Assert.Contains() Failure: Filter not matched in collection`.
3. du's `-h, --human-readable` row back to `-h` → du row failed: `Not found: "-h, --human-readable"`.
4. links' `'attachment (not a note)' for` back to `'attachment' for` → links row failed: `Not found: "'attachment (not a note)' for other files"`.
5. pdf Usage back to `pdf [info] <path> [pages]` → `PdfCommandTests.The_usage_names_the_options_of_the_help_synopsis` failed: `Assert.Equal() Failure: Strings differ`.
6. backlinks Usage without `[--unlinked]` → 2 failed: the backlinks row and `The_readers_that_hide_the_archive_take_archive(backlinks)`: `Not found: "\n  backlinks [--archive] [--unlinked] <path> "`.

#### Files changed

- src/Kumbu.Core/Sh/Commands/Head.cs, Tail.cs, Wc.cs, Sort.cs (Usage and synopsis `[--version V]`, an option row; tail `[-n N|-n +N]`; wc and sort flags as separate brackets)
- src/Kumbu.Core/Sh/Commands/OutlineCommand.cs, LinksCommand.cs (synopsis `[--version V]`; links `'attachment (not a note)'`)
- src/Kumbu.Core/Sh/Commands/Semsearch.cs (`-n, --max N; default {DefaultMax}`, help now `$"""`)
- src/Kumbu.Core/Sh/Commands/Restore.cs (Usage and synopsis `[--plan <token>]`)
- src/Kumbu.Core/Sh/Commands/PdfCommand.cs (Usage = the two synopsis lines joined by " | ")
- src/Kumbu.Core/Sh/Commands/Df.cs, Du.cs (`-h, --human-readable   accepted, changes nothing` row)
- src/Kumbu.Core/Sh/Commands/Backlinks.cs (Usage `[--unlinked]`), DiffCommand.cs (Usage `[--word]` on the checkpoint form)
- tests/Kumbu.Core.Tests/Sh/HelpTests.cs (new theory; archive theory split into listUsage and manualUsage)
- tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs (new test; `:934` updated to the new synopsis)

#### Decisions and why

1. **The theory has a `synopsis` flag.** The brief asks for the fragment in `help <command>` and in the help-list line. For semsearch `--max N` and df/du `--human-readable`, the spec puts the long form "where its siblings are": the Usage writes short forms (`[-n N]`, `[-h]`), which `ArchiveHiddenTests.cs:310,317`, `SemsearchTests.cs:177,325` and `DfTests.cs:158-170` pin. So those rows, and links' output mark, are checked in the help text only. Every other row is checked in a synopsis line of `help <command>` and in the list line.
2. **pdf's Usage is the full synopsis, `[--images|--images=all]` without spaces.** With the spaces it would be 120 characters, one more than graph's 119 (the Usage-width ruling). Without them it is 118. The help synopsis changed the same way so the two stay equal, and the test asserts that they are equal. `PdfCommandTests.cs:934` was updated to match. The test also checks that graph's row is still padded the same way in a shell with pixels, so pdf doesn't widen the list.
3. **semsearch's default now comes from `DefaultMax`** (numbers from constants). I changed the line in place and didn't rewrap, because `SemsearchTests.Help_documents_C_and_l` pins `"then the\n  section's whole text."`. A first try that rewrapped the paragraph failed that test, so I reverted it.
4. **df/du:** "-h is accepted and changes nothing" moved from the prose into an option row of its own, `-h, --human-readable   accepted, changes nothing`, and the other rows were realigned to it.
5. `Manual.ToolDescription` is unchanged (brief open point 6).

#### Concerns

- The links help says `'attachment (not a note)'` for other files. I didn't trace exactly when the resolver returns `LinkKind.Attachment` without a target. The string itself is verbatim from `LinksCommand.cs:200`.

### Task 11

### Task 11 report: §4 part 2, calendar, rg, trash, history, lscp, ls and numbers from constants

Commits: `75029df` Say in help what calendar, rg, trash, history, lscp and ls -l print; and the found-on-the-way commit `fa75e5a` (below).

#### Read first (each claim checked in the code)

- calendar: notifications takes a vault as a second operand (`CalendarCommand.cs:283-292`). `today · <clock>` is shown for an entry that is ongoing on its day before its time (`:454`, `Calendar/EntryStates.cs:33-35`). `CalendarCommandTests.Notifications_say_today_and_the_time_before_it` shows the row `today · 14:00  Zahnarzt  /Work/Zahnarzt.md`. `--week 46` takes its year from `ISOWeek.GetYear(today)` (`:167-169`, `ParseWeek` `:374-378`).
- rg: text files go through the same `AddLines` as notes (`Rg.cs:453,459-469`), so `-l`, `-c` and `-m` apply to them too. `-l` right of a pipe is refused (`:774`).
- trash: a folder's marker row is `<time>  /Work/Drafts/  folder  (by …)` (`Trash.cs:49-51`, `FolderMarkerHiddenTests.cs:132`).
- history: any attachment (`StoreAttachments.GetAsync`, regardless of kind) lists as `version time writer summary[ (current)[ (restored)]]`, with no message (`History.cs:70-77`). A note's row is `Line(...)` plus `  — message` plus `(current)`/`(restored)` (`:93-96,101-103`).
- lscp: every non-root path ends with `{count} of {limit} checkpoints in {vault}` (`Notes/Checkpoints.cs:153`).
- ls: the root branch never reads `-l` or `--archive` (`Ls.cs:69-90`).
- `NoteWriter.HistoryLimit` is a `public const int` (`NoteWriter.cs:27`). The "10 versions per note" sits in `History.cs` (directory part) and in `Mkcp` (`CheckpointCommands.cs:17`), which is mkcp's help, not lscp's.

#### RED

- `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"`: total 57, failed 7, succeeded 50. The failures were `Help_trash_names_a_folders_row`, `Help_lscp_says_any_path_but_the_root_tells_how_many_the_vault_can_keep`, `Help_history_says_any_attachment_and_how_a_notes_row_ends`, `Help_calendar_names_today_before_a_time_and_the_year_of_a_week_number`, `Help_ls_l_says_it_changes_nothing_in_the_list_of_vaults`, `Help_rg_names_text_files_in_l_and_c_and_that_l_needs_paths` and `Help_calendar_names_the_vault_of_notifications_and_the_write_forms`. `Help_history_and_checkpoints_take_the_history_limit_from_its_constant` passed before the change, because the number was right; the mutation below is its evidence.
- `--filter-method "*Errors"`: total 145, failed 1 (`RgTests.Errors(line: "ls / | rg -l x", …)`, updated to the new error).
- `--filter-method "*Help_names_every_form"`: total 1, failed 1 (CalendarCommandTests, updated to `calendar notifications [--tag <tag>] [vault]`).

#### GREEN

- `--filter-class "*HelpTests"`: total 57, succeeded 57. `*RgTests`: total 55, succeeded 55. `*CalendarCommandTests`: total 62, succeeded 62. `*CheckpointTests`: total 40, succeeded 40. `*LsTests`: total 43, succeeded 43.
- Whole `dotnet test --project tests/Kumbu.Core.Tests`: total 3822, failed 0, succeeded 3752, skipped 70.

#### Mutations (each restored with `git checkout -- <file>`)

1. calendar Usage back to `… | calendar notifications` → `Help_calendar_names_the_vault_of_notifications_and_the_write_forms`: `Assert.StartsWith() Failure`.
2. `, or 'today · 14:00' for a time later today` removed → `Help_calendar_names_today_before_a_time…`: `Not found: "'upcoming', 'ongoing' or 'overdue' with a span, or"…`.
3. `46 of the current week's ISO year` back to `46 of the current year` → same test: `Not found: "--week W that week: 46 of the current week's ISO y"…`.
4. rg `-l` row back to `notes and PDFs with a match` → `Help_rg_names_text_files_in_l_and_c_and_that_l_needs_paths`: `Not found`.
5. rg pipe error back to `-l lists notes, so it needs paths` → `RgTests.Errors(ls / | rg -l x)`: `Assert.Equal() Failure: Strings differ`.
6. trash folder row removed → `Help_trash_names_a_folders_row`: `Not found: "A removed folder (rm takes one that holds nothing)"…`.
7. history `(any file: …)` back to `(an image or a PDF)` → `Help_history_says_any_attachment…`: `Not found`.
8. lscp `Any path but /` changed to `Any vault` → `Help_lscp_…`: `Not found`.
9. ls -l's `the list of vaults stays as it is` removed → `Help_ls_l_…`: `Not found`.
10. mkcp help hard-coded back to `(10 versions per note` and `HistoryLimit = 11` → `Help_history_and_checkpoints_take_the_history_limit_from_its_constant`: `Not found: "(11 versions per note"` (in mkcp's help).
11. The same with history's help hard-coded → the same test: `Not found: "(11 versions per note"` (in history's help).

#### Files changed

- src/Kumbu.Core/Sh/Commands/CalendarCommand.cs (Usage, notifications synopsis, today state, --week)
- src/Kumbu.Core/Sh/Commands/Rg.cs (-l, -c, -m rows; the -l pipe error)
- src/Kumbu.Core/Sh/Commands/Trash.cs (folders in the first line and Summary, the folder row)
- src/Kumbu.Core/Sh/Commands/History.cs (row ending, any attachment, `{NoteWriter.HistoryLimit}`, `{DefaultLimit}`)
- src/Kumbu.Core/Sh/Commands/CheckpointCommands.cs (mkcp `{NoteWriter.HistoryLimit}`, lscp's count line)
- src/Kumbu.Core/Sh/Commands/Ls.cs (-l row)
- tests/Kumbu.Core.Tests/Sh/HelpTests.cs, RgTests.cs (`:157`), CalendarCommandTests.cs (`Help_names_every_form`)

#### Decisions and why

1. **calendar's Usage is `calendar <vault> [--week W|--month M|--from D --to D] [--tag T] | notifications [--tag T] [vault] | set|move|… <note>`** (117 characters). Naming all five write verbs with `<note>` would take 134, past graph's 119 (the Usage-width ruling). The summary beside it in the list already names set, move, close, reopen and unset, and the full forms stay in the synopsis. I kept `T` (the Usage's existing letter) rather than the spec's lowercase `t`, and wrote the forms without the spaces around `|` to fit.
2. **`--week 46`:** "46 of the current week's ISO year" says it in plain words. The year is that of today's ISO week, not the calendar year, so it differs around New Year.
3. **The rg `-l` row says "(not piped)".** It mirrors `-c`'s "(piped: one total)", as the spec asks. `-m` got "text file" too: it applies to text files through the same `AddLines` (same kind of gap, in a file I touched).
4. **trash's Summary** (help list) now names folders as well. That is the same gap one line up, and the help list is built from it.
5. **history's "the last 50"** now comes from `DefaultLimit` (numbers from constants, in a help I touched). I wrote no test for it, because the constant is private. Wording only.
6. **The constant test asserts `(<HistoryLimit> versions per note`** in the helps of `history` and `mkcp`. Those are the two places the number is typed out (the brief's "lscp :17" is `Mkcp`'s help in the same file).

#### Found on the way (commit `fa75e5a`: Say that rm, mv and restore take any file, and correct pdfimages' and write's message lines in the README)

Test first: `Help_rm_and_mv_take_any_file_as_an_attachment` in HelpTests.

- RED: `--filter-method "*Help_rm_and_mv_take_any_file_as_an_attachment"`: total 1, failed 1, `Not found: "Move notes and attachments (any file: an image, a "…`. After rm and mv were fixed, I added the restore assertion, and it failed alone: `Not found: "An attachment (any file) comes back the same way"`.
- GREEN: whole `dotnet test --project tests/Kumbu.Core.Tests`: total 3823, failed 0, succeeded 3753, skipped 70.
- Mutations: rm back to "notes, images or PDFs" → `Not found: "Move notes and attachments (any file: …"`. mv's rename rule removed → `Not found: "an image's, a PDF's or an SVG's new name keeps the"…`. restore back to "An image or a PDF comes back" → `Not found: "An attachment (any file) comes back the same way"`.

What changed:
- **`Rm.cs`:** "Move notes, images or PDFs" became "Move notes and attachments (any file: an image, a PDF, a text file …)". rm removes any non-note path through `AttachmentWriter.RemoveAsync` (`Rm.cs:56-62`, `VaultPath.IsAttachment` = not a note).
- **`Mv.cs`:** "An attachment (an image or a PDF)" became "any file". The rename rule now says what `AttachmentFormat.RenameProblem` (`AttachmentFormat.cs:128-150`) does: an image's, a PDF's or an SVG's new name keeps its format's extension, and no other file's may end in .md or in one of theirs (pinned by `AttachmentFormatTests.cs:110-111`).
- **`Restore.cs`** (touched in Task 10): "An image or a PDF comes back" became "An attachment (any file)". `AttachmentWriter.RestoreAsync` doesn't look at the kind.
- **`Trash.cs`:** already covered by Task 11 (folders in its first line, Summary and the folder row).
- **`README.md:36`:** pdfimages names `.jpg` for a JPEG, and the time limit now reads "stops a long PDF at a page boundary after about 20 seconds, or within a page after 50" (`PdfImages.cs:23,26,344-351`). **`README.md:43`:** the message is kept "except for an SVG or a skill's text file, whose history keeps no message (the write says so)". README only, no test: wording only.

#### Concerns

- calendar's list row writes the write forms as `set|move|… <note>`, relying on the summary to name all five. If the maintainer prefers a longer Usage, the Usage-width ruling would have to change.
- Not changed, the same kind but outside my files: none found in the files I touched beyond those listed.

#### Fix round 1 (review minors), commit `a45090e` Show trash's folder row as it prints, and take calendar's and rg's limits from their constants

1. **trash:** the folder row's example is now the row as `Trash.cs:52` prints it, indented as an example line: `2026-11-19 14:05 UTC  /Demo/Drafts/  folder  (by Alice)` ("A removed folder (rm takes one that holds nothing) has no version or lines:").
2. **rm:** the long second line is rewrapped after "gets a line;". The test asserts that every line of `help rm` is at most 120 characters.
3. **Numbers from constants:**
   - calendar's "at most 366 days" now comes from `MaxRangeDays`. The constant went from private to internal so the test can read it, and calendar's help became `$"""` (it holds no literal braces).
   - rg's help became `$"""` and takes "15 MB" from `AttachmentFormat.MaxBytes / (1024 * 1024)`, "64 MB" from `MaxTextBytes / (1024 * 1024)`, and also "8 PDFs" from `MaxRegionPdfs` (the same kind, in the same help).
   - rg's glob row has a literal `{a,b}`. In an interpolated raw string `{{` doesn't compile (CS9006: a `$"""` literal can't hold `{{`), so it is written `{"{a,b}"}`. The test asserts that `(ripgrep's rules, {a,b} too; repeatable)` prints unchanged. Note for the other tasks: context.md's "double any literal brace" does not hold for raw strings.
4. **semsearch:** the option is now written `-n N, --max N`, and the theory row was updated to that fragment.

Ruling followed: Manual.cs's `tail [-n N|+N]` line stays as it is.

RED: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"`: total 59, failed 3 (`Help_trash_names_a_folders_row`, the semsearch row, `Help_rm_and_mv_take_any_file_as_an_attachment`). `Help_calendar_and_rg_take_their_numbers_from_the_constants` passed before the fix because the numbers were right; the mutations below are its evidence.
GREEN: HelpTests total 59, succeeded 59. Whole `dotnet test --project tests/Kumbu.Core.Tests`: total 3824, failed 0, succeeded 3754, skipped 70.

Mutations (each a hard-coded number plus a scratch change of its constant, restored with `git checkout -- src`):
- `366` hard-coded, `MaxRangeDays = 365` → `Help_calendar_and_rg_take_their_numbers_from_the_constants`: `Not found: "give both, at most 365 days"`.
- `64 MB` hard-coded, `MaxTextBytes = 65 MiB` → same test: `Not found: "up to 15 MB: a CSV, a script), up to 65 MB of them"…`.
- `15 MB` hard-coded, `AttachmentFormat.MaxBytes = 16 MiB` → the same test, and `Help_lists_the_attachments_topic` also failed (`Not found: "larger than 15 MB is kept"`, the attachments topic's own pinned number).
- `8 PDFs` hard-coded, `MaxRegionPdfs = 9` → same test: `Not found: "for up to 9 PDFs a search"`.
- trash row without its time → `Help_trash_names_a_folders_row`: `Not found`.
- rm's line joined again → `Help_rm_and_mv_take_any_file_as_an_attachment` failed (line over 120).
- semsearch `(-n, --max N;` → the semsearch row: `Not found: "-n N, --max N"`.

Files: src/Kumbu.Core/Sh/Commands/Trash.cs, Rm.cs, Semsearch.cs, CalendarCommand.cs, Rg.cs; tests/Kumbu.Core.Tests/Sh/HelpTests.cs.

### Final review wave

### Final fix wave report
Commit 6fd1361. Files: README.md (:26 mv, :27 rm, :426 graph + calendar), Rg.cs:102, GraphCommand.cs:54, WriteTools.cs:19-20; tests: ArchiveHiddenTests (Help_rg_says_archive_covers_notes_text_files_and_pdfs), GraphCommandTests (Help_says_archive_takes_in_notes_and_attachments), McpClientTests (Writes_path_and_content_parameters_...).
RED: 3 new tests failed (Sub-string not found) before the edits.
GREEN: `dotnet test --project tests/Kumbu.Core.Tests`: total 3826, failed 0, succeeded 3756, skipped 70. `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*McpClientTests"`: total 33, failed 33-0 (succeeded 33).
Mutations (each restored via git checkout): old rg text -> ArchiveHiddenTests failed "Not found: its notes, text files and PDFs"; old graph text -> GraphCommandTests failed "Not found: take in the notes and attachments..."; old write descriptions -> McpClientTests failed "Not found: Absolute path of the note, SVG or skill text file". README: wording only, no test.
