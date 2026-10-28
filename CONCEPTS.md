# Concepts

> **Draft by Claude — Chris rewrites each chapter in his own words.**

## 1. Talking to a model through MCP

### What MCP is

MCP (Model Context Protocol) is a way for an AI application to use outside capabilities. There are two sides. The **client** is the AI application (Claude Code, for example). The **server** is a program that offers capabilities. The most important kind is a **tool**: a named function with a description and typed arguments. The model reads the description, decides to call the tool, and the client sends the call to the server. Messages are JSON-RPC 2.0.

Kumbu² started as a server with one tool, `sh`; today it has four (`sh`, `write`, `edit`, `replace`).

### Why Streamable HTTP

MCP can run over standard input/output (the client starts the server as a child process) or over HTTP. Kumbu² is a hosted service that several people and agents reach from different machines, so it uses HTTP. In Streamable HTTP every message is a `POST /mcp`. The server may answer with plain JSON or with a short event stream (SSE). Our server answers with SSE even for a single result.

### The protocol change of 2026-07-28

Older revisions started with a handshake. The client sent `initialize`, the server answered and could hand out an `Mcp-Session-Id`, and the client then repeated that id on every call. The server had to remember the session. That makes scaling awkward: the next request must reach the same instance.

The 2026-07-28 revision removes sessions and `initialize` (SEP-2567 and SEP-2575). Each request is self-contained and stateless. What the handshake used to negotiate, the protocol version and the client's capabilities, now travels in `_meta` on every request. Three headers repeat the essentials so proxies can route without reading the body: `MCP-Protocol-Version`, `Mcp-Method` and `Mcp-Name`. Because the request describes itself, any server instance can answer it.

Kumbu² is stateless (the SDK default; we did not enable sessions). Real exchanges against our server follow. The dev token is redacted. These were captured under the test host (`dotnet test tests/Kumbu.Server.Tests --output Detailed` prints them). `serverInfo` reads `kumbu` and the server's version because `Program.cs` sets it; left unset, the SDK falls back to the entry assembly's name, which under the test host is `Kumbu.Server.Tests`.

**Old form (2025-06-18): `initialize`.** Our server still answers it, but without an `Mcp-Session-Id`.

Request:

```http
POST /mcp
Authorization: Bearer <dev token>
Accept: application/json, text/event-stream
Content-Type: application/json; charset=utf-8

{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "2025-06-18",
    "capabilities": {},
    "clientInfo": { "name": "raw", "version": "0" }
  }
}
```

Response, as sent (SSE framing):

```http
200 OK
Cache-Control: no-store, no-cache
X-Accel-Buffering: no
Content-Type: text/event-stream
Content-Encoding: identity

event: message
data: {"result":{"protocolVersion":"2025-06-18","capabilities":{"logging":{},"tools":{}},"serverInfo":{"name":"kumbu","version":"0.1.0"}},"id":1,"jsonrpc":"2.0"}
```

The same message, pretty-printed:

```json
{
  "result": {
    "protocolVersion": "2025-06-18",
    "capabilities": { "logging": {}, "tools": {} },
    "serverInfo": { "name": "kumbu", "version": "0.1.0" }
  },
  "id": 1,
  "jsonrpc": "2.0"
}
```

**New form (2026-07-28): one self-describing `tools/call`.** No handshake, no session.

Request:

```http
POST /mcp
Authorization: Bearer <dev token>
Accept: application/json, text/event-stream
Content-Type: application/json; charset=utf-8
MCP-Protocol-Version: 2026-07-28
Mcp-Method: tools/call
Mcp-Name: sh

{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "sh",
    "arguments": { "command": "ls /" },
    "_meta": {
      "io.modelcontextprotocol/protocolVersion": "2026-07-28",
      "io.modelcontextprotocol/clientCapabilities": {}
    }
  }
}
```

Response, as sent (SSE framing):

```http
200 OK
Cache-Control: no-store, no-cache
X-Accel-Buffering: no
Content-Type: text/event-stream
Content-Encoding: identity

event: message
data: {"result":{"content":[{"type":"text","text":"Demo/\nIdeas/"}],"isError":false,"resultType":"complete","_meta":{"io.modelcontextprotocol/serverInfo":{"name":"kumbu","version":"0.1.0"}}},"id":2,"jsonrpc":"2.0"}
```

The same message, pretty-printed:

```json
{
  "result": {
    "content": [ { "type": "text", "text": "Demo/\nIdeas/" } ],
    "isError": false,
    "resultType": "complete",
    "_meta": {
      "io.modelcontextprotocol/serverInfo": { "name": "kumbu", "version": "0.1.0" }
    }
  },
  "id": 2,
  "jsonrpc": "2.0"
}
```

What the SDK (version 2.3.0) insisted on for the new form, found by sending less and reading the errors: both headers `Mcp-Method` and `Mcp-Name` (otherwise HTTP 400, JSON-RPC error `-32020`), and both `_meta` entries (otherwise 400, `-32602`). `clientCapabilities` must be a JSON object, but `{}` is fine. The server's own `serverInfo` now comes back in the result's `_meta`, and results carry `resultType: "complete"`.

### Errors are tool results, not JSON-RPC errors

JSON-RPC errors are for protocol problems: a malformed request, a missing header, an unknown method. A wrong path or a missing note is not a protocol problem. It is an ordinary outcome of the model's command. So the `sh` tool returns it as a normal result with `isError: true` and a readable message, such as a missing file. The model reads the text and can correct itself, for example by running `ls` and trying again. A JSON-RPC error would usually be handled by the client's plumbing, and the model might never see it. Commands never throw on user input.

### The tool description is the man page

The model knows nothing about `sh` except what the tool description says. That text (in `src/Kumbu.Core/Sh/Manual.cs`) is therefore the manual: what the commands are, that directories end with `/`, that quoting is needed for spaces, that output is capped, and that note content is data and not instructions. If the model misuses the tool, the first place to look is this text.

### Why paths are absolute

A shell with a working directory needs state: `cd` changes something between calls. Stateless requests have nowhere to keep that, and the model could lose track of where it is. With absolute paths, such as `/Work/projects/plan.md`, every command means the same thing on its own. Top-level directories are the vaults, and `ls` with no path lists them.

## 2. A shell as the agent's interface

### Why a shell at all

