# A read-only console in the web app

Date: 2026-11-02. Status: approved by the maintainer.

A page in the web app where a signed-in person types what an agent would send to the `sh` tool, and reads the answer. It looks like a kind of shell without pretending to be bash. It only reads: nothing typed here changes a vault.

## 1. Why

- **Hand tests.** A test guide can hold commands to copy, with no LLM client in between, and the tester sees the same output and error sentences as an agent.
- **Power users** get a fast way to search and read their vaults by keyboard.

**Read-only by decision.** The console is meant to be a helpful tool, not one with which someone who doesn't know their way around ruins everything. A prompt that changes notes with a few keystrokes, under a session with the owner's full rights, could do that. What the console is for needs no writing, so nothing it is meant to achieve is lost. Notes are changed in the app's editor or by agents with a key of their own.

## 2. Goals, non-goals, anti-goals

Goals:

- Every reading `sh` command runs with its real output, through the real `Shell`.
- `cd` and `pwd`, with relative paths, while the server still only sees absolute paths.
- The console shows what it really ran, so a mistake in its path handling is visible at once.
- The server enforces the read-only rule, and a new shell command is refused until someone has classified it.

Non-goals:

- **Any way of writing:** no `file` command, no changes to the board, and `rm`, `mv`, `mkdir`, `restore`, `mkcp` and `rmcp` are refused. The `write`, `edit`, `replace` and `board` tools aren't reachable from here.
- `connect <key>` or any other way of acting as an API key.
- Tab completion, globbing, redirection, `&&`, `;`, aliases, scripting, variables.
- History across reloads.
- A layout made for phones. The page has to work there, nothing more.
- Testing the MCP layer, the write paths or a key's rights. The console can't do that (§4).

Anti-goals, which make this a failure even if it works:

- **A write through the console,** by whatever spelling, pipeline or quoting.
- **The session cookie on `/mcp`.** It is refused there on purpose (`src/Kumbu.Server/Auth/CallerMiddleware.cs:97-99`). This feature must not change that.
- **Anything an agent sees changing.** `Manual`, the tool descriptions, `help`, the hints and every error sentence of the shell stay byte for byte as they are.
- **Commands or note content in browser storage.** `docs/security-architecture.md:226` lists what the browser keeps, and this adds nothing to it. Search queries and paths can be as private as the notes.
- **A credential made behind the user's back.** The console creates no API key and no connection.

## 3. What it looks like

The route is `/console`, a lazily loaded page like the others in `web/src/router.tsx:119-126`, linked from the account links in `web/src/shell/TreeDrawer.tsx:260`, next to Access and Settings, for every signed-in user.

On opening:

```
Kumbu² console, not a bash, and read-only. Type help for the commands, help <command> for details.

/
>
```

After some use:

```
/Gartenarbeit/Shopping
> rg -n Tomaten plan.md
→ rg -n Tomaten /Gartenarbeit/Shopping/plan.md
/Gartenarbeit/Shopping/plan.md:12:Tomaten, zwei Sorten
```

Decided:

- **The prompt has two lines:** the working directory, then `> `. It always shows the full path, the vault included.
- **Colour:** the vault segment in the accent colour, the rest of the path muted. It must read in the light and the dark theme, and nothing depends on colour alone.
- **Scrollback:** the path line is repeated only when the directory changed. It stays visible above the input. The `→` line is quiet, and `>` and `→` line up, so the two commands can be compared.
- **The `→` line** appears for every shell command that goes to the server, identical to what was typed or not, and can be pasted as it is into a hand-test guide or an `sh` call.
- **The working directory starts at `/`.** A vault is the top directory, so `cd /Gartenarbeit` is how one is chosen; there is no separate command.
- **Plain DOM:** an input and a list of output. No terminal emulator such as xterm.js, so escape sequences in a note can't do anything.

## 4. How it reaches the server

The web app can't speak MCP (§2), so there is one new endpoint, **`POST /api/console`**: session-only (`SessionOnlyMetadata`), so an API key gets a 403 like on the other session-only endpoints. It is a `POST` so that commands stay out of URLs, and it comes under the existing `Origin` check.

It takes one call and allows exactly these:

| Call | Goes to | Fields |
|---|---|---|
| `sh` | the read-only check (§5), then `Shell.RunAsync` | `command` |
| `cd` | see §6 | `path` |

