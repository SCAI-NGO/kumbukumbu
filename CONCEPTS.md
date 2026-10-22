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

An agent working on a note often saves several times in a row. Keeping every intermediate step would push the interesting state, the one before the agent started, out of the 10 slots. So writes by the **same writer within 10 minutes coalesce**: the state they replace is not kept again. What survives is the state before the session. Today every MCP call writes as `dev`; with API keys (step 7) each key is its own writer, so two agents never coalesce into each other.

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