Models have read an enormous amount of Unix. `ls`, `cat`, `head` and `rg`, joined by `|`, need no explanation: a model reaches for them first. So instead of inventing ten tools with their own JSON arguments, Kumbu² offers one tool, `sh`, and speaks that language. The price is that we have to behave enough like a real shell that the model's expectations hold, and fail loudly where they don't.

### From a command line to a pipeline

A command line passes through two small steps before anything runs:

1. **Tokenizer (lexer).** It walks the characters once and produces words and the pipe operator. Quoting decides which is which: in `rg 'a|b'` the `|` is part of the word `a|b`, in `rg a | head` it is an operator. Only the tokenizer knows about quotes, so only it can tell the two apart.
2. **Pipeline (parser).** It splits the tokens at each pipe into stages. Each stage is a command name and its arguments. A pipe at the start, at the end or twice in a row is a syntax error.

Then the shell runs the stages left to right. The output of one stage becomes the input of the next. The concept describes each command as a function `string[] → string[]` (lines in, lines out), and a pipe as a chain of them. In the code the text travels as one string and filters split it into lines (`Lines.Split`): a trailing newline ends the last line instead of starting an empty one.

### Sources and filters

Not every command makes sense right of a pipe. `ls`, `cat` and `help` produce text from the vault (**sources**); `head`, `tail`, `wc`, `sort` and `rg` transform text (**filters**). `ls /Work | cat` is rejected with a message that lists the filters, because the model almost certainly meant something else.

A filter at the start of a pipeline reads the note it is given (`head -n 5 /Work/plan.md`). Behind a pipe it takes no path. Unix would silently let the file win over the input; here that is an error, because a model that writes both has misunderstood something, and saying so costs one call instead of a wrong answer.

When a stage fails, the pipeline stops and that stage's error is the result (bash calls this `set -o pipefail`). Otherwise `cat /Work/nope.md | wc -l` would answer `0`, and the model would believe the note is empty.

### Only one operator

Real shells have many operators: `&&`, `;`, `>`, `$( )` and more. Kumbu² supports only `|`. The others are not passed through as text but rejected, with the operator named and a hint to quote it. The reason: a model that writes `ls /A && ls /B` expects two listings. Treating `&&` as an argument would produce a confusing error from `ls` at best, and at worst a plausible but wrong answer. Rejecting it clearly lets the model split the work into two calls.

Two characters stay ordinary on purpose. `*` is not a glob, because globs need a working directory to make sense. `#` does not start a comment, because `note.md#Heading` addresses a section (concept §4).

### Searching with `rg`

`rg` is ripgrep's command name, and models know its output: `path:line:text` for a match, `path-line-text` for context lines, and `--` between groups that are not adjacent. Kumbu² copies that layout, so a model can read it without instructions. Without a path it searches every vault (there is no working directory to default to). Right of a pipe it filters lines instead and prints them bare, as ripgrep does with standard input.

`rg --files` lists note paths. Together with a filter it answers a question the first test raised: where does `[[kumbu]]` live? `rg --files | rg -i kumbu` prints `/Demo/projects/kumbu.md`. Since step 4, `links` resolves a wikilink directly (chapter 5).

### Regular expressions that cannot hang the server

A regex comes from the model and runs on the server. That is a classic attack surface called **ReDoS** (regular expression denial of service). Most regex engines, .NET's default included, work by **backtracking**: they try one way to match, and when it fails they step back and try another. For some patterns the number of ways explodes. `^(a+)+$` against `aaaa…a!` tries every way of splitting the a's into groups before giving up: 2ⁿ attempts for n characters. A few dozen characters are enough to keep a CPU busy for a very long time.

The alternative is to compile the regex into an **automaton** (a state machine) and walk the input once, tracking every possible state at the same time. Time is then linear in the input, whatever the pattern. Rust's `regex` crate, which ripgrep uses, works this way. So does .NET with `RegexOptions.NonBacktracking`, which Kumbu² uses (plus a timeout as a backstop).

The cost: features that need to remember and revisit, such as look-around (`(?=…)`) and backreferences (`\1`), are impossible. ripgrep doesn't have them by default either, so models rarely expect them. If one is used anyway, the error says why it is missing.

### Errors and `help` as teaching

The tool description is sent to the model every time it connects, so every line of it costs tokens in every conversation. It therefore stays a short man page: the commands, one line each, plus the rules that matter on every call. The details (every option of `rg`, examples) live in `help <command>`, which the model only fetches when it needs them. This is called **progressive disclosure**. `rg --help` works too, because models try that first.

Error messages do the same job. `command not found: grepp — try 'help'` names the next step. For the commands models reach for most often, the hint is specific: `grep` points to `rg`, `find` to `rg --files`, `cd` to "there is no working directory". Every error should let the model fix its next call without guessing; the phase 1 test showed that this works.

## 3. Writing safely: versions, history and trash

### Why writing needs more than "save"

Reading can't break anything. Writing can, in two ways that matter for agents. First, **lost updates**: two writers (two agent sessions, or an agent and you) read the same note, both change it, and the second save silently throws away the first one's change. Second, **mistakes**: an agent misunderstands a request and rewrites a note wrongly. Step 3 answers the first with version tokens and the second with history and a trash.

### Content goes through JSON, not the shell

`sh` stays the language for reading and for operations that only need a path (`rm`, `restore`). Content travels through two separate tools, `write` and `edit`, as JSON parameters. A Markdown note has quotes, backticks, `$`, `|` and line breaks; pushing that through a shell command line would mean escaping all of it correctly, which models get wrong exactly when it matters. In a JSON string the client does the escaping.

### Version tokens and optimistic concurrency

Every `cat` returns a **version token** next to the content, for example `version of /Demo/welcome.md: 3f9a2c1b7d0e`. It comes as a separate content block of the MCP result, so a pipe like `cat x | head` never mixes it into the text. `write` and `edit` want the token back. The server compares it with the note's current token: if they match, nobody changed the note since the agent read it, and the write goes through. If not, it fails with "has changed since version …; cat it again and redo the change".

This is called **optimistic concurrency**: nothing is locked while the agent thinks; conflicts are detected at the moment of writing. It is the same idea as HTTP's `ETag` and `If-Match`. (The pessimistic alternative, locking a note while someone edits it, needs to know when the editor is done, which a stateless protocol can't tell.)

The token is the first 12 hex characters of the SHA-256 hash of the content. Because it is computed from the content, it never has to be stored or migrated, and two identical texts have the same token. That sounds like a flaw (change a note from A to B and back to A, and an old token for A works again) but is harmless: the agent's change is applied to exactly the text it read.