- **Shared code.** `src/Kumbu.Server/Mcp/ToolResult.cs:68-100` turns vault exceptions into results. Extract that so that the console and MCP share it instead of copying it; MCP's behaviour stays the same. The console's answers then say what an agent's would.
- **The answer** is `{ parts: [{ type: "text", text } | { type: "image", mediaType, data }], isError, version?: { path, token } }`, with 200 also for a failed command, as in MCP. Authentication, limits and a busy vault keep their usual statuses. A version token is shown as MCP shows it: `version of <path>: <token>` on its own line (`ToolResult.cs:24`).
- **The caller is the session.** All rights checks stay where they are, so a member sees only what their membership allows.

**What this costs, on purpose:** the console tests the shell's reading commands, not the MCP layer, the write paths or a key's rights. An owner's session has full rights, so a `read` key can't be imitated. A hand test of those still needs a real MCP client. The README says so.

## 5. Read-only, and the rest of security

**The rule is enforced on the server, by an allowlist.** The endpoint parses the line with the shell's own `Pipeline.TryParse` and looks at every stage's command name:

- A command the shell has and the list doesn't name is refused, whichever stage it stands in, with one sentence: `rm: not in this console, which only reads`. Nothing of the line runs, and that also goes for `rm --help` (`help rm` works).
- A name the shell doesn't have, and a line that doesn't parse, go on to `Shell.RunAsync`, so that the error is the shell's own.

The list: `ls`, `cat`, `outline`, `rg`, `semsearch`, `img`, `pdf`, `links`, `backlinks`, `tags`, `board`, `head`, `tail`, `wc`, `sort`, `history`, `diff`, `trash`, `lscp`, `uname`, `help`.

Refused: `restore`, `mv`, `mkdir`, `rm`, `mkcp`, `rmcp`.

The list lives in the server, not in the shell, and the shell needs at most a way to say which commands it has. A test classifies every command the shell registers as allowed or refused and fails for one that is neither, so a new command needs a decision. Each refused command is also tried through the endpoint, alone and in a pipeline, and the vault is checked to be unchanged. `--checkpoint` reads and stays allowed; `diff` and `history` read.

The web app knows nothing of this rule beyond its help text. Its path handling is comfort.

| Point | Rule |
|---|---|
| Rights | No new ability: less than what a session can already do through the REST API. |
| Password window | No guard: reading needs no fresh password. Add `POST /api/console` to `DeliberatelyOpen` in `tests/Kumbu.Server.Tests/Api/PasswordWindowTests.cs:126`, which fails until it is there. |
| Bulk reading | `history` on a folder reads every note below it (`src/Kumbu.Core/Sh/Commands/History.cs:117`), the cost the `HistoryScan` limit exists for (`src/Kumbu.Server/Api/HistoryApi.cs:285`). A console `sh` call with `history` in any stage of its pipeline takes one permit, and over the limit answers 429 as those endpoints do. |
| Output | Text only, as React text nodes. No Markdown rendering, no raw HTML: notes and file names in shared vaults come from other people. Images only from `image` parts, shown through `data:` or `blob:` URLs, which the CSP allows. |
| Storage | History, working directory and scrollback live in memory only, in a store outside the page, so they survive moving around in the app. They are gone on reload and are cleared when the session ends or the user signs out, so the next user of the browser finds nothing. |
| Logs | Neither the command nor any output is logged. Metrics keep to the existing label rule; extend `tests/Kumbu.Server.Tests/Metrics/LabelGuardTests.cs` with a marker typed through the console. |
| Body size | Limit the request to 64 KiB: it only ever holds a command line. |
| Path rewriting | Comfort, never a boundary: the server checks every path as for any other caller. |

`docs/security-architecture.md` is updated in the same pull request: the endpoint and its read-only rule in §4 and §6, the limit in §7, with their code references.

## 6. Commands that the console answers itself

| Command | Behaviour |
|---|---|
| `cd [path]` | No argument goes to `/`. `.` and `..` work, and `..` at `/` stays at `/`. There is no `~` and no `cd -`. The `cd` call asks the server whether the path is a vault or a folder, empty folders included; for a note, an attachment or nothing there: `cd: <path>: not a folder`, and the directory stays. |
| `pwd` | Prints the working directory. |
| `clear` | Empties the scrollback. |
| `help` | The console's own block, then the server's `help`, marked clearly (below). |
| `help cd`, `pwd`, `clear` | Local text. |

The manual agents get says "All paths are absolute", lists commands that change things and names the write tools. It must stay as it is, so the console doesn't edit it. It puts its block first:

```
In this console:
  cd [path], pwd, clear
  Relative paths work here; the console turns them into absolute ones, and → shows the result.
  It only reads: restore, mv, mkdir, rm, mkcp and rmcp are refused, and the write tools aren't here.

The manual agents get, unchanged:
```

