# Welcome Vault Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every user who registers gets a vault "Welcome" whose seven notes explain Kumbu², how to work by hand, with an AI and with Obsidian, how to connect an AI, the security and privacy concept, and what happens to their data.

**Architecture:** The seven notes are Markdown files embedded in the server assembly. `POST /api/join` (the only way an account comes into being, also for the first admin through `bootstrap-admin`'s link) creates the vault with the new user's key right after the session starts, fills in `{{server}}`, and writes the notes as the seed writer. A failure is logged and never fails the join.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB, xUnit with `KumbuAppFactory`.

**Spec:** `docs/superpowers/specs/2026-10-26-demo-round-design.md` §3 (binding), with the features it describes from `docs/superpowers/specs/2026-10-26-obsidian-sync-design.md` and `docs/superpowers/specs/2026-10-25-oauth-connectors-design.md`.

## Where and how this runs

- **Builds on the OAuth work, merged into `master` as `ed08aa0` (PR #28):** `VaultCatalog.CreateForUserAsync` takes `DateTime? credentialReadAt = null`, connections and the "Access" page exist, and the texts describe them. Symbols are named rather than line numbers.
- One branch `welcome-vault` from `master`, one pull request. Chris merges it himself.
- Commands: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~<Class>"`; the whole suite with `dotnet test`.
- Commit messages imperative and free-form (`Give new users a welcome vault`), each ending with the session's attribution lines.

## Global Constraints

- Vault name: **`Welcome`**. Seven notes at its root, exactly these names: `Start here.md` and the six it links to, `Working by hand.md`, `Your AI in Kumbu².md`, `Obsidian.md`, `Connect your AI.md`, `Security and privacy.md`, `Your data.md` (the spec's §3 list).
- Only accounts made through `POST /api/join` get it; nothing for existing users, nothing for users created directly through `UserCatalog` (tests, tools). Existing welcome vaults are never touched again.
- Writer label of every welcome note: `Writers.Seed` (`seed`), so it reads as the server's in the history and in the writer kinds of the Obsidian design §5a.
- Placeholder: `{{server}}` is replaced by `Kumbu:PublicUrl` without a trailing slash; without a public URL by `https://<your Kumbu² address>`. No other placeholder exists; a `{{` left in a written note is a bug.
- Texts: English, British spelling, **AI-agnostic** ("your AI", "your agent") everywhere except the per-client sections of `Connect your AI.md`; prosumer level (no jargon left unexplained, no hand-holding); nothing about any organisation or deployer; every `[[link]]` resolves inside the vault.
- A client is marked *tested* only when tested end to end: **Claude Code: tested.** claude.ai (web, Desktop, mobile): "should work, not tested yet" until the dry run on 2026-10-26 confirms it (Task 4). Any other MCP client: "should work, not tested yet".
- Texts describe the v0.2.0 feature set. A feature cut from v0.2.0 (drop order: search in earlier versions, the deleted-ids list, Activity) is removed from the texts before the release (Task 4).
- Creating the welcome vault never fails the join: the account exists and the recovery key must reach the user.

## Review Focus

1. **Creating the vault fails mid-join** (MongoDB fault, the user deactivated between sign-in and creation, a credential change): the join still answers `200` with the recovery key and cookie, the server logs a warning without content or names, and no half-filled vault is left that later looks complete. Test in Task 1 (`A_failing_welcome_vault_never_fails_the_join`).
2. **A user who deletes or renames "Welcome"** gets nothing back on a later sign-in: only the join creates it, so no code path re-creates it, and a freed name stays free for the user's own vault. Test in Task 1 (`Signing_in_again_never_recreates_the_welcome_vault`).
3. **Without `Kumbu:PublicUrl`** (a local or development setup) the texts read correctly, with the placeholder address and the sentence pointing at the browser's address bar. Test in Task 1 (`Without_a_public_url_the_address_is_a_readable_placeholder`).
4. **Links and names that only work in the web app:** the welcome vault opened in Obsidian (after export or sync) must work too: wikilinks by note name, no Markdown links to `/notes/...` URLs, the `²` in a file name. Test in Task 2 (`Every_link_resolves_and_no_link_is_a_web_url`).
5. **Texts drifting from the product**: a menu name or path in a text that the web app doesn't have. Checked by hand against the running app in Task 4, and pinned for the stable names by Task 2's test of the menu strings it quotes.

---

## File structure

- Create `src/Kumbu.Server/Welcome/WelcomeVault.cs`: the service that reads the embedded notes, fills the placeholder and writes the vault.
- Create `src/Kumbu.Server/Welcome/Notes/*.md`: the seven texts (embedded).
- Modify `src/Kumbu.Server/Kumbu.Server.csproj`: embed `Welcome/Notes/*.md`.
- Modify `src/Kumbu.Server/Program.cs`: register `WelcomeVault` as a singleton.
- Modify `src/Kumbu.Server/Api/AccountApi.cs` (`JoinAsync`): call it after the session started.
- Create `tests/Kumbu.Server.Tests/Welcome/WelcomeVaultTests.cs` and `tests/Kumbu.Server.Tests/Welcome/WelcomeTextTests.cs`.
- Modify tests that list a joined user's vaults (`tests/Kumbu.Server.Tests/Api/AccountApiTests.cs`, `JoinedWithKeysAsync` and whatever else the full suite shows).
- Modify `README.md` (one paragraph) and `docs/security-architecture.md` (one row or sentence, if it lists what a new account holds).

### Task 1: The welcome vault on joining

**Files:**
- Create: `src/Kumbu.Server/Welcome/WelcomeVault.cs`, `src/Kumbu.Server/Welcome/Notes/Start here.md` (a one-line stub for now; Task 2 writes the texts)
- Modify: `src/Kumbu.Server/Kumbu.Server.csproj`, `src/Kumbu.Server/Program.cs`, `src/Kumbu.Server/Api/AccountApi.cs` (`JoinAsync`)
- Test: `tests/Kumbu.Server.Tests/Welcome/WelcomeVaultTests.cs`

**Interfaces:**
- Consumes: `VaultCatalog.CreateForUserAsync(string name, string userId, byte[] userKey, CancellationToken ct, DateTime? credentialReadAt = null)` (returns the `UnlockedVault`, whose `Id` the clean-up needs), `VaultCatalog.DeleteVaultsAsync(IReadOnlyCollection<string>, Func<IClientSessionHandle, CancellationToken, Task>, CancellationToken)`, `EncryptedNoteStore(VaultCatalog, ICredential, SearchIndex?)`, `UserCredential(string userId, byte[] userKey)`, `INoteStore.TryCreateAsync(StoredNote, CancellationToken)`, `Writers.Seed`, `IOptions<KumbuOptions>` (`PublicUrl`).
- Produces:
  - `public sealed class WelcomeVault(VaultCatalog catalog, IOptions<KumbuOptions> options, TimeProvider time, ILogger<WelcomeVault> log, SearchIndex? index = null)`
  - `public const string Name = "Welcome";`
  - `public const string Placeholder = "{{server}}";` and `public const string NoAddress = "https://<your Kumbu² address>";`
  - `public static IReadOnlyList<(string Name, string Content)> Texts()`: the embedded notes, ordered by name, file name with `.md`, content as in the file (LF).
  - `public static string Fill(string content, string? publicUrl)`
  - `public async Task CreateAsync(User user, byte[] userKey, DateTime credentialReadAt, CancellationToken ct)`: creates the vault and writes every text; never throws (logs instead), except for cancellation.

- [ ] **Step 1: Write the failing tests** in `WelcomeVaultTests` (use `KumbuAppFactory.PublicApi` for the public-URL cases, the plain factory otherwise; join through `POST /api/invitations` and `POST /api/join` as `AccountApiTests.InvitationCodeAsync` does):

```csharp
[Fact] public async Task Joining_creates_the_welcome_vault_with_every_text()
// GET /api/vaults with the join's session → contains "Welcome";
// GET /api/vaults/Welcome/tree → note names equal WelcomeVault.Texts().Select(t => t.Name), in text order;
// GET /api/notes/Welcome/Start%20here.md → content == WelcomeVault.Fill(<embedded Start here>, KumbuAppFactory.PublicUrl),
//   lastWriter == "seed".

[Fact] public async Task The_server_address_is_filled_in()
// with PublicApi: no note content contains "{{"; "Connect your AI.md" contains "https://kumbu.test/mcp".

[Fact] public async Task Without_a_public_url_the_address_is_a_readable_placeholder()
// plain factory: "Connect your AI.md" contains "https://<your Kumbu² address>/mcp" and no "{{".

[Fact] public async Task Signing_in_again_never_recreates_the_welcome_vault()
// join; delete "Welcome" (POST /api/vaults/Welcome/delete with password and confirm); sign in again →
// GET /api/vaults has no "Welcome".

[Fact] public async Task A_user_made_without_joining_gets_no_welcome_vault()
// RestClient.NewAsync(...) → GET /api/vaults names == ["Arbeit"].

[Fact] public async Task A_failing_welcome_vault_never_fails_the_join()
// Call WelcomeVault.CreateAsync directly for a user that was deactivated after sign-in
// (UserCatalog/Admin deactivate as the tests of deactivation do): it returns without throwing,
// the captured log (CapturedLog) has one warning "Could not create the welcome vault for user {id}: …"
// with no username and no note text, and the user has no vault "Welcome" (nothing half written).
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~WelcomeVaultTests"`
Expected: FAIL, `WelcomeVault` does not exist.

- [ ] **Step 3: Embed the texts.** In the csproj, next to the existing `common-passwords.txt` entry:

```xml
<EmbeddedResource Include="Welcome/Notes/*.md" LogicalName="Kumbu.Server.Welcome.Notes.%(Filename)%(Extension)" />
```

`Texts()` lists `GetManifestResourceNames()` with the prefix `Kumbu.Server.Welcome.Notes.`, strips it, reads each stream as UTF-8 with CRLF → LF, and orders by `TextOrder.Default`.

- [ ] **Step 4: Implement `WelcomeVault`.** `Fill` replaces every `{{server}}` with `PublicUrl.TrimEnd('/')`, or with `NoAddress` when it is null or empty. `CreateAsync`: `catalog.CreateForUserAsync(Name, user.Id, userKey, ct, credentialReadAt)`, then a fresh `new EncryptedNoteStore(catalog, new UserCredential(user.Id, userKey), index)` and for each text `TryCreateAsync(new StoredNote(VaultPath.Root.Child(Name).Child(name), filled, 1, Writers.Seed, now, [], filled), ct)`, as `Seeder.SeedAsync` writes seeded notes. On any exception other than `OperationCanceledException`: log `LogWarning("Could not create the welcome vault for user {UserId}: {Reason}", user.Id, e.GetType().Name)` (the type, not the message, which may name the vault) and, if the vault was created but a note failed, delete the vault again through `catalog.DeleteVaultsAsync([vault.Id], (_, _) => Task.CompletedTask, CancellationToken.None)` so no half-filled "Welcome" stays. Catching broadly is deliberate: the join has already created the account, and an exception here would hide the recovery key from the user (comment that in one sentence). Register it in `Program.cs` as a singleton next to the other account services.

- [ ] **Step 5: Call it from `JoinAsync`**, inside the branch where `TrySignInAsync` succeeded and the session started, after `SetSessionCookie`: `await welcome.CreateAsync(signedIn, userKey, readAt, CancellationToken.None);` (not cancellable, like the rest of the join after the claim). Add `WelcomeVault welcome` to the handler's parameters. A join whose sign-in fails (deactivated meanwhile) gets no welcome vault; that's acceptable.

- [ ] **Step 6: Run the tests, then the whole suite**

Run: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~WelcomeVaultTests"` → PASS. Then `dotnet test`. Fix every test that now sees a "Welcome" vault after a join (at least `AccountApiTests.JoinedWithKeysAsync` users list vaults; assertions on a joined user's vault list must include `Welcome` or filter it), without weakening what they check.

- [ ] **Step 7: Commit**

```bash
git add src/Kumbu.Server/Welcome src/Kumbu.Server/Kumbu.Server.csproj src/Kumbu.Server/Program.cs src/Kumbu.Server/Api/AccountApi.cs tests/Kumbu.Server.Tests
git commit -m "Give every new user a welcome vault"
```

### Task 2: The texts

**Files:**
- Create/replace: the seven files in `src/Kumbu.Server/Welcome/Notes/`
- Test: `tests/Kumbu.Server.Tests/Welcome/WelcomeTextTests.cs`

**Interfaces:**
- Consumes: `WelcomeVault.Texts()`, `WelcomeVault.Placeholder`; `Kumbu.Core.Links` link parsing (the same parser `links` uses) for the link test.
- Produces: the texts; nothing else depends on them in code.

- [ ] **Step 1: Write the failing text tests**

```csharp
[Fact] public void The_vault_holds_exactly_the_seven_notes()
// WelcomeVault.Texts().Select(t => t.Name) == ["Connect your AI.md", "Obsidian.md", "Security and privacy.md",
//   "Start here.md", "Working by hand.md", "Your AI in Kumbu².md", "Your data.md"] (in TextOrder.Default).

[Fact] public void Every_link_resolves_and_no_link_is_a_web_url()
// every [[wikilink]] in every text targets one of the seven note names (with or without .md, an optional #heading
// that exists in that note); no Markdown link points at "/notes/" or at the server; external links are https.

[Fact] public void Start_here_links_every_other_note()
// Start here.md contains [[Working by hand]], [[Your AI in Kumbu²]], [[Obsidian]], [[Connect your AI]],
// [[Security and privacy]], [[Your data]].

[Fact] public void The_texts_are_ai_agnostic_outside_the_client_sections()
// "Claude", "ChatGPT", "Anthropic", "OpenAI" appear in no text except Connect your AI.md, and there only below
// the heading "## This is how you connect Claude (claude.ai, Desktop and mobile)" and "## … Claude Code".

[Fact] public void No_text_names_a_deployer_or_leaves_a_placeholder_unknown()
// no "tidesson" (case-insensitive); every "{{" is exactly "{{server}}".

[Fact] public void Tested_markers_are_as_released()
// Connect your AI.md: the Claude Code section contains "*Tested.*"; the claude.ai section and the
// "any other MCP client" section contain "*Should work, not tested yet.*" (Task 4 flips claude.ai after the dry run).
```

- [ ] **Step 2: Run them to see them fail** — `dotnet test … --filter "FullyQualifiedName~WelcomeTextTests"` → FAIL (only the stub exists).

- [ ] **Step 3: Write the seven texts.** Each starts with `# <title>` (the note name without `.md`), is short enough to read on a phone in two or three minutes, uses headings for its parts, and links back to `[[Start here]]` at the end. Content to cover, as facts the text must state correctly (menu names as the web app shows them after the OAuth merge and the v0.2.0 PRs):

**`Start here.md`**
- Opening, verbatim: "Kumbu² is your second brain: notes in plain Markdown that you write by hand and that your AI may read and write too. It is your memory; your AI gets a key."
- The name: *kumbukumbu* is Swahili for memory; kumbu × kumbu = kumbu², and the ² reads as "second", as in second brain.
- A vault is a top-level collection of notes (this one is "Welcome"); you can have several and give each AI access to only some.
- Three ways in, each one sentence and a link: by hand in this web app, on a phone or a laptop ([[Working by hand]]); your AI through MCP ([[Your AI in Kumbu²]], then [[Connect your AI]]); Obsidian, kept in sync ([[Obsidian]]).
- Then [[Security and privacy]] and [[Your data]].
- "Try it": after connecting an AI, two or three sentences to say to it, e.g. "Look in my Kumbu² Welcome vault and summarise what Kumbu² can do." and "Make a note in my Welcome vault called 'My first note' with three things I want to remember this week."
- This vault is yours: edit it, rename it, delete it (Settings → Vaults).

**`Working by hand.md`**
- Navigation: the drawer lists vaults and folders; new vault from the drawer.
- Read and Write mode; text saves itself; if your AI wrote meanwhile, a prompt shows both, nothing is lost.
- Links: `[[Note name]]` like in Obsidian, resolved within the vault; `[[Note#Heading]]` for a section; backlinks show which notes link here.
- Search by words and by meaning (also across languages).
- History: a note's ⋯ menu → History; the last 10 earlier versions, restore any.
- Trash: the vault's ⋯ menu → Trash; removed notes stay 30 days.
- Checkpoints: the vault's ⋯ menu → Checkpoints; a named state of a note, folder or vault that never expires (up to 10 per vault by default); restore after a preview of exactly what changes.
- Upload Markdown files or whole folders.
- Settings: colour scheme, "Refresh Read mode automatically".

**`Your AI in Kumbu².md`**
- Your AI reaches Kumbu² through MCP, the open protocol for giving AI tools to an assistant; it gets exactly the vaults you give it, read or read and write.
- What it can do: list and read notes, search by words and meaning, follow links and backlinks, look at history and differences, write and edit notes or one section, move and rename (links are rewritten), remove to the trash, make checkpoints, replace a text across a folder after showing a preview first.
- It can't overwrite your change by accident: every write names the version it read; if the note changed meanwhile, the write fails and it reads again.
- Seeing what it did: its writes carry the name of its key or connection; the banner on a note shows changes by your agents since your last visit, with highlights; the bar "changed elsewhere" while a note is open; the vault's ⋯ menu → Activity lists changes grouped by session with side-by-side comparison.
- Undoing: History per note; a checkpoint before a big job ("make a checkpoint of this vault first" is a good sentence to give it).
- Changes made by you in Obsidian count as yours, not an agent's.
- Taking access away: drawer → Access, revoke the key or connection; it stops at once.

**`Connect your AI.md`**
- Intro: any AI client that speaks MCP can use Kumbu². Two ways, both on the **Access** page (drawer → Access): a **connection** for apps that sign in through the browser (OAuth), and an **API key** for agents on your own machine. Give each AI its own connection or key, named after it, with only the vaults it needs.
- `## This is how you connect Claude (claude.ai, Desktop and mobile)`: Access → Connect an app → name it, choose vaults and rights (none, read, read and write), optionally an expiry → copy the connection's own address → in claude.ai: Settings → Connectors → Add custom connector, paste it, leave client ID and secret empty → Kumbu²'s consent page shows what is being connected → Allow. A connector added on claude.ai also works in Desktop and the mobile apps. *Should work, not tested yet.*
- `## This is how you connect Claude Code`: Access → new API key (shown once) → `claude mcp add --transport http kumbu {{server}}/mcp --header "Authorization: Bearer kumbu_…"` in a code block. *Tested.*
- `## This is how you connect any other MCP client`: with a key: streamable HTTP at `{{server}}/mcp` and the header `Authorization: Bearer <key>`; with a connection: the connection's address, if the client supports MCP sign-in (OAuth). *Should work, not tested yet.*
- "Your Kumbu² address is the one in your browser's address bar right now." (covers the placeholder case).
- Good to know: the plain `{{server}}/mcp` takes keys only; a connection's address takes no key; a key or connection is revoked on the Access page at once.

**`Obsidian.md`**
- Kumbu² Sync keeps an Obsidian vault and a Kumbu² vault the same, on a laptop or a phone; text notes only (images and PDFs stay local and are counted as "not synced").
- Steps: in Kumbu², the vault's ⋯ menu → Connect Obsidian… → in Obsidian install the community plugin BRAT, add the beta plugin `SCAI-NGO/kumbu-obsidian`, enable Kumbu² Sync → enter the server address `{{server}}`, a device name and the pairing code (valid 10 minutes, once).
- The first sync merges both sides: notes on one side are copied, equal notes are left alone. Only if notes differ, one question for all of them: keep both (safest; your text becomes a copy next to the original), keep local, or keep Kumbu² (your local version goes to Obsidian's trash first). At the end it says how many notes moved, and "no conflicts" when there were none.
- After that changes arrive within seconds while online, and on the next start otherwise. If both sides changed the same note, a conflict copy appears next to it with a note at the top saying where each change came from; merge what you need and delete the copy.
- Each device gets its own key, named "<vault> – Obsidian <device>", listed on the Access page; revoking it stops that device. By default it doesn't expire; the dialog lets you choose 30, 90 or 365 days.

**`Security and privacy.md`** (Chris reviews this text in the PR; source `docs/security-architecture.md` §1, §2, §4)
- Encrypted at rest: every note, its history, the trash, and even note paths and vault names are encrypted before they are stored; each vault has its own key. A copy of the database or a backup shows nothing readable.
- Your password and your recovery key (shown once when you joined) unlock your vaults. Keep the recovery key somewhere safe; lose both and nobody can open your vaults, not even an admin, who cannot reset your password.
- Other users can't see your vaults at all. An admin sees usernames, how many vaults and how much space you use, never a vault name, note name or note.
- Keys and connections open exactly the vaults and rights you gave them and stop the moment you revoke them; their writes carry their name.
- Honest limits: the running server works with your notes in memory (that's how search works), so whoever operates your Kumbu² server is trusted — choose who runs it. Your AI's provider sees what your AI reads and writes, under the provider's own terms; this is not end-to-end encryption. Search by meaning runs on the server itself unless you choose an external provider for a vault (the vault's ⋯ menu → Search by meaning…), which then receives that vault's text.
- Deleting is for good: a vault or your account, with your password (Settings). Backups the operator keeps are not reached by a deletion; ask your operator about their backup policy.
- The web app keeps no notes in your browser's storage.

**`Your data.md`**
- No lock-in: your notes are plain Markdown.
- Export one vault: the vault's ⋯ menu → Export as ZIP; unpacked, it opens in Obsidian as a vault.
- Export all: Settings → Your data → Export my vaults… (untick any you don't want); one folder per vault.
- Import: Settings → Your data → Import vaults…; each folder becomes a vault (or merges into one of the same name); history, trash and checkpoints are not carried over — a move starts a fresh history.
- Delete a vault or your account in Settings (password and name typed out).
- If you can't sign in any more and want your data deleted, write to your Kumbu² server's operator.

- [ ] **Step 4: Run the text tests** — `dotnet test … --filter "FullyQualifiedName~WelcomeTextTests"` → PASS; then `WelcomeVaultTests` again → PASS.

- [ ] **Step 5: Read every text once in the running web app** (`dotnet run --project src/Kumbu.Server` with a local MongoDB and `pnpm -C web dev`, join with a fresh invitation): the links work, headings render, nothing reads awkwardly on a phone-width window. Fix and rerun the tests.

- [ ] **Step 6: Commit**

```bash
git add src/Kumbu.Server/Welcome/Notes tests/Kumbu.Server.Tests/Welcome/WelcomeTextTests.cs
git commit -m "Write the welcome vault's notes"
```

### Task 3: Docs and pull request

**Files:**
- Modify: `README.md` (accounts section: "A new account gets a vault *Welcome* with notes about Kumbu²; it is the user's like any other. The texts live in `src/Kumbu.Server/Welcome/Notes/`; `{{server}}` is replaced by `KUMBU_PUBLIC_URL`."), `docs/security-architecture.md` (only if §4 lists what a new account holds: add the welcome vault, written by `seed`).

- [ ] **Step 1: Update the two docs** as above, British English.
- [ ] **Step 2: Run the full suite** — `dotnet test` → PASS.
- [ ] **Step 3: Commit and open the pull request** against `master`, title "Give new users a welcome vault". The description lists: the seven notes, that Chris should review `Security and privacy.md`, and which features the texts describe that are not merged yet (by PR), so the review knows what must land before v0.2.0.

### Task 4: Release check (before tagging v0.2.0)

**Files:**
- Modify: `src/Kumbu.Server/Welcome/Notes/*.md`, `tests/Kumbu.Server.Tests/Welcome/WelcomeTextTests.cs` (`Tested_markers_are_as_released`)

- [ ] **Step 1: Remove what was cut.** For each feature of the drop order not in v0.2.0 (search in earlier versions, deleted-ids list, Activity), remove its sentences from the texts (Activity is named in `Your AI in Kumbu².md`).
- [ ] **Step 2: Check every menu name the texts quote against the deployed app** on kumbu.tidesson.net: drawer → Access, Connect an app, the vault's ⋯ menu entries (History, Trash, Checkpoints, Connect Obsidian…, Export as ZIP, Activity, Search by meaning…), Settings → Your data, Settings → Vaults.
- [ ] **Step 3: After the dry run**, if claude.ai (web, Desktop, mobile) worked end to end, change its marker to `*Tested.*` and the assertion in `Tested_markers_are_as_released` with it; otherwise leave both.
- [ ] **Step 4: Run** `dotnet test --filter "FullyQualifiedName~Welcome"` → PASS; commit (`Bring the welcome texts in line with v0.2.0`) on the same branch or a follow-up PR if the first one is merged.

## Self-review notes

- Spec coverage: placeholders → Task 1 (`Fill`), Task 2 tests; tested markers → Task 2, Task 4; on registration incl. `bootstrap-admin` → Task 1 (`bootstrap-admin` only prints a join link, so `JoinAsync` covers it); theirs, editable, deletable, never re-created → Task 1; embedded Markdown, new users only → Task 1; AI-agnostic, English → Global Constraints, Task 2 test; the seven notes and their contents → Task 2; texts match the shipped features → Task 4.
- Decided here, not in the spec (for Chris to check): the fallback address is a visible placeholder plus "the address in your browser's address bar" instead of "ask your admin" (the browser's address *is* the server's, so nobody needs to be asked); the writer is `seed`; a failed creation removes a half-filled vault; the notes sit flat at the vault's root.