### Compare-and-swap in MongoDB

Comparing the token and then saving is two steps. Between them, another writer could save, and both would win. So the store makes the comparison part of the write itself: each note has a revision counter, and the replace is one `UpdateOne` whose filter includes the revision the writer saw. If someone got there first, the filter matches nothing and the update changes nothing. One database operation, atomic by itself, no transaction needed. This pattern is called **compare-and-swap (CAS)**.

Because a note's history is stored inside the note's document, the history moves with every write in that same single operation. A move to the trash copies the document and then deletes the original with the same revision filter; if the delete finds nothing, the copy is removed again.

### History and coalescing

Each note keeps its last 10 earlier states, newest first, with who wrote them and when (`history <path>`). `restore <path> <version>` makes one current again, and the state it replaces goes into the history, so a restore can be undone the same way.

An agent working on a note often saves several times in a row. Keeping every intermediate step would push the interesting state, the one before the agent started, out of the 10 slots. So writes by the **same writer close together in time coalesce**: the state they replace is not kept again. What survives is the state before the session. (Until step 6b this was "within 10 minutes on the same note"; chapter 8 makes it a changeset across notes.) Each API key and app connection writes under its own name (chapter 10), so two agents never coalesce into each other.

### The trash

`rm` doesn't delete. It moves the note, history included, into a separate `trash` collection with the time of deletion. MongoDB removes it after 30 days by itself: a **TTL index** on `deletedAt` makes a background task delete documents once that date is older than the index's limit. `restore <path>` brings back the most recently removed note at that path, as long as no new note took its place.

`rm` and `restore` take no version token. A token protects against losing someone's change without noticing; both operations can be undone, so nothing is lost.

### Seeding becomes a three-way merge

Until now, the folder on disk was the source and the database a copy. Now agents write to the database, so the database is the source of truth. Still, editing files and restarting stays useful. The seeder therefore remembers, for each note, the content it last loaded (`seedContent`), and compares three texts: the file, the note, and that base.

| File vs. base | Note vs. base | Meaning | Action |
|---|---|---|---|
| same | same | nothing changed | none |
| changed | same | edited on disk | load the file (the old state goes into the history) |
| same | changed | edited through MCP | keep the note |
| changed | changed | edited on both sides | keep the note, warn in the log |

This is the same idea as a **three-way merge** in Git: with only two versions you can see that they differ, but not which side changed. The common ancestor tells you. Notes removed with `rm` stay removed, even though their file is still on disk.

(Replaced in step 5: the seeder is a development aid on its way out, so it now only fills vaults that are still empty. The three-way idea returns with the Obsidian sync plugin, where it belongs.)

## 4. Sections: the heading as the unit

### Why sections

A note can be long, and the output of one `sh` call is capped at about 8,000 tokens. Reading a whole note to change one line wastes the model's context, and a truncated note hides the part the model needed. Markdown already has a structure people use: headings. So the heading is the unit for reading, for addressing, and later for chunking and embeddings (concept §6).

### Finding the headings

