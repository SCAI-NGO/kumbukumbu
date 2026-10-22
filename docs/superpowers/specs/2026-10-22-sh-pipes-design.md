# `sh` Parser, Pipes and Filters — Design

**Date:** 2026-10-22
**Scope:** `docs/concept.md` §12, step 2, plus what the phase 1 test asked for ([notes-test-phase1.md](../../notes-test-phase1.md)).
**Status:** Written by Claude from the suggested next step in the phase 1 notes; Chris reviews it with the PR.

## 1. Intent

### From the phase 1 test
1. Wikilinks cannot be followed: nothing says where `[[kumbu]]` lives.
2. No way to search: in a large vault the only strategy is blind `ls`.
3. No `help`: the concept promises `command not found: rm — try 'help'`.

### Success criteria
- An agent finds a note by name (`rg --files | rg -i kumbu`) and by content (`rg -i hecate`) in one call each.
- Pipes work as a model expects from Unix: `cat /Demo/welcome.md | head -5`, `rg -l TODO | wc -l`.
- Every operator other than `|` is rejected with an error the model can act on.
- Unknown commands point to `help`; common Unix names (`grep`, `find`, `cd`) point to what we have instead.
- Parser and every command are covered by deterministic unit tests; one end-to-end test runs a pipe over MCP.
- `CONCEPTS.md` gets chapter 2.

### Out of scope
Writing (`edit`, `write`, `mv`, `rm`), link resolution and backlinks, `outline` and section addressing, encryption, semantic search, permissions per command.

## 2. Decisions

| Topic | Decision | Why |
|---|---|---|
| Lexer | The existing tokenizer emits words and a pipe marker. A quoted or escaped `\|` stays a word. | A regex like `'a\|b'` must survive; the shell, not the command, decides what is an operator. |
| Operators | Only `\|`. Unquoted `\|\|`, `&`, `&&`, `;`, `<`, `>`, `$(`, `${`, backticks, `(` and `)` are errors that name the operator and say "quote it to use it literally". | Concept §5. A real shell would treat them as syntax, so a model expects them to mean something; silently passing them through as text would hide its mistake. |
| No globbing, no comments | `*` and `#` are ordinary characters. | `#` must stay free for section addresses (`note.md#Heading`, step 4). Globs need a working directory to make sense. |
| Sources and filters | `ls`, `cat`, `help` only start a pipeline. `head`, `tail`, `wc`, `sort`, `rg` may stand right of a pipe. A filter at the start reads one note given as a path (`head -n 5 /Demo/welcome.md`). Right of a pipe a filter takes no path. | Concept §5 ("right of a pipe: filters only"). Accepting a path first is what models type anyway. Rejecting a path behind a pipe beats Unix's silent "the file wins". |
| Errors in a pipeline | The first stage that fails ends the pipeline; its message is the result. | Like `set -o pipefail`: the model sees the real cause, not an empty `wc`. |
| Lines | Text splits on `\n`; one trailing newline ends the last line instead of adding an empty one. A last line without a newline still counts. | Notes are LF (concept §4). Matches what people mean by "lines", differs from `wc -l` only for a missing final newline. |
| `rg` as a source | `rg [options] PATTERN [path...]`, default `/` (every vault). Prints `path:line:text`, sorted by path. Options: `-i -F -w -v -l -c -n -N -m -A -B -C -e --files`, plus their long forms. | The output format of `rg` in a terminal; line numbers prepare for section addressing. No working directory, so the root is the only sensible default. |
| `rg --files` | Lists note paths; `rg --files \| rg -i kumbu` finds a note by name. | Addresses phase 1 observation 1 with a real ripgrep idiom until link resolution arrives in step 4. |
| `rg` as a filter | Prints matching lines without a prefix; `-n` adds `line:`. | ripgrep's behaviour on standard input. |
| Regex engine | .NET `Regex` with `RegexOptions.NonBacktracking` and a match timeout. | Patterns come from the model and run on the server. Non-backtracking matching is linear in the input, so no pattern can hang the server (ReDoS). Like Rust's `regex`, which ripgrep uses, it has no look-around or backreferences; the error says so. |
| Search storage | New `INoteReader.ReadNotesUnderAsync(dir)` returns paths and contents in one query; `rg` sorts by path. | One round trip instead of one per note. Holding a vault's text in RAM matches the unlocked working set of concept §7. |
| `help` | `help` lists commands with their synopsis; `help <cmd>` and `<cmd> --help` show the details. The tool description stays the short man page. | Concept §5 (discoverability). The tool description is read on every connect, `help` only when needed. |
| Unknown commands | `command not found: rm — try 'help'`. `grep`, `find`, `cd`, `pwd`, `tree`, `less`, `more` get a specific hint. | These are the names models reach for first. |
| Truncation hint | The footer adds "narrow it with rg, head or tail". | Concept §5 wants the footer to teach; `outline` and sections arrive in step 4. |

## 3. Shape

```
Shell.RunAsync("cat /Demo/welcome.md | rg -n kumbu")
  Tokenizer  → [cat] [/Demo/welcome.md] | [rg] [-n] [kumbu]
  Pipeline   → stages: (cat /Demo/welcome.md) (rg -n kumbu)
  run stage 1 (input: none)        → text of the note
  run stage 2 (input: that text)   → "9:See [[Meeting Notes]] and the project page [[kumbu]]."
  OutputLimit on the final output only
```

`ICommand` grows a synopsis, a help text and a flag whether it reads a pipe, and `RunAsync` gets the piped input (`null` for the first stage). A small option parser shared by the filters handles combined flags (`-in`), attached values (`-n5`, `-C2`), `-N` as a line count for `head` and `tail`, long options and `--`.
