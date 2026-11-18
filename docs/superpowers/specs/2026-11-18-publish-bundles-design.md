# Publish 2.0: a note with its linked notes — design

Approved by Chris on 2026-11-18. Size: a project (server, `sh`, web app, reader).

Sources:
- the ideas note: `/SWE/Kumbu/Ideas/Publish 2.0 and export as SPA 2026-11-11.md`;
- the UX review with Chris's answers: `/SWE/Kumbu/Ideas/Publish 2.0 UX review 2026-11-18.md`;
- the graph backend's bundle query: `docs/superpowers/specs/2026-11-16-graph-backend-design.md` §6.7.

## Why

Today a publication is one note: links to other notes become plain text, and sharing a topic means publishing note after note. Sharing a topic usually means sharing a few notes that belong together, such as a project's hub with its pages, a trip with its days, or a garden diary with its plants. Publish 2.0 sends the note together with the notes it links to, up to a depth, behind the same one secret link.

## Goals

- A publication carries its start note and the notes it reaches by outgoing links, to a depth the publisher sets.
- The publisher sees exactly what goes out, and what stays behind and why, before anything goes out.
- The unpublishable mark is a hard blacklist, at every point in the bundle's life.
- The reader can move around the copy as if it were a small site.
- Agents can do the same with `sh publish`.

## Non-goals

- **Export as an SPA or HTML file:** moved to 0.8.0. Decided by Chris on 2026-11-18. His reason: right now it is gold-plating; the feature should first be built "pareto-optimal". It will come together with every file type.
- **A graph in the reader:** not in this version. The architecture keeps room for it (see "Room for a graph").
- **Publishing a folder or a tag as a bundle:** later, if people ask.
- **Bundles across vaults.**

## Anti-goals

- **A note goes out that the publisher didn't see in the list.** That is a failure even if everything else works. Why: honesty is security. Someone who shares with a client must see that `/Work/Salaries.md` is not in it.
- **A note goes out, or stays out, against its unpublishable mark.**
- **The reader learns that a note exists which was left out.**

## Decisions

Each comes with its reason. "Chris" marks his decisions of 2026-11-18; "my call" marks proposals he can still correct.