A heading is a line starting with one to six `#` and a space (an **ATX heading**, in CommonMark's terms). Two places look like headings but aren't: inside a **fenced code block** (between ```` ``` ```` or `~~~` lines), where `# comment` is shell or Python, and inside the **YAML frontmatter** at the top of a note, between `---` lines. The parser walks the lines once, keeps track of whether it is inside a fence, and skips both. `#tag` without a space is a tag, not a heading.

A **section** is a heading plus everything up to the next heading of the same or a higher level. So `## Gates` under `# Architecture` belongs to the Architecture section, and the next `#` heading ends both. Blank lines at the end of a section are left out of what `cat` shows and what an edit replaces, so the spacing before the next heading survives an edit.

`outline` prints this tree with line ranges and sizes, so a model can choose what to read without reading it.

### Addressing a section

The address format follows Obsidian's links: `note.md#Architecture#Gates`. Everything after the first `#` that follows `.md` is the address, each further `#` goes one level down. Two rules make it practical:

- **Short addresses work.** An address matches the *end* of a heading's chain, so `#Gates` finds `## Gates` wherever it is, as long as it is the only one. Models start with the short form; the long form is only needed when the short one is ambiguous.
- **Ambiguity fails loudly.** If an address matches more than one heading, nothing is picked; the error lists the full addresses to choose from. Two headings with the same text under the same parent get an index, `#Notes[1]` and `#Notes[2]`. Indexes shift when someone inserts a heading, but every edit carries the note's version token, so a stale index can only lead to a refused edit, never to editing the wrong section.

A literal `#` in a heading is written `\#` (`#C\# basics`), and a heading that itself ends in something like `[2]` escapes the bracket.

### Editing a section

`edit` takes the section in its `path`. Then `old_text` only needs to be unique within that section, which is often the easiest way out of "old_text occurs 3 times". Without `old_text`, `new_text` is appended at the end of the section, before its trailing blank lines; appending a new `##` heading there is how a section is inserted after another one. With `replace_section`, the whole section, heading included, is replaced. The version token is always the whole note's: the section is just a way to point into it.

### One more kind of error message

Tool parameters have a JSON schema, and most clients check it before calling. A model that ignores it, though, used to get the SDK's generic "An error occurred invoking 'edit'", which teaches nothing. A **call-tool filter** now runs before the SDK binds the arguments: it reads the tool's own schema, finds the missing required parameter and answers like every other error, with the next step (`edit: version is required; get it from 'cat <path>'`). A filter here works like ASP.NET middleware: a function that wraps the next handler and may answer on its own instead of calling it.

## 5. Links: resolving at read time, rewriting only on `mv`

### A link is a name, not an address

`[[kumbu]]` doesn't say where the note is. Obsidian finds it when you click: the note whose path ends with `kumbu.md`, whichever folder it is in. That keeps links short and lets people reorganise folders without breaking them. The price: the same text can mean different notes depending on what else exists. If a second `kumbu.md` appears, `[[kumbu]]` becomes **ambiguous**. Obsidian then quietly picks one. Kumbu² follows Obsidian's rules (path suffix, case-insensitive, the source's own folder wins a tie, `./` and `../` relative to the source) but where Obsidian would guess, it reports the candidates instead: an agent can ask, a guess would silently send it to the wrong note.

### Resolve when reading, store nothing

There are two ways to answer "what links here?". Keep an **index** of links that every write updates, or **parse on demand**: read the vault's notes and resolve their links when someone asks. An index is fast, but it can go stale, needs a migration whenever the rules change, and every write path (agents, the seeder, later the Obsidian plugin) has to maintain it. Parsing a few thousand notes takes milliseconds, and the answer is always current. With encryption (step 5) the unlocked vault sits in memory anyway (concept §7). So Kumbu² parses on demand; an index can come if vaults get big.

This fits a decision from step 4a: **content is never rewritten automatically.** Notes are stored exactly as written, and links are a way of *reading* them, not something Kumbu² normalises.

### What counts as a link

A wikilink is `[[target]]`, with optional parts: `#Heading` or `#^block` after the target, `|alias` for the text shown (written `\|` inside tables, where `|` separates columns), and `!` in front for an embed. Links inside fenced code blocks and inline code are examples, not links. Links in YAML frontmatter count, because Obsidian treats them as links. A target that matches no note but looks like a file (`diagram.png`) is an attachment; Kumbu² stores text only, so it shows them as such and leaves them alone.

### Moving a note: compare before and after

`mv` is the one place where Kumbu² writes links. The obvious rule, "rewrite links to the moved note", is not enough. Two cases show why:

- Moving `projects/kumbu.md` to `archive/kumbu.md` changes nothing for `[[kumbu]]`: the name is still unique, the link still finds it. Rewriting it would be an edit for nothing.
- Moving `a/plan.md` to `a/kumbu.md` touches no link to `plan` that resolves, but every `[[kumbu]]` elsewhere that meant `projects/kumbu.md` would suddenly be ambiguous. That's a **name collision**, and the move must protect those links too.

So `mv` resolves every link in the vault twice, with the notes as they are and as they will be, and rewrites exactly the links whose target would change, back to the note they meant. The new text keeps **the author's form** where it can (decided after the phase 4b test): a bare name stays a bare name (`[[alpha]]` → `[[omega]]`), a path keeps its length (`[[projects/kumbu]]` → `[[archive/kumbu]]`), so moving a note back restores the original links. Where the kept form wouldn't lead uniquely to the right note, and for relative links and links protected from a collision, it falls back to the **most specific form**, the note's path in its vault without `.md` (`[[projects/kumbu]]`), which can't be ambiguous. Heading, alias and `!` stay; every other byte of the note stays as it was. Each changed note is a normal write (compare-and-swap, history), so a link rewrite can be undone like any edit.

The move and its link rewrites are one transaction (chapter 7): delete the old path only if nobody wrote in between, insert at the new path (the unique index refuses an existing note), and rewrite the linking notes, or none of it. The history travels with the note; a move changes no text, so the last writer stays who it was.

`mv --keep-links` skips the rewriting. The Obsidian plugin will use it: Obsidian rewrites links itself when a file is renamed and uploads those edits, so a second rewrite on the server would collide with them.

## 6. Encryption at rest: vault keys and key slots

### What it protects, and what not

The threat is a **leak**: a database dump, a stolen backup, a hoster's disk (concept §3). After step 5 such a copy shows random vault ids, keyed hashes of paths, revision numbers, sizes and times. No content, no file names, no vault names.

It does not protect against whoever runs the server while it runs: to search a note, the server has to read it. Until step 8 the dev token unlocked every vault and sat in the server's configuration, so the database plus that file was everything; since then the dev token opens only the seeded vaults it has slots in, and a user's vaults open only with that user's credentials (chapter 10). Being honest about this is part of the design.

### Sealing: AES-GCM with a context

Every piece of data is **sealed** with AES-256-GCM, an **authenticated** cipher: besides encrypting, it computes a tag over the ciphertext, and decryption fails if a single bit was changed. Each seal uses a fresh random 12-byte **nonce**, so the same note sealed twice looks completely different.

GCM can also authenticate **associated data** that is not encrypted. Kumbu² puts a context there: what the blob is and where it belongs, e.g. `note:<vault id>:<document id>`. A blob only opens in its own context. Someone with write access to the database could otherwise copy a sealed note from one document into another, or from one vault into another, and the server would show it without noticing. With the context, that copy simply fails to open.

### One key per vault, wrapped in slots

Each vault has its own random 32-byte **vault key**. It is never stored as it is. Instead, a **key slot** stores it **wrapped**: sealed with a key-encryption key (KEK) derived from a credential. Step 5 had two kinds of slot (chapter 10 adds the user key, sessions and API keys; step 10 app connections):

- **API-key slot** (the dev token): the KEK is `HKDF(token, "kumbu/v1/key-kek", salt = vault id)`. To find the slot without storing the token, the slot carries a second derivation, `HKDF(token, "kumbu/v1/key-lookup")`. Different labels make two unrelated keys from one secret, so knowing the lookup value says nothing about the KEK.
- **Recovery slot**: the same with the vault's recovery key.

This is how disk encryption like LUKS works: many slots, one key. Revoking a credential means deleting its slot (step 8); changing a credential means adding a new slot with the help of another one. Nothing has to be re-encrypted, because the vault key stays the same. (Step 10 added re-keying anyway, for the case where an old copy of the database is in the wrong hands: revoking a slot doesn't reach that copy; `docs/security-architecture.md` §3.)

**HKDF** (HMAC-based key derivation) turns a secret that is already random into keys. It is fast, which is fine for random tokens and keys: there is nothing to guess. Passwords are different: people choose guessable ones, so deriving a key from a password must be deliberately slow and memory-hungry. That is what **Argon2id** is for (chapter 10).

### Paths in the dark: keyed hashes

Encrypting paths has a price: MongoDB can no longer find a note by its path or keep two notes from having the same one. Kumbu² gives each document a **keyed hash** of its path, `HMAC(path key, "/Work/plan.md")`, where the path key is derived from the vault key. The same path always gives the same hash, so MongoDB can enforce uniqueness and look things up, but without the key the hash says nothing, not even whether two vaults hold the same path.

Everything else that needs the paths (listing, `rg`, links) works on the **working set**: when a vault is first used, the server decrypts all of it into memory and answers from there. Writes still go to MongoDB first, as one transaction (chapter 7), and update the working set afterwards. A vault that hasn't been used for 15 minutes is locked again: the server forgets its key and its plaintext. Above a memory limit, the least recently used vault goes first.

Every request derives the lookup value from its own token and only gets the vaults that token has a slot for. An unlocked vault in memory saves the decryption, never the check.

### The recovery key

If the dev token is lost, and with it every API-key slot, the recovery key is the way back in. It is created with the vault and written once to a file for the operator to collect (`/recovery` in the container); it is never logged, because logs are kept and copied. Starting the server with `Kumbu:Recovery:Keys` gives the current token a slot in those vaults. Without either the token or the recovery key, a vault is lost for good, for the operator too. That is the point.

### No migration

Databases from before step 5 hold plaintext. Rather than carrying migration code forever, the server refuses such a database with a clear message, and the development data is seeded again, encrypted.

## 7. Many notes at once: diffs, plans and transactions

### Diff: the shortest edit script

`diff` answers "what did this write change?". Two texts are split into lines, and the question becomes: which lines must go and which must come to turn one into the other, with as few changes as possible? **Myers' algorithm** (1986, the one `git diff` uses) finds such a shortest edit script. It walks the edit graph diagonal by diagonal, where a diagonal step is a line both texts share and costs nothing, and stops at the first path that reaches the end. Kumbu² first trims the lines both versions start and end with, so a small change in a long note costs almost nothing.

The output is the **unified diff** format: `@@ -12,3 +12,4 @@` says the hunk covers 3 lines from line 12 of the old text and 4 from line 12 of the new one, followed by context lines (` `), removed (`-`) and added (`+`) lines. Three lines of context, as in git.

Markdown paragraphs are often one long line, so one changed word marks the whole paragraph. `--word` runs the same algorithm again on the words of the changed lines and marks them inline, `[-old-]{+new+}`, as `git diff --word-diff` does.

### Plan tokens: apply what was shown

A change across many notes is the one place where an agent's mistake is expensive to undo by hand. So `replace` is two calls. The preview lists every note it would change and returns a **plan token**: a hash over the call (path, old text, new text, word) and the version of every listed note. The server keeps the plan for an hour. The apply names the token and gets exactly that plan: if any of its notes has another version now, nothing is written. A note added after the preview is not touched, since the preview didn't show it. The idea is the version token's (chapter 3), lifted from one note to a set of notes: *what you saw is what you change*.

### Transactions, and why MongoDB needs a replica set for them

Until step 6 every write touched one document, and a single-document write is atomic in MongoDB. A folder move or a `replace` touches many, and must not stop halfway. A **transaction** groups writes: they all become visible at once when it **commits**, or none do when it **aborts**. Kumbu² runs each change in one: every compare-and-swap, every insert, every move record. If one of them is refused (a note changed, a target exists), the transaction aborts and the working set in memory stays as it was. A cancelled request aborts too; before transactions, a cancellation between two writes needed special care.

MongoDB offers transactions only on a **replica set**, because they are built on the replication log (the oplog). A replica set with a single member is still a replica set: no extra container, no extra cost. The trap is its configuration, which stores each member's host name. Docker's default host name is the container id, which changes when the container is recreated, and the node then no longer recognises itself as a member. `compose.yaml` therefore sets `hostname: mongo`, initiates the set with that name from the health check on first start, and the server connects with `directConnection=true`, so it talks to that one node without caring about names in the configuration.

## 8. Changesets and named checkpoints

### Changesets: one version per session, per note

Chapter 3 coalesced writes per note: the same writer within 10 minutes of that note's last write. Two things didn't fit. Each tool decided for itself (`replace` and the link rewrites of `mv` always made a version of their own), so a series of ten renames on one glossary note filled its history with renames and pushed out the state before them. And "a session" was really about the writer, not one note.

A **changeset** is everything one writer writes close together in time: it starts with a write and stays open while that writer writes again within 10 minutes, for an hour at most. A person's editor (the web app, Obsidian) saves every few seconds, so for a session or a device key the window is 2 minutes; otherwise an hour of typing would leave one version. Each note gets **one version per changeset**, whichever tool wrote it. The open changeset is derived from the notes themselves: every state carries its changeset's id and start time, and the writer's most recent write in the vault tells whether its changeset is still open. There is no session to keep track of, and a restart changes nothing.

Each history entry records its changeset id. Today that shows in `history <folder>`, grouping a session's writes across notes; later it allows undoing a whole changeset at once.

The state a note is created with is never coalesced away: the next write, even in the same changeset, keeps it as a version. A conflict copy's first state is the evidence of the conflict.

Two kinds of write never join a changeset: a **restore**, which is a deliberate step back and must stay undoable on its own, and **seeding**. In Git terms a changeset is a commit that is squashed while the session lasts.

### Named checkpoints: a tag that holds its content

The history keeps 10 versions per note, and that's the point: it is a safety net, not an archive. Sometimes a state should be kept for longer, "before the big cleanup". A **named checkpoint** (`mkcp /Demo before-cleanup`) is that state under a name, for a note, a folder or a whole vault, kept until `rmcp` removes it. In Git terms it is a tag. Git can keep a tag as a mere pointer, since it keeps every commit. Kumbu² keeps only 10 versions, so a checkpoint has to hold its content itself.

It does so cheaply: each content is stored once per vault, under a keyed hash of the text (**content addressing**). Ten checkpoints of a vault where most notes never change cost little more than one. Removing a checkpoint removes the texts no other checkpoint still needs. Name, paths and texts are sealed like everything else (chapter 6). The id of a text is an HMAC of its content, which makes it tamper-evident: a changed text no longer matches its id, and the whole checkpoint refuses to open rather than silently missing a note.

A checkpoint is read like the current state: `ls`, `cat`, `rg` … with `--checkpoint <name>` run unchanged on a read-only view of the checkpoint's notes. `diff --checkpoint` compares; `restore --checkpoint` goes back. For a folder, restoring means the folder matches the checkpoint exactly afterwards, so notes made since go to the trash. That changes many notes, so it uses the same preview and plan token as `replace` (chapter 7), applied in one transaction. The plan registry also remembers what became of each plan for its hour: a client that lost the answer to an apply learns that it was applied, instead of hearing "unknown".

Agents set and remove checkpoints only when the user asks; the manual says so. A checkpoint is a user's decision about what matters, and with 10 per vault, an agent setting them on its own would fill the slots the user wants.

## 9. Search by words and by meaning

### Two signals, one score

`rg` answers "where does this text occur?" exactly and without ranking. `semsearch` answers "which sections are about this?", and needs two different signals:

- **Words** (lexical): **BM25** scores a section by the query's terms. A term counts more the rarer it is in the vault (inverse document frequency), a repeated term counts less each time (saturation, `k1`), and long sections are not favoured for being long (length normalisation, `b`). Good for names, abbreviations and exact terms ("Hecate", "MCP"). Stop words ("the", "und") are left out: they match everywhere and say nothing.
- **Meaning** (semantic): an **embedding model** turns a text into a vector (768 numbers for the default model), so that texts about the same thing lie close together, also in other words or another language. The score is the dot product of the query's vector with a section's (both have length 1, so it is the cosine of the angle between them).

The two scores aren't comparable: BM25 has no upper bound, a cosine lies between −1 and 1. Step 7 first merged the two lists with **Reciprocal Rank Fusion** (RRF), which sidesteps the question by using ranks only: each list gives a section `1 / (60 + rank)`, and the sums decide. A section high in both lists wins; a section high in one still shows up. It is a well-known, robust recipe, and it lost here.

**Why RRF lost.** Because it counts ranks only, a weak list weighs as much as a strong one. Think of two advisers, one who knows the subject and one who skims for keywords: RRF lets them vote equally, even when the second has nothing useful to say. BM25 nearly always returns *some* list. For „Warum ist die Datenbank im September ausgefallen?" it finds `September` in a heat pump log and `Datenbank` in a budget, and a section that is mediocre in both lists beats the incident note that is first by meaning and absent from the word list (the note is in English). On the evaluation of the phase 7 notes (40 queries, 39 notes), meaning alone found the right section first for 0.81 of the queries with an answer; RRF only for 0.70. Of the 7 queries the merge made worse, 6 were across languages or needed world knowledge, exactly where words can't help.

**What replaced it.** The meaning decides, and the words add a small bonus: `cosine + 0.1 × (the share of the query's distinct terms found in the section)`. A section with all the terms gains 0.1, one with none gains nothing, and a strong match by meaning is never outvoted by a coincidence of words. On the same queries this found the right section first for 0.84 (0.70 with RRF); with the larger model it matches meaning alone. The 0.1 was set once, beforehand, not tuned on the queries. The measurement is in `docs/notes-test-phase7.md` ("Fusion"), and `tools/search-eval` repeats it, so a change to ranking, chunking or model is judged by numbers.

**Scores are shown** after each result (`score 0.47`; not with `--words`, where BM25 scores mean nothing to a reader). They are comparable within one search, where they show how fast relevance falls off. They are not comparable across searches, and no absolute threshold works: the best cosine of a query *with* an answer was as low as 0.23, and the three queries *without* one scored 0.18, 0.26 and 0.42 ("Kubernetes autoscaling configuration"). A cut-off by score would drop right answers before wrong ones, so the list is not cut; the reader sees the drop instead.

### Chunks: sections with their context

What gets ranked is a **section's own text**: from its heading to the next heading of any level, so a subsection is its own chunk and a result can be read with `cat "note.md#A#B"`. Long sections are split between paragraphs at about 512 tokens. Each chunk is embedded together with its context, the note's name and the heading path (`plan > Architecture > Gates`), since a section called "Risks" means little without it.

### The model, in-process

The default model is `snowflake-arctic-embed-m-v2.0` (Apache-2.0), multilingual, run by **ONNX Runtime** inside the server process: no note leaves the server to be embedded (concept §6), unless its vault's owner opted in to an external embedding provider that the admin configured; then that vault's sections and every search over it are sent there (README, "External embedding providers"). It is a transformer, so text first becomes **tokens**, pieces of words from a vocabulary of 250,002. Its tokenizer is a **Unigram** model (SentencePiece): every piece has a probability, and a word is split into the most probable sequence of pieces, found by **Viterbi**, a dynamic programme over the positions in the word. Before that comes a normaliser (`nmt_nfkc`, stored as a compact trie that maps characters to their normalised form) and a split at whitespace with "▁" marking a word's start. Kumbu² implements these steps in C# from the model's `tokenizer.json`; a test compares its ids with Hugging Face's library on sample texts in many scripts.

Queries get the model's query prompt (`query: `), sections don't: the model was trained that way, as an asymmetric search from a short question to a longer text. The model is downloaded on the first start, pinned to one revision and checked against SHA-256 checksums, and 16-bit (half the size of 32-bit, with almost the same vectors; an 8-bit version was faster but moved the vectors too far).

### Choosing the model, and changing it

The server doesn't take any model name from Hugging Face. It knows a short **catalogue** of two, `arctic-m` (default) and `arctic-l` (larger: better at questions in other words, about twice the memory and time per section), chosen with `Kumbu:Search:Model` / `KUMBU_SEARCH_MODEL`. Each entry pins a **revision** (like a Git commit hash, not a branch name that can move), a SHA-256 checksum per file, and the query prompt and vector size (both models export the pooled sentence vector the server reads). A free id would mean running whatever that name points to today, unmeasured and unchecked; with the catalogue, the checksums keep a foreign model out, and the evaluation covers exactly what runs. Only the chosen model is downloaded.

Each note's embeddings **record which model made them**. Vectors of two models live in different spaces (768 and 1024 numbers): comparing one with the other is like comparing a position in metres on one map with a position on another map. So they are never mixed. After a switch, the old embeddings count as missing, and the indexer re-embeds them in the background, the same way it embeds a note that has none; sections not yet done are found by words only, and the search says how many notes aren't indexed yet. The old vectors are dropped as the new ones arrive.

**Encryption at rest needs nothing new here.** Embedding needs the plain text, so it only happens in an unlocked vault, as it always did. A locked vault is re-embedded when it is next opened, and until then nobody can search it anyway.

### The index

There is no vector database. A vault's embeddings are sealed in MongoDB like everything else (one document per note, the vectors as 16-bit floats), decrypted into the working set when the vault is unlocked, and a search is dot products over them: a personal vault has thousands of sections, which takes milliseconds.

Indexing runs **in the background**, so a write isn't slowed down by it: each write queues its notes; a worker splits them into chunks and embeds only the chunks whose text changed (each chunk carries a hash of its text). When a vault is unlocked, it catches up with everything written while it was locked. A search says how many notes aren't indexed yet; those still count by words.

### Tags as a filter

A question like "what did we discuss about modelling capabilities in #research?" names a topic that comes up in many projects, and a tag that says which one is meant. Ranking can't tell the projects apart (the sections are about the same thing), so the tag works as a **filter before the ranking**: `semsearch --tag research …` ranks only the notes that carry `#research`, by exactly the scores it would give them without the filter. Narrowing changes which notes take part, never how a section scores against the query, so a result's score means the same with and without a tag. `rg --tag` narrows the same way, and `tags` lists what there is to narrow by.

Tags are read as Obsidian reads them, since the vaults are Obsidian's: the frontmatter's `tags` property and `#tags` in the text, but not in code, comments or a URL's `#fragment`, and not `#123` (an issue number, not a topic). They match ignoring case and **nested**: `#project` covers `#project/kumbu`, so a parent tag is a filter over its whole family. A tag no note carries is an error, not an empty result: a misspelt filter that quietly found nothing would look like an answer.

**Read, not indexed.** Tags aren't stored anywhere: they are read from the notes an unlocked vault already holds in memory, as `links` and `backlinks` read links (§5). An index would be one more thing to keep in step with every write, and one more encrypted document per vault, for a question the working set answers by itself. What it does need is a **cache**: reading every note of a 2,000-note vault takes about 100 ms, too much for each search. So a note's tags are remembered with the text they came from, in a weak table keyed by the very string the working set holds for the note's current version: a new version is a new string and is read afresh, and the old entry goes when its string does, with nothing to invalidate by hand. A second pass over the same vault takes under a millisecond.


## 10. Accounts: callers, the user key, sessions and API keys

### Every request is one caller

Chapter 6 ended with one credential, the dev token. Step 8 adds two more, and the server treats all three alike: a request under `/mcp` or `/api` is resolved to exactly one **caller**, or refused.

- A **session**: the cookie `kumbu_session` of a signed-in person in the web app. Accepted under `/api` only. A cookie is something a browser sends on its own; on `/mcp` a page on a sibling subdomain could make the browser post a tool call with the user's session without the user noticing. MCP clients send a header they set themselves, which a web page can't forge.
- An **API key** (`Authorization: Bearer kumbu_…`): a user's key for an agent or a script, with rights on chosen vaults.
- The **dev token**, if the operator configured one: what step 5 had. On a public instance it is left out, and then it opens nothing.

The point of "exactly one" is that everything after it asks the same question: *which vaults does this caller hold a slot for, and with which right?* The notes code doesn't know whether a person or an agent is asking. The middleware also fails closed: it guards every path under `/mcp` and `/api`, matched without regard to case, and a few endpoints (sign-in, joining, the OpenAPI document) are explicitly marked anonymous. A new endpoint is guarded unless someone marks it otherwise, not the other way round.

Wrong bearer keys are counted per client network, 20 a minute. Only failures count, and once the budget is used up every bearer from that network is refused before it is checked, so a lucky guess can't be told from an unlucky one by the answer.

### The user key: credentials and vaults, independent

Chapter 6 wrapped each vault key in a slot per credential. That works for one token. With people it breaks: if a vault key were wrapped by the password, changing the password would mean re-wrapping every vault, and each new API key would need the password to be typed in.

So there is a layer between: the **user key**, a random 32-byte key made when the account is. The vault keys of a user's vaults are wrapped by the user key (one slot per vault), and the user key itself is wrapped by each of the user's credentials: the password (through Argon2id, 64 MiB, three passes, four lanes; slow on purpose, since passwords are guessable) and the **recovery key** shown once at sign-up. Think of a key cabinet: the vault keys hang in the cabinet, the user key opens the cabinet, and the password and the recovery key are two copies of its key, each in its own safe.

The reward is independence. A password change re-wraps one 32-byte key, not every vault. A new credential, a session or an API key, only needs the user key in hand to receive its own slot. And nothing but the user holds it: MongoDB has only wrapped copies, the server forgets the key after a request, and an admin has no credential that opens it. That is also why there is **no password reset**: a reset would be a way in that isn't the user's. The recovery key is the way back, and with neither password nor recovery key the vaults are gone. That is the point, as in chapter 6.

That makes a fresh recovery key as powerful as the password: whoever holds one can set a new password and lock the owner out for good. So making a new one asks for the **current password** too, not just a signed-in session, and the password must open the very user key the session holds. A stolen session (a laptop left open, a cookie copied) can then read and write the user's notes until it ends, but can't turn itself into the account. The check is limited like the old-password check of a password change, 5 a minute per username, shared with re-encrypting and deleting.

Passwords are normalised to Unicode NFC before Argon2 runs (the same password typed on two devices that compose "ö" differently must give the same key), have at least 12 characters, and are checked against a list of the 10,000 most common ones. Signing in as an unknown user, with a wrong password and as an inactive user all give the same answer and all run Argon2, so neither the answer nor the time says which usernames exist.

### Sessions are slots

A session needs the user key on every request, yet the server must not keep it. The answer is to make the session a slot like the others. At sign-in the server makes a random 32-byte secret and puts it in the cookie. In MongoDB it stores a slot: the user key, wrapped by a key derived from that secret, found again by a second derivation (the lookup, as in chapter 6). The server keeps neither the secret nor the user key. A request brings the secret, the server finds the slot, unwraps the user key, uses it, and forgets it.

Two things follow. A **server restart signs nobody out**, because the session lives in the database and the cookie carries the only secret that opens it; this differs from the unlocked vaults, which a restart does lock (chapter 6). And a **stolen database is no stolen session**: the slots are useless without cookies, which the database doesn't have.

A session ends after 30 minutes idle or 12 hours in all. The check runs on every request. MongoDB's own expiry sweep runs only about once a minute, so it merely tidies up.

**A sign-in racing a password change.** Sessions made before a password change must die with it, or a thief with the old password would stay signed in. Each user has a `credentialsChangedAt`; a session older than it is refused. But a sign-in takes a while (Argon2 runs for a good fraction of a second): it reads the password slot, hashes, and only then writes its session. If a password change lands in between, the new session would carry a user key unwrapped with the old password, yet look younger than the change. So the session's creation time is not the moment it is written, but the moment the **credential was read**, taken before Argon2. A change that lands meanwhile has a later time and refuses the session. The change stamps `credentialsChangedAt` both before and after it writes the new slot, to close the gap on both sides. Time stamps compare with "at most equal": MongoDB keeps milliseconds, and the harmless cost of a tie is signing in again.

### API keys carry their rights in the slots

An API key is `kumbu_` and 32 random bytes in Base62, shown **once**, when it is created. A user makes it for chosen vaults, each with *read* or *read and write*. For each of those vaults the key gets a slot, as the dev token does, with one difference: the slot seals the **vault key together with the right**, under a key derived from the API key.

That is the reason the right sits inside the slot and not next to it. If the right were a plain field in the database ("this key may read"), anyone who can edit the database could turn read into write with one update. Sealed together, the right is as protected as the key: changing it means unwrapping the slot, which needs the API key. A database edit can only delete a slot (the key loses the vault) or break it (the key opens nothing). The server then enforces the right it unwrapped: a read-only key is refused every write, including `replace` and the preview of a folder `restore --checkpoint`, which would otherwise plan a write.

MongoDB holds only values derived from the key: a lookup, the first eight characters as a **prefix** for the owner's list, and a sealed name and vault list that only the user key opens. The key's **name** is sealed once more under the API key itself, so that it can mark the key's writes in the history ("written by `claude-code`") without the server needing the user. A key's name therefore cannot be the username, `dev` or `seed` (their writes would look alike), contain " (" (the history writes the tool in brackets after the writer, as in "laptop (mv)", and a name must not fake that), control characters or invisible formatting characters such as a zero-width space or a right-to-left override ("chris" followed by a zero-width space would look like the username).

A key is created together with all its slots in one transaction, so it never exists with only some of its vaults. Revoking deletes its slots and nothing has to be re-encrypted: the vault key never changed. The flip side of that independence: a key holds its own slots and never needed the password, so nothing ties it to the password by itself. A key made from a stolen session would outlive a password change, which is the one thing a password change after a suspected compromise must not allow. So a password change revokes every key by default, and the settings page lets the user untick that for an ordinary change, when the agents should keep working. A recovery always revokes them, disconnects every app, gives the account a new user key and each vault a new key in the background, and replaces the recovery key as well: otherwise a thief of the recovery key and its owner could take the account from each other in turn, and the thief's keys would survive each round.

### Invitations: single use, and the code stays in the fragment

New users come in through an **invitation**, a link `https://…/join#<code>`. The code is 32 random bytes; the server stores only a keyed hash of it (HMAC with a key derived from a server secret), so it can check a code without keeping it. An invitation has a role, an expiry (7 days by default, 30 at most), an optional note, and can be revoked until used. Joining uses it up: single use means a leaked link works for whoever is first, which is why the lifetime is short and an admin can see and revoke what is open.

The code sits after the `#`, in the URL's **fragment**. A browser never sends the fragment to the server, so the code does not appear in the request line, in the proxy's access log, or in a `Referer` header. The page's script reads it and posts it in a request body. Logs get collected and kept; a bearer secret in a URL would be one of the things they keep. (Chapter 6 kept the recovery key out of the log for the same reason.) The server's own log follows the same rule for what the encryption hides: it names a vault by its id, never by its name, and logs no note path (seeding alone names the folders and files it reads, which the operator put there). A reverse proxy's access log is outside its reach, though: the REST API's request lines name the vault and the note (`/api/notes/Work/plan.md`) and carry search queries (`?q=…`), so a public instance turns that log off for `/api` or filters it.

### The first admin: a command, not the log

Someone has to make the first account, and it must not be the first stranger to find the server. A one-time code printed to the log at first start would be the obvious design, and a bad one: logs go to collectors, other people's dashboards and backups, and whoever reads the line becomes the primary admin.

So the first admin comes from a **command** that only someone with a shell on the container can run: `docker compose exec kumbu kumbu bootstrap-admin`. It makes an invitation for the primary admin, prints the link on standard output only, and is valid for 24 hours; running it again replaces the link. Once a primary admin exists it refuses to run. It needs `KUMBU_PUBLIC_URL`, since the link is built from the address users reach the server at. This is the same idea as the recovery key's file in chapter 6: a secret goes to the one person entitled to it, not into a stream that others read.

### What an admin can and can't do

Admins list users (name, role, number of vaults, bytes stored), invite, revoke invitations, change roles and deactivate people. There is exactly one primary admin, who can't be demoted, deactivated, or made by anyone else.

What they can't do is the other half of the design. They can't read a note: they hold no user key, and no vault is opened for the list; the figures come from what MongoDB shows in the clear. They can't reset a password (above). **Deactivating** a user ends their sessions and revokes their API keys in the same transaction as the flag; reactivating revokes once more, which catches a key that was being created at the moment of deactivation. Nothing comes back by itself: after reactivation the user signs in again and makes new keys.

### What the `owner` field tells a database reader

User slots carry the owner's user id **in the clear**, as the field `owner`. The admin's list needs to count each user's vaults and their bytes without any key, and MongoDB needs it for an index. So someone who reads the database (a dump, a backup, a curious operator) learns **which vaults belong to which user**, and with the sizes MongoDB shows anyway, how many vaults and how much storage each user has. They don't learn the vaults' names (those are sealed, and a user's vault names are kept unique by a keyed hash of the name) or anything in them.

This is consistent with the threat model (concept §3): the leak the encryption is for, a copy of the database, shows structure (counts, sizes, times) but no content, and the running server's operator is trusted anyway. The alternative, hiding the owner, would make the admin screen impossible without opening vaults, which is the one thing admins are meant not to do.

### Throttles that cost something

Signing in costs an Argon2 run, 64 MiB of memory for a moment, which an attacker could use to exhaust the server instead of guessing. So the account endpoints are limited per minute: sign-in 5 per username and 20 per client network, joining and recovering 5 per network. Every attempt counts, successful or not. The trade-off is deliberate: someone who knows a username can lock that user out of *signing in* for a minute (keys and live sessions are untouched). Checking the current password behind a session, for a password change, a new recovery key, re-encrypting or deleting, is limited to 5 a minute per username, so a stolen session guesses no faster than signing in would. The limits bound each client, not all of them together: many addresses signing in at once could still start many 64 MiB runs. So Argon2 itself runs **at most once per core** across the process, and further runs wait (asynchronously, holding no thread) for a free one; the server slows down instead of running out of memory. IPv6 addresses count by their /64, since one host usually has a whole /64 and would otherwise get 2^64 times the attempts. And behind a reverse proxy the server sees the proxy's address for everyone unless `KUMBU_TRUSTED_PROXY` is set, in which case one attacker's wrong keys lock out all bearer users for a minute. The README lists these as security notes.
