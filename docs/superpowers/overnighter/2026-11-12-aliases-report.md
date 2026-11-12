# Overnighter report: `aliases` in the front matter

Date: 2026-11-12. Branch `feature/aliases`, pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/196. Spec `docs/superpowers/specs/2026-11-12-aliases-design.md`, brief `docs/superpowers/overnighter/2026-11-12-aliases-brief.md`, plan `docs/superpowers/plans/2026-11-12-aliases.md`. Not merged, not tagged, version unchanged.

## Summary

`[[Kumbu]]` and `[[K²#Roadmap|roadmap]]` now lead to a note whose front matter says `aliases: [Kumbu, K²]`, through the one `LinkResolver`, so `links`, `backlinks`, the REST answer the web app draws links from, embeds in the web app and in `cat`, block references, `rm`'s count, `replace`'s warnings, `mv`, `archive` and `restore` agree. A note's name or an attachment's always beats an alias; two notes sharing an alias make the link ambiguous. `links` marks such a link ` (alias)`. Unlinked mentions look for the title and every alias. Creating a note that takes a link away from an alias says so in `write`'s answer. The web app, the API and publications needed no change.

Along the way, `Frontmatter.Read` (also used by boards, tags and skills) was hardened: on `master` a self-referencing YAML anchor (`a: &a [*a]`) crashes the server process with a stack overflow, anchor expansion stalls it for seconds to minutes, and keys that read alike throw `ArgumentException`. All three now give "no values" with a problem text.

## Environment

