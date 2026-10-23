# Concepts

> **Draft by Claude — Chris rewrites each chapter in his own words.**

## 1. Talking to a model through MCP

### What MCP is

MCP (Model Context Protocol) is a way for an AI application to use outside capabilities. There are two sides. The **client** is the AI application (Claude Code, for example). The **server** is a program that offers capabilities. The most important kind is a **tool**: a named function with a description and typed arguments. The model reads the description, decides to call the tool, and the client sends the call to the server. Messages are JSON-RPC 2.0.

Kumbu² is a server with one tool, `sh`.

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

Two characters stay ordinary on purpose. `*` is not a glob, because globs need a working directory to make sense. `#` does not start a comment, because `note.md#Heading` will address a section (concept §4).

### Searching with `rg`

`rg` is ripgrep's command name, and models know its output: `path:line:text` for a match, `path-line-text` for context lines, and `--` between groups that are not adjacent. Kumbu² copies that layout, so a model can read it without instructions. Without a path it searches every vault (there is no working directory to default to). Right of a pipe it filters lines instead and prints them bare, as ripgrep does with standard input.

`rg --files` lists note paths. Together with a filter it answers a question the first test raised: where does `[[kumbu]]` live? `rg --files | rg -i kumbu` prints `/Demo/projects/kumbu.md`. Proper link resolution comes in step 4; until then this is enough.

### Regular expressions that cannot hang the server

A regex comes from the model and runs on the server. That is a classic attack surface called **ReDoS** (regular expression denial of service). Most regex engines, .NET's default included, work by **backtracking**: they try one way to match, and when it fails they step back and try another. For some patterns the number of ways explodes. `^(a+)+$` against `aaaa…a!` tries every way of splitting the a's into groups before giving up: 2ⁿ attempts for n characters. A few dozen characters are enough to keep a CPU busy for a very long time.

The alternative is to compile the regex into an **automaton** (a state machine) and walk the input once, tracking every possible state at the same time. Time is then linear in the input, whatever the pattern. Rust's `regex` crate, which ripgrep uses, works this way. So does .NET with `RegexOptions.NonBacktracking`, which Kumbu² uses (plus a timeout as a backstop).

The cost: features that need to remember and revisit, such as look-around (`(?=…)`) and backreferences (`\1`), are impossible. ripgrep doesn't have them by default either, so models rarely expect them. If one is used anyway, the error says why it is missing.

### Errors and `help` as teaching

The tool description is sent to the model every time it connects, so every line of it costs tokens in every conversation. It therefore stays a short man page: the commands, one line each, plus the rules that matter on every call. The details (every option of `rg`, examples) live in `help <command>`, which the model only fetches when it needs them. This is called **progressive disclosure**. `rg --help` works too, because models try that first.

Error messages do the same job. `command not found: rm — try 'help'` names the next step. For the commands models reach for most often, the hint is specific: `grep` points to `rg`, `find` to `rg --files`, `cd` to "there is no working directory". Every error should let the model fix its next call without guessing; the phase 1 test showed that this works.

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

An agent working on a note often saves several times in a row. Keeping every intermediate step would push the interesting state, the one before the agent started, out of the 10 slots. So writes by the **same writer within 10 minutes coalesce**: the state they replace is not kept again. What survives is the state before the session. Today every MCP call writes as `dev`; with API keys (step 8) each key is its own writer, so two agents never coalesce into each other.

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

It does not protect against whoever runs the server while it runs: to search a note, the server has to read it. And until users and API keys arrive (step 8), the dev token unlocks every vault and sits in the server's configuration, so the database plus that file is still everything. Being honest about this is part of the design.

### Sealing: AES-GCM with a context

Every piece of data is **sealed** with AES-256-GCM, an **authenticated** cipher: besides encrypting, it computes a tag over the ciphertext, and decryption fails if a single bit was changed. Each seal uses a fresh random 12-byte **nonce**, so the same note sealed twice looks completely different.

GCM can also authenticate **associated data** that is not encrypted. Kumbu² puts a context there: what the blob is and where it belongs, e.g. `note:<vault id>:<document id>`. A blob only opens in its own context. Someone with write access to the database could otherwise copy a sealed note from one document into another, or from one vault into another, and the server would show it without noticing. With the context, that copy simply fails to open.

### One key per vault, wrapped in slots

Each vault has its own random 32-byte **vault key**. It is never stored as it is. Instead, a **key slot** stores it **wrapped**: sealed with a key-encryption key (KEK) derived from a credential. Today there are two kinds of slot:

- **API-key slot** (the dev token): the KEK is `HKDF(token, "kumbu/v1/key-kek", salt = vault id)`. To find the slot without storing the token, the slot carries a second derivation, `HKDF(token, "kumbu/v1/key-lookup")`. Different labels make two unrelated keys from one secret, so knowing the lookup value says nothing about the KEK.
- **Recovery slot**: the same with the vault's recovery key.

This is how disk encryption like LUKS works: many slots, one key. Revoking a credential means deleting its slot (step 8); changing a credential means adding a new slot with the help of another one. Nothing has to be re-encrypted, because the vault key stays the same.

**HKDF** (HMAC-based key derivation) turns a secret that is already random into keys. It is fast, which is fine for random tokens and keys: there is nothing to guess. Passwords are different: people choose guessable ones, so deriving a key from a password must be deliberately slow and memory-hungry. That is what **Argon2id** is for, and it comes with passwords in step 8.

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