1. **The publisher sets the depth.** A link and a note embed (`![[Note]]`) both count as an edge, as in the graph backend. Chris: people should choose for themselves. The field runs from 0 (this note only, the default and today's behaviour) to 5. Beside the field is the live count, e.g. "+31 notes". From 50 notes on, the count and a line under it turn red: "That's a lot of notes. Check the list before you publish."
   - The red line is a warning, not a block. Chris wanted the warning; a block would get in the way of people who mean it.
   - Agents use `--depth 0..5`.
   - My call: the cap of 5. Further than that is in practice the whole vault, and the 500-note hard limit (decision 9) holds anyway.
2. **See before it goes out.** The dialog lists the notes the link will carry: title, path, the count and the total size. It lists separately what stays behind, with the reason:
   - "marked unpublishable";
   - "only reached through a note that stays out";
   - "a PDF (not published)";
   - "taken out by you".

   Links never cross a vault (`LinkResolver.cs:60`), so the list needs no "in another vault" reason. Attachments go along exactly as they do for a single note today, and those a single note leaves behind are listed with their reason.

   Nothing is published until that list has been on screen. The publisher can untick single notes. The untick is a one-off exclusion that belongs to this publication, and Update copy keeps it.
   - My call on the unticking: it was recommended in the review and Chris didn't object.
   - **An unticked note blocks the walk** the same way a mark does: notes reached only through it stay out, with the reason "only reached through a note that stays out". My call: one rule for "stays out", and the publisher doesn't have to untick a whole branch note by note.
   - **What was seen is what goes.** The `POST`/`PUT` carries the set of paths the preview showed. If a walk at publish time gives a different set (the vault changed in between), the server publishes nothing and answers `409` with the new list, and the dialog shows it again. My call: this is the first anti-goal, enforced by the server, not just by the dialog. `sh publish add` and `publish update` walk and publish in one step and print the list, so the agent sees exactly what went out; `--dry-run` shows it before.
3. **The unpublishable mark is a hard blacklist.** Chris: "hartes Blacklisting".
   - **The walk:** a marked note, or a note under a marked folder, is never taken, and the walk does not go through it. Notes reached only through it stay out too. Why: the mark says "not public", and the notes behind it were reached because of it.
   - **A mark set later:** marking a note of a bundle unpublishable, moving it into a marked folder or removing it takes it out of the copy at once. The bundle stays. Links to it in the other notes of the copy become plain text from then on. Chris confirmed this ("6. ja, wunderbar"): a mark means "not public, now", not "at the next update", and withdrawing the whole bundle for one note would punish the reader.
   - **Notes behind it leave too:** the notes that the stored edges now reach only through a note that left go out of the copy at once, with it. My call: it follows from Chris's hard blacklist, since a walk now would refuse them. It needs no new walk of the vault, only one over the stored edges.
   - **The start note:** marking or removing it withdraws the whole publication, as today.
   - Before the mark is set, the web app says: "It leaves the copy of *Start note*."
4. **A snapshot, not live.** Chris: "ja, eindeutig", and the unpublishable mark is part of it.
   - **Update copy** walks again, applies the marks and the one-off exclusions, and takes fresh copies.
   - In the web app, when the set of notes changed (new notes reached through new links, or notes gone), it shows the list again before anything goes out.
   - In `sh`, `publish update` prints what changed in the set.
   - New marks never wait for an update (decision 3).
5. **Links inside the copy.** A link to a note in the bundle becomes a link within the copy. It lands at the heading or block: `[[Note#Heading]]`, `[[Note#^id]]`.
   - A link to a note left out becomes plain text with no marker, as today. Chris confirmed this; the reason is the third anti-goal.
   - Note embeds (`![[Note]]`) are shown inline when the embedded note is in the bundle. Otherwise they appear as the note's name, in plain text.
6. **The reader.** The start note opens first. A **Contents** list, with titles in walk order, is one tap away.
   - Back goes back within the copy.
   - A note inside the copy has its own address after the code (`#K7QM-…/3`), so a reload stays on it. The code stays out of the server's logs, as today.
   - **Copy as Markdown** and **Link for your AI** carry the whole bundle: the start note first, each note under its own heading, and the links between them as anchors. Brita pastes one link into Grok, and Veyalt reads all of it.
7. **The bar above the notes.** Every note of a bundle carries the bar "Published with *Start note*". It turns yellow when the copy is older than the note.
   - A note in several bundles names them all.
   - Members of a shared vault see "Published by the vault's owner".
8. **Who may publish:** unchanged. Only the vault's owner publishes; members never do. Agents need "May publish".
9. **Limits.** At most 500 notes. My call: at 500 notes the list itself stops being something a person reads.
   - The 64 MB of `Publications.CopyLimit` count for the whole bundle, images included.
   - Over either limit the dialog says which, and nothing is published until the depth is lowered or notes are unticked.
10. **The activity** records "Published *Start note* with 12 notes" and "Updated *Start note*: +2, −1". Decided in the review (Chris: "5. ja!!"); history is an audit log.

## Room for a graph

Chris, 2026-11-18: the architecture has to keep in mind that Publish will have a graph in the end.

- The publication keeps the **edges between its notes**, as the bundle query returns them: indices into the list of notes, with each link's heading or block, in the shape `/api/graph` uses.
- The reader API returns the contents with the notes in walk order and each note's depth from the start note. The edges are stored but not served in this version.
- A later graph view in the reader then needs no new walk, and no change to how a publication is stored.
- The edges follow the snapshot rule and the hard blacklist: an edge to a note taken out goes with it.

## The flows

**Web app.** Open the note's ⋯ menu and choose **Publish…**. The dialog shows:
- the depth with its count;
- the list of what goes and what stays, with checkboxes;
- the red line from 50 notes on;
- the button **Publish N notes**.

Once published, the dialog shows the link and Copy, as today. Below that are **Update copy** and **Unpublish**.

**`sh` and MCP.**
- `publish add <note> [--depth N] [--without <path>…]` prints the notes taken and those left behind, with reasons, then the link.
- `publish ls` shows the bundles with their note counts.
- `publish update <note>` prints what changed in the set.
- `publish rm` withdraws, as today.
- An agent has no screen between the preview and the publish. My call: `publish add` publishes and prints the list, and `--dry-run` prints the list without publishing. The tool description tells the agent to show the list to the person when the bundle has more than the start note.

**API.**
- `GET /api/publications/{vault}/{path}/preview?depth=N&without=…` returns what would go and what would stay, with reasons, counts and the size. It writes nothing.
- `POST` and `PUT` take `depth` and `without`.
- The reader's `POST` with the code returns the contents and the start note, and the notes come one by one after that. My call; the builder may choose to send everything at once below a size.
- Every input is validated at the boundary: depth 0 to 5, `without` as vault paths of notes in the bundle, everything else a `400`.

## What stays as it is

- The fair copy for every note: frontmatter, tags, comments and raw HTML are taken out.
- One secret link per publication, the reader cookie and the AI links.
- Withdraw on unpublishing the start note.
- Single-note publications are bundles of depth 0. One rule over exceptions: there is no second kind of publication.

## Tests (written first)

**xUnit:**
- the walk with the hard blacklist: a marked note, a marked folder, notes reachable only through a marked note, cycles, depth 0 to 5, never across a vault;
- the preview equals what gets published;
- one-off exclusions kept across an update;
- an update with a changed set;
- a mark, a removal or a move into a marked folder taking a note out at once, with the links to it turning into plain text in the stored copies;
- marking the start note withdrawing everything;
- the 500 and 64 MB limits;
- links and embeds to headings and blocks inside the copy;
- the activity entries;
- members and agents without "May publish" refused;
- the edges stored and pruned along with the notes;
- existing single-note publications reading unchanged (migration).

**Vitest:**
- the dialog: depth, count, red line, unticking, list again on update;
- the bar on bundle notes;
- the reader's contents and navigation inside the copy.

## README and docs

- README: the publish section with depth, the list, the hard blacklist and the limits.
- `docs/security-architecture.md`: the bundle's guarantees (the blacklist, never across a vault, the fair copy for each note).
- `help publish` and the `sh` description.

## Left to the builder

Answered while building (2026-11-18):

- **The record and the copy:** the publication entry gains `Depth`, `Without` (stored paths) and `Members` (id, path, version per note but the start). The copy stays one sealed blob: the start note's `Title` and `Markdown` as before, plus `Notes` (id, title, Markdown, depth, the copy's files each note's `f/n` names) and `Edges` ([from, to] ids). Images are numbered across the copy and copied once.
- **Links to a note taken out** are rewritten in the stored copy at once (`FairCopies.Relink`, which skips code), in the same transaction as the mark, the move or the removal; the files only that note used are deleted. Such a link becomes its text with only the brackets unescaped that the copy escaped, so the text reads as it did.
- **Numbering without gaps:** notes and files are numbered from 0 without gaps, when the copy is made and after every prune: what stays is numbered again, notes in walk order, files in their order (each file is sealed again under its new index), links, edges, each note's files and the members' ids rewritten. Why: the third anti-goal. Ids `[0, 2, 3]` or files `[0, 2]` would tell the reader that a note left. A reader with `#/3` bookmarked may land on another note after a prune; the start note stays 0.
- **A prune walks again** over the stored edges, to the publication's depth: depths are the new walk's, and a note now further than the depth leaves, as a fresh walk would leave it behind.
- **Over HTTP `expected` is required** when the depth is above 0, or the publication already goes past depth 0 (`PUT`): `400` without it. Why: the first anti-goal, enforced by the server for every client, not only the web app's dialog. `sh` keeps its own rule (decision 2): it prints the list in the same answer.
- **`without` names notes of the walk:** over HTTP each path must be a note the walk to that depth takes with nothing left out, never the start note; anything else is a `400`. Notes the publication already left out stay as they are across updates, even once the walk no longer reaches them.
- **Embeds and links within a note:** `![[Note#Heading]]` shows that section in place, `![[Note#^id]]` that block's paragraph; one the note doesn't have is a link to it. `[[#Heading]]` links to the heading within the copy.
- **The vault's list** names the start note of each bundled note for the owner only; a member gets each bundled path once, without a start, as she learns only "Published by the vault's owner" (decision 7), and the start would tell her which note links to it.
- **Migration:** none. The new fields are optional; a copy or entry without them is a single note, and a walk that takes only the start note stores exactly what was stored before.
- **The reader** gets every note at once with the copy (texts are small; images still come one by one). A link to a heading scrolls to it; a link to a block lands at the top of its note.
- **The preview** is `GET /api/publication-previews/{vault}/{path}`; `PUT` takes an optional JSON body (none keeps the publication's walk).
- **Update copy** of a bundle in the web app always shows the list first (not only when the set changed): one flow, and the owner sees what goes out each time.
- **Who prunes:** whoever's batch removes a note of a copy or moves it under a mark (a member's, a share's): it only reduces what is out. Only the start note keeps the owner-only rule.
- **A mark on an image** of a bundle withdraws the whole copy, as it does for a single note today, also when it is a marked folder that covers the image; pruning by image is left for later. A deliberate safe choice: the mark wins at once, and the owner publishes again without the image.