The shell's sentences stay in the agents' words everywhere, also in errors ("use the write tool"). That is part of seeing what an agent sees.

A local command can't be part of a pipeline: `cd: can't be part of a pipeline`. `help | head` is simply passed to the server.

The directory can stop existing, or the user can lose the vault, after `cd`. The console doesn't check that in advance; the next command's error says it, and `cd /` always works.

## 7. Relative paths

The console tokenises the line, finds the paths, makes them absolute and sends a normal command line. It needs a table with one entry per allowed command.

Rules:

- A path argument that doesn't start with `/` is relative to the working directory; `.` and `..` segments are resolved. A `#section` stays attached and untouched.
- Option values and everything after `--` follow the command's own options (`Options.TryParse`, each command's `Spec`), so `head -n 5 plan.md` doesn't take `5` for a path.
- **Optional paths default to the working directory** when the command starts the pipeline. At `/` nothing is added. Decided for `rg` in particular: without a path it searches the working directory; to search everything, `rg foo /`.
- **Right of a pipe** nothing is added: `cat x | rg foo` reads the pipe. An explicit relative path is still resolved.
- A line the console can't tokenise (`>`, `&&`, an open quote) goes to the server unchanged, so its error is the shell's. So does a line with a command the table doesn't know, whether the shell's error or the refusal of §5 answers it.
- Output keeps absolute paths as the server wrote them.

| Command | Paths | Without a path |
|---|---|---|
| `ls`, `trash`, `lscp` | the one operand | working directory |
| `rg <regex> [path...]` | all operands after the regex | working directory |
| `rg --files [path...]` | all operands | working directory |
| `semsearch <query> [path]` | the last operand, only if it starts with `/`, `./` or `../`: an unquoted query has many words (`semsearch how do agents read notes`, `src/Kumbu.Core/Sh/Commands/Semsearch.cs:68`), so `semsearch Tomaten plan.md` searches for two words | working directory |
| `tags [tag] [path]` | second operand; a sole operand counts as a path only if it starts with `/`, `./` or `../` | working directory |
| `history <path>` | first operand | working directory |
| `board [vault]` | first operand | working directory's vault |
| `cat`, `outline`, `links`, `backlinks`, `img` | first operand | required |
| `pdf [info] <path> [pages]` | the operand after an optional `info` | required |
| `head`, `tail`, `wc`, `sort` | the one operand | nothing added: without a path they read the pipe |
| `diff <path> [x [y]]` | first operand; `x` counts as a path if it contains `/` or `.`, else as a version token | required |
| `uname`, `help` | none | |

**Keeping it in step:** a test must fail when the allowlist of §5 holds a command the table doesn't know, and the console's tokeniser must agree with `src/Kumbu.Core/Sh/Tokenizer.cs`. One way, the builder's to choose: a JSON file in the repository with the allowed command names and with lines and their tokens, read by an xUnit test and a Vitest test alike. The `→` line must quote its words so that `Tokenizer` reads back the same tokens.

## 8. Input behaviour

- Moving to another page and back keeps everything (§5); a running command is aborted on leaving.
- Enter runs. Up and down walk through the history, in memory, at most 200 lines, without adjacent duplicates. An empty line does nothing and isn't recorded.
- One command at a time; the input waits for it.
- Scrollback keeps the last 500 commands. A single answer is already limited to about 8000 tokens by the shell.
- The output stays selectable, and the page scrolls to the newest answer unless the user scrolled up.
- Errors look different from output by colour and also by a marker, not by colour alone.
- A 401 is handled by the app as everywhere; a 429 or 503 is printed as a line with its sentence.

## 9. Testing and documents

- **Server:** the endpoint's calls and their answers; an API key refused; the read-only rule of §5, as spelt out there; the history limit; errors equal to those MCP gives for the same input; `cd` on a vault, a folder, an empty folder, a note and nothing; a member's view of a shared vault.
- **Agents unchanged:** `help`, the tool descriptions and `sh` over MCP, the commands refused here included, as they were.
- **Web:** the table of §7, every row and the pipe rule; tokeniser agreement; `cd`, `pwd`, `clear`, history; nothing written to local or session storage; a note's HTML shown as text; state cleared at sign-out.
- **README:** what the console is, that it only reads, what it can't test (§4). No new environment variables.
- **`help`:** the local texts of §6 in British English.
- **Hand-test guide** in the pull request, with steps, where to look and what passes.

## 10. Left to the builder

- Where the code lives and how the shared part is extracted.
- The page's styling within the app's design and the mentioned colour rule; which colour tokens.
- The exact wording of the local help and of local errors, within the above.
- The form of the check in §7.