- Cloud container, 4 cores, 15 GB RAM, Linux 6.18. .NET SDK 10.0.403 (global.json 10.0.100, roll forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- Installed by hand: Node 26.13.1 into `/opt/node26` (the image had Node 22 and no `/opt/node26`; checksum verified against nodejs.org's SHASUMS256), pnpm 10.32.1 (`npm install -g`). `dockerd` was not running and was started by hand; `mongo:7.0` pulled. Tokenizer fetched and checked as the brief says (`KUMBU_TOKENIZER`). `TESTCONTAINERS_RYUK_DISABLED=true`; the test MongoDB as the brief's `kumbu-test-mongo`, fresh before each server group.
- Superpowers skills were available. The plan was written with `writing-plans`. Work was subagent-driven: a fresh implementer subagent per task (Tasks 1 to 8); implementers never committed, pushed or switched branches; I committed and pushed after each task. **Deviation:** the per-task spec-compliance and code-quality reviews were done by me on each task's diff, not by separate reviewer subagents; to make up for it, a fresh whole-branch reviewer read the finished diff against spec and brief (below), and a fresh security reviewer as the brief asks. A small security fix and the whole-branch fix I implemented myself with the same red/green/mutation steps.
- Task 0 took about 35 minutes of wall time (the server groups alone take about 20), started in the background while I read the code; nothing was off.

## Plan review

A fresh subagent reviewed the plan with the spec and the brief. Its twelve findings, all folded into the plan before the tasks they concern (commit `e60525e`):

1. Task 5's archive test expected `[[Kumbukumbu]]`; `archive` stamps the name, so the name form is `[[Kumbukumbu 2026-11-12 100000]]`. Fixed.
2. Task 3's archive test: the folder is `_archive`, and from `/W/_archive/x.md` the same-folder rule picks one; the source moved to `/W/_archive/Sub/x.md`. Fixed.
3. Task 7's side-by-side test couldn't be built as written; rebuilt on creating `/Work/Ideas/Kumbu.md` with `aliases: [K²]`. Fixed.
4. Task 5's red step named the wrong failing tests (without aliases in `before`, alias links are skipped); corrected, and noted that the first test passes before for the wrong reason.
5. Task 5 named fields that don't exist (`moved[p].Content`); corrected to `.Then` / `.Item2`.
6. Task 1 kept only `catch (YamlException)`; a catch-all was added in `NoteAliases` (the deep-nesting concern was measured: YamlDotNet itself reports it as `YamlException` up to 100,000 levels).
7. Task 7 mutation (a) can't fail; recorded as equivalent.
8. Task 4's cat test and mutation (e) couldn't fail; replaced by a positive embed test.
9. "Look the name up" was ambiguous; now "look up `name`".
10. Several expectations were prose; replaced by exact strings.
11. Spec §6 names `Manual.cs` but `help links` prints `LinksCommand.Help`; stated as a deliberate deviation.
12. Costs and side effects (mentions merge within one title, `OfAsync` reading the vault, Task 1's limits on boards and skills): mentions now merge only across terms; the rest recorded in the plan and here.

## Task 0: baseline, planned against measured

Planned (brief): core + server 5,966 total, with the tokenizer only the 5 to 7 model/TensorX skips; web 141 files, 2,503 tests; build green.

Measured on `91a9a9c` (Release, as CI, with `KUMBU_TOKENIZER`), 0 failed everywhere:

| Group | Total | Skipped |
|---|---|---|
| Core | 2,390 | 3 |
| Server Api, Mcp, Auth, Net | 847 | 0 |
| Server Accounts | 413 | 0 |
| Server Sharing | 353 | 0 |
| Server Welcome | 20 | 0 |
| Server Vaults, Skills, Search, Metrics | 780 | 6 |
| Server, the rest | 1,163 | 1 |
| **Core + server** | **5,966** | **10** |
| Web | 141 files, 2,503 tests | — |

`pnpm -C web build` green (the known chunk-size warning). The 10 skips: 3 core and 7 server, the model and TensorX tests (running as root).

## Tasks

Each task: red test, green, then every mutation alone (break, watch it fail, restore; the restore was from a saved copy checked with `cmp`, since `git checkout` would have dropped the uncommitted work). Test counts are from `dotnet test` (Debug, one class with `--filter-class`).

### Task 1: front matter that can't hurt a request — `f02dcc7`

`Frontmatter.Read` converts under a budget (10,000 values, depth 64) and reports keys that read alike as a problem.

- Red: the full class run was killed for lack of memory (exit 137: eight levels of tenfold expansion build about 10⁹ values); alone, the key tests failed with `ArgumentException` (3 total, 2 failed).
- Green: `FrontmatterTests` 22/0; `*Board*` 116/0, `*Tag*` 92/0, `*Skill*` 145/0.
- Mutations: (a) `MaxValues = int.MaxValue` → `Anchor_expansion_is_a_problem_and_fast` fails (`OutOfMemoryException`, run under a 1 GB heap limit); (b) depth check removed → no plan test failed (the value budget also stops a cycle), so I added `A_chain_of_anchors_deeper_than_the_limit_is_a_problem` (100 anchors, about 5,000 values): 22 total, 1 failed with the mutation; (c) `TryAdd` back to indexing → both key cases fail (21/2 before the added test).

### Task 2: a note's aliases — `09b4c94`

`NoteAliases.Of(string)` and `Of(IEnumerable<Note>)` (spec §2), on top of `Frontmatter.Read`, with a cheap pre-filter (front matter containing `aliases`).

- Red: build fails, `NoteAliases` doesn't exist. Green: 16/0; `*Links*` 176/0.
- Mutations: (a) `^` allowed → the forbidden-characters case fails (16/1); (b) case-sensitive dedupe → duplicates case fails (16/1); (c) no NFC → `An_alias_is_normalised_to_nfc` fails (16/1); (d) pre-filter removed → 16/0, equivalent as planned (cost only).

### Task 3: the resolver's alias step — `a4dd8ba`

`LinkResolution.ViaAlias`, an optional `noteAliases` constructor argument, the alias step after the note lookup and before the "looks like a file" fallback, sharing `Choose` (archive and same-folder rules) with `Find`.

- Red: build fails (no 4-argument constructor, no `ViaAlias`). Green: `AliasResolutionTests` 39/0; `LinkResolverTests` 47/0, `AttachmentLinkTests` 26/0, `*Links*` 215/0, `LinkCommandsTests` 66/0, `NoteMoverTests` 22/0.
- Without aliases, a dump of 946 resolutions (11 sources × 43 targets, with and without attachments; kind, target, candidates) was byte-identical before and after.
- Mutations (39 total each): (a) alias step before names → 2 failed (`A_note_name_beats_an_alias`, `A_name_ambiguity_stays_one`); (b) no `.md` condition → `Kumbu.md` case; (c) no `/` condition → `Projects/Kumbu` case; (d) no archive preference → `A_living_note_beats_an_archived_one_by_alias`; (e) no same-folder rule → `The_source_folder_settles_a_shared_alias`; (f) aliases not filtered to the given notes → `Aliases_of_notes_not_given_or_in_another_vault_do_not_count`; (g) alias step after the file fallback → `An_alias_that_looks_like_a_file_name_resolves`. Each 1 failed unless said.

### Task 4: `links`, `backlinks`, REST, `rm`, `replace`, docs — `1222541`

`NoteLinks.OfAsync` reads the vault's notes (it listed paths), `ToAsync`, `links <dir>`, `rm` and `replace` pass aliases; `links` appends ` (alias)` (after `#^id` too), `LinksCommand.Describe` (used by `cat`) unchanged; `help links` and the README got their sentence.

- Red: 10/10 failed. Green: 10/0; `*LinkCommandsTests` 76/0, `CatEmbedsTests` 25/0, `ReplacerTests` 27/0, `NoteWriterTests` 53/0, `HelpTests` 25/0.
- Mutations: (a) `OfAsync` without aliases → 5 failed (links, block link, `NoteLinks` direct, ambiguity, cat embed; covers planned (e)); (b) `ToAsync` → backlinks test; (c) `links <dir>` → directory test; (d) no ` (alias)` → 2 failed; (f) `rm` → `Rm_counts_links_by_alias`; (g) `replace` → `Replace_preview_sees_links_by_alias`.

### Task 5: `mv`, `archive` and `restore` keep alias links — `0fd5c06`

`NoteMover.Rewrites` gives `before` the notes' aliases and `after` the same keyed through `map`; `Checkpoints` gives `before` the current aliases and `after` those of the content each note will have after the restore (`changed`, `back`, `moved`).

- Red: 7 total, 4 failed (rename onto an alias, archive, restore report, the added restore test); 3 passed for the wrong reason (alias links were skipped as "not found", not kept). In the archive test the link silently moved to the other note sharing the alias: the spec's anti-goal, now fixed.
- Green: 7/0; `NoteMoverTests` 22/0, `FolderMoveTests` 17/0, `ArchiveCommandTests` 33/0, `CheckpointTests` 40/0, `CheckpointAttachmentTests` 8/0.
- Mutations: (a) `after` without aliases → 4 failed; (b) `after` keyed by old paths → 3 failed; (c) `before` without aliases → 2 failed; (d) `Checkpoints`' `before` without aliases → 1 failed; (e) `Checkpoints`' `after` from current content → 2 failed (the plan's test for this couldn't work: if the current note merely lost its aliases, `before` never resolves the link; the added test has a new note catch `[[Kumbu]]` by name, going to the trash in the restore, while the restored aliases keep the link).

### Task 6: unlinked mentions by alias — `095b2fb`

`Mentions.In(content, terms)`: minimum length per term, marks merged across terms only (longest term wins an overlap), one term's marks exactly as before; `MentionsAsync` uses the title and the note's aliases and no longer returns early on a short title.

- Red: build fails (no overload); the two shell tests fail (12/2). Green: `MentionsTests` 30/0, `AliasLinkCommandsTests` 12/0, `*LinkCommandsTests` 78/0.
- Mutations: (a) early return kept → `A_short_title_still_finds_its_aliases`; (b) first term wins → `Overlapping_marks_keep_the_longest`; (c) title only → 2 failed; (d) minimum on the first term only → `The_minimum_length_counts_per_term`.

### Task 7: the creation hint — `ada1332`

`NewlyAmbiguousAsync` renamed `LinkHintsAsync`; `before` leaves out the created note and its aliases; links that led to another note by alias and now lead to the new one by name form their own `; note: …` clause, capped at 5. The existing hint text is unchanged.

- Red: 57 total, 4 failed. Green: 57/0; `WriteCommandsTests` 19/0.
- Mutations: (a) `before` keeps the created note's aliases → 57/0, equivalent (aliases count only for given notes); (b) no cap → `TheAliasHintListsFiveAndCountsTheRest`; (c) no `!ViaAlias` on `after` → 57/0, equivalent (the other note still has the alias, so the answer would be ambiguous, not one note); (d) clauses merged → 3 failed.

### Task 8: a published note's fair copy is unchanged — `3f29b12`

`AliasPublishTests.An_alias_link_in_a_published_note_stays_plain_text`: Alice publishes the spec's diary; the fair copy's Markdown is exactly `"Talked with Bob about Kumbu and the roadmap.\nKumbu needs a calendar.\n"`, no files, and neither the copy nor `publish add`'s answer contains "Projects" or "Kumbukumbu". Pass 1/0. Mutation: `FairCopies.WikiText` passing the link through as written → 1/1 failed (a first mutation, keeping the raw text but marking it handled, still passed: a later pass also turns leftover `[[…]]` into text, so there are two places that do it).

### Fixes from the reviews — `e29079e`, `53e6f5e`

See the security review and the whole-branch review below. Each with a red test, green, and mutations:

- `e29079e`: `NoteAliasesTests` 19/0, `AliasResolutionTests` 41/0 (red: 2 failed, the indexing test taking 12.9 s); core 2,483/0. Mutations, each 1 failed: list check back in the index → `Many_notes_sharing_many_aliases_index_in_linear_time`; attachment-format check removed → `An_alias_never_takes_a_name_of_a_kind_the_vault_keeps_as_attachments`; no 100 cap → `Only_the_first_hundred_aliases_count`; no 64 KiB cap → `Front_matter_too_long_to_be_names_has_no_aliases`; no cancellation check → `Of_notes_stops_when_cancelled`.
- `53e6f5e`: red `A_markdown_link_never_resolves_by_alias` (13/1, `[talk](Kumbu)` led to the note); green core 2,484/0; mutation (no `wikilink` condition) → 13/1.

## Final gate

On `53e6f5e` (the last code commit), Release as CI, with `KUMBU_TOKENIZER`, a fresh test MongoDB per server group. 0 failed everywhere, no flaky test met:

| Group | Total | Skipped | Baseline |
|---|---|---|---|
| Core | 2,484 | 3 | 2,390 |
| Server Api, Mcp, Auth, Net | 847 | 0 | 847 |
| Server Accounts | 413 | 0 | 413 |
| Server Sharing | 353 | 0 | 353 |
| Server Welcome | 20 | 0 | 20 |
| Server Vaults, Skills, Search, Metrics | 780 | 6 | 780 |
| Server, the rest | 1,164 | 1 | 1,163 (+1: `AliasPublishTests`) |
| **Core + server** | **6,061** | **10** | 5,966 |
| Web | 141 files, 2,503 tests | — | the same |

`dotnet build --configuration Release`: 0 warnings, 0 errors. `pnpm -C web build` green (known chunk-size warning). WebDAV (`litmus`) and the PDF sidecar are untouched and left to CI on the pull request.

## Security review

A fresh subagent got spec §1 (anti-goals, security boundary) and §3.3 and the diff, not my reasoning. Its findings:

- **Confirmed, fixed (`e29079e`): quadratic alias index.** The resolver checked each alias's list before adding (`List.Contains`), O(A·N²) for N notes sharing A aliases; a Write member of a shared vault could plant notes that make every note view cost minutes of CPU, uncancellable. Fixed: no list check (`NoteAliases` already lists each alias of a note once), at most 100 aliases per note, front matter over 64 KiB not read for aliases, `NoteAliases.Of(notes, ct)` honours cancellation.
- **Confirmed, fixed by the same cap: unlinked mentions** looked for every alias in every line of the vault (10,000 aliases × vault size).
- **Confirmed, fixed (`e29079e`): an alias could take a link from an attachment's name** where the resolver has no attachment list (`rm`, `replace`, `write`'s hint): `[[shot.png]]` with a real `shot.png` resolved to a note with `aliases: [shot.png]`, so `rm` said a link "now leads nowhere" falsely. Now a name of a kind the vault keeps as attachments never resolves by alias.
- **Plausible, recorded:** the `aliases` pre-filter is passed by any front matter mentioning the word, and YamlDotNet builds the whole document before the budget stops it; with the 64 KiB cap a note costs at most one parse of 64 KiB per resolver. `Frontmatter`'s other callers (board, skills, tags) still rely on `YamlException` for unknown failures; their unknown cases are now the budget's.
- **Not an issue:** the vault and permission boundary (every resolver is built from the same vault's notes; `EncryptedNoteStore` and `CheckpointView` serve the same note set for list and read, so switching `OfAsync` to reading neither adds nor drops notes; roles are vault-wide), publications (unchanged, get no aliases), ordering, hint texts and logs.

## Whole-branch review

A fresh subagent reviewed `f23d4ae..HEAD` against spec and brief:

- **Fixed (`53e6f5e`): Markdown links resolved by alias** (`[talk](Kumbu)`), against spec §3.1. `LinkResolver.Resolve(source, NoteLink)` now tries aliases only for a `WikiLink`; every caller that has the link passes it.
- **Fixed:** `NoteAliases` skips content not starting with `---` before splitting it into lines; the pointless `StackOverflowException` in the exception filter is gone and the catch-all's comment says what it guards.
- **Recorded:** every note view (`NoteLinks.OfAsync`) now reads the vault's notes, decrypting them in an encrypted store, where it listed paths (brief point 1 allows it; "Linked from" already did the same). `NoteMover.Rewrites` has no cancellation token to pass. The resolver relies on its callers listing each alias of a note once, which `NoteAliases` does.
- Everything else checked out (spec §2 to §4, brief points 2 to 8, nothing changed where no alias is involved).

## Decisions made without Chris

1. **Where aliases come from (point 1).** Options: read the notes in each caller; an alias source the resolver calls; a cache. Chose reading, since every caller but `OfAsync` and `replace` already read the notes, and a cache was ruled out. `OfAsync` and `replace` switched from listing to reading.
2. **Resolver shape (point 2).** An optional `noteAliases` constructor argument and `LinkResolution.ViaAlias` (true for one note or an ambiguity among aliased notes); `LinkKind` unchanged. Later a `Resolve(source, NoteLink)` overload, so a Markdown link never tries aliases (the plain `Resolve(source, string)` means a wikilink).
3. **Naming (point 3).** `NoteAliases`, `noteAliases`, `_byAlias`; the display text `WikiLink.Alias` untouched.
4. **`Frontmatter.Read` hardened rather than worked around.** Options: a separate YAML reader for aliases; guards only in `NoteAliases`; harden `Read`. Chose hardening `Read` (10,000 values, depth 64, keys that read alike), since the crash and the stall exist on `master` for boards, tags and skills too, and a second YAML reader would go against "the existing YAML reader". Limits no real front matter comes near.
5. **Limits on aliases (security review).** At most 100 per note (the first 100), front matter over 64 KiB not read for aliases. Options: no limit; a per-vault limit; per note. Per note is simple and predictable; Obsidian notes carry a handful. Not in the spec; chosen under rule 3.
6. **A name with an attachment extension never resolves by alias.** Options: pass attachments to `rm`, `replace` and `write`'s hint; refuse aliases with such extensions in `NoteAliases`; skip the alias step for such names. Chose the last: one place, and it keeps any alias like `v1.0` working (spec's example of a non-text scalar).
7. **Mentions merge.** Across terms only, longest term first; one term's overlapping marks stay as today (rule 1).
8. **The creation hint's method** renamed `NewlyAmbiguousAsync` → `LinkHintsAsync`, since it now gives two kinds of hint.
9. **`restore` moving notes back:** `NoteMover.Rewrites` takes aliases from the current content of a note moved back, since it receives only the current notes; the checkpoint's own resolver uses the restored content. Allowed by point 7 ("where it doesn't, keep it … and record that"); it matters only if a moved-back note's aliases changed since the checkpoint.
10. **`help links`** got the sentence (spec §6 names `Manual.cs`, but `help links` prints `LinksCommand.Help`; the manual's one-line summary stays).

## Deviations, surprises and open points for Chris

- **Surprise, on `master`:** `Frontmatter.Read` can be crashed (stack overflow kills the process) by `a: &a [*a]` in a note's front matter, stalled by anchor expansion, and made to throw by keys like `[a]: 1` / `[b]: 2`. `TagReader` calls it for any note with front matter, so this is reachable today by anyone who can write a note; fixed here in Task 1. Worth a look whether 0.4.x needs the same fix.
- **Spec §3.3's reader page** holds trivially (brief point 5): the publication resolver only finds attachments for embeds, fair copies turn note wikilinks into text, and an alias never resolves to an attachment; `Publications.cs` is untouched and gets no aliases, so a publication can't leak them. Should publications ever resolve note links, aliases must come only from the published notes.
- **Cost:** every note view reads (and decrypts) the vault's notes for the alias index, as "Linked from" already did; a note with a short title and no aliases now reads the vault before finding no mentions.
- **Process deviation:** per-task reviews were mine, not separate reviewer subagents (see Environment).
- **Left out, in `open-items.md`:** the creation hint for upload and the web app's write; `rm`'s wording ("now lead nowhere" can be untrue; proposal "now lead to X by its alias"); aliases in the web app's link completion.
- The Mermaid spec's line on aliases is superseded, as this spec says; the file was left alone.

## Hand-test checklist

On staging (or a local server), vault `Work`, as Alice:

1. Create `/Work/Projects/Kumbukumbu.md`: `---` / `aliases: [Kumbu, K²]` / `---` / `# Kumbukumbu` / `## Roadmap` / `Text`. Create `/Work/Diary/2026-11-12.md`: `Talked with Bob about [[Kumbu]] and the [[K²#Roadmap|roadmap]].` / `Kumbu needs a calendar.`
2. Web app, open the diary: both links are drawn as links (not "not found"). Tap `Kumbu`: Kumbukumbu opens. Back, tap `roadmap`: Kumbukumbu opens at Roadmap.
3. Open Kumbukumbu: "Linked from" lists the diary (line 1). Open "Unlinked mentions": it lists the diary's line 2 with "Kumbu" marked, and not line 1.
4. Repeat 2 and 3 at a phone width of 375 px: links tappable, sections readable, no horizontal scroll.
5. `sh`: `links /Work/Diary/2026-11-12.md` prints `1: [[Kumbu]] → /Work/Projects/Kumbukumbu.md (alias)` and `1: [[K²#Roadmap|roadmap]] → /Work/Projects/Kumbukumbu.md (alias)`.
6. `backlinks /Work/Projects/Kumbukumbu.md` lists `/Work/Diary/2026-11-12.md:1:…`; `backlinks --unlinked /Work/Projects/Kumbukumbu.md` gives `/Work/Diary/2026-11-12.md:2:Kumbu needs a calendar.`
7. `cat` a note containing `![[Kumbu]]`: the embedded-notes block shows Kumbukumbu's text.
8. `mv /Work/Projects/Kumbukumbu.md /Work/Done/Kumbukumbu.md`: `cat` the diary, it is unchanged; `links` on it leads to `/Work/Done/Kumbukumbu.md (alias)` twice. Move it back.
9. As Bob (member with Write), `write /Work/Ideas/Kumbu.md` with any text: the answer contains `; note: [[Kumbu]] in /Work/Diary/2026-11-12.md now leads to /Work/Ideas/Kumbu.md (before: /Work/Projects/Kumbukumbu.md by its alias)`. In the web app, `Kumbu` in the diary now opens Bob's note. `rm` Bob's note again.
10. As Carol, write `/Work/People/Kumbu Team.md` with `aliases: [K²]`: the answer says `now ambiguous: [[K²#Roadmap|roadmap]] in /Work/Diary/2026-11-12.md (2 notes)`; `links` on the diary says `ambiguous: /Work/People/Kumbu Team.md, /Work/Projects/Kumbukumbu.md`; the web app shows `roadmap` as ambiguous.
11. Publish the diary (`publish add /Work/Diary/2026-11-12.md`) and open the reader link: the text reads "Talked with Bob about Kumbu and the roadmap." with no link and nothing of Kumbukumbu; check at 375 px too.
12. A Markdown link `[talk](Kumbu)` in any note: `links` says `not found`.
13. Robustness: a note with front matter `a: &a [*a]` / `aliases: [x]` saves and opens; links elsewhere in the vault keep working; the server stays up.

## Pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/196
