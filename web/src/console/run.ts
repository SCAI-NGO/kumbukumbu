// SPDX-License-Identifier: AGPL-3.0-or-later
import { errorText } from "../api/error-text";
import { consoleCall, type ConsoleRequest, type ConsoleView } from "./api";
import { forgetEntries } from "./entries";
import { helpBlock, localHelp } from "./help";
import { resolveLine, resolvePath } from "./paths";
import { useConsole, type Output, type Running } from "./store";
import { tokenise } from "./tokenise";

/** The commands the console answers itself (console design §6). */
const local = new Set(["cd", "pwd", "clear", "exit", "p"]);

/** The console's words for ls; the shell has none of them. */
const shortcuts: Record<string, string> = { l: "ls -l", la: "ls -a", lsa: "ls -la" };

/** `line` with a shortcut as its first word written out, the rest kept as typed; in a later stage it stays. */
const expand = (line: string) => line.replace(/^\s*(l|la|lsa)(?=[\s|]|$)/, (_, word: string) => shortcuts[word]);

let nextId = 1;

/** The image types a part may show as; the shell sends PNG and JPEG. */
const raster = new Set(["image/png", "image/jpeg", "image/gif", "image/webp"]);

const text = (value: string): Output => ({ kind: "text", text: value });
const error = (value: string): Output => ({ kind: "text", text: value, error: true });

/** What a view shows: its parts in order, then the version on its own line, as MCP gives it. */
function shown(view: ConsoleView, sent: string): Output[] {
  const output = view.parts.map((part): Output => {
    if (part.type !== "image") return { kind: "text", text: part.text ?? "", error: view.isError };
    // Only raster types go into the data: URL: an SVG is a document, and anything else no image to show.
    if (!raster.has(part.mediaType ?? "")) return error("(an image the console can't show)");
    return { kind: "image", src: `data:${part.mediaType};base64,${part.data ?? ""}`, alt: `Image from ${sent}` };
  });
  if (view.version) output.push(text(`version of ${view.version.path}: ${view.version.token}`));
  return output;
}

/**
 * Runs a typed line: a local command here, anything else through `POST /api/console` with its paths made absolute.
 * One at a time; an answer that comes after the console was reset (the session ended) is dropped. `leave` goes back to
 * the notes, for `exit`.
 */
export async function run(line: string, signal: AbortSignal, leave: () => void) {
  const store = useConsole.getState();
  if (store.running || line.trim() === "") return;
  store.remember(line);

  const { cwd, generation } = store;
  const entry: Running = { id: nextId++, cwd, typed: line };
  const done = (output: Output[]) => useConsole.getState().add({ ...entry, output });
  const expanded = expand(line);
  const stages = tokenise(expanded);

  // Only as the first word of a line of one stage; a line that doesn't tokenise goes to the shell for its error.
  const inPipeline = stages && stages.length > 1 ? stages.find((stage) => local.has(stage[0])) : undefined;
  if (inPipeline) return done([error(`${inPipeline[0]}: can't be part of a pipeline`)]);

  // `..` alone is `cd ..`, as in zsh and fish.
  const [name, ...args] = stages?.length !== 1 ? [] : stages[0].length === 1 && stages[0][0] === ".." ? ["cd", ".."] : stages[0];
  if (local.has(name) && args.length > (name === "cd" || name === "p" ? 1 : 0)) return done([error(`${name}: too many arguments`)]);
  if (name === "pwd") return done([text(cwd)]);
  if (name === "clear") {
    // Every other command ends in the store's add, which forgets the listings; this one only empties the scrollback.
    forgetEntries();
    return useConsole.setState({ entries: [] });
  }
  if (name === "exit") {
    done([]);
    return leave();
  }
  if (name === "help" && args.length === 1 && Object.hasOwn(localHelp, args[0])) return done([text(localHelp[args[0]])]);

  if (name === "cd") {
    const path = resolvePath(args[0] ?? "/", cwd);
    // The root always is a directory, also when the one we were in has gone.
    if (path === "/") {
      useConsole.setState({ cwd: "/" });
      return done([]);
    }
    return ask({ call: "cd", path }, entry, generation, signal, (view) => {
      if (view.isError) return done(view.parts.map((part) => error(part.text ?? "")));
      useConsole.setState({ cwd: view.parts[0]?.text || path });
      done([]);
    });
  }

  if (name === "p") {
    if (args.length === 0) return done([error("p: missing note")]);
    const path = resolvePath(args[0], cwd);
    // A section (plan.md#Goals) or a block (plan.md#^goal) of a note; the view reads the whole note, as the note view.
    const at = path.indexOf(".md#");
    const note = at < 0 ? path : path.slice(0, at + 3);
    if (!note.endsWith(".md")) return done([error(`p: ${path}: not a note`)]);
    return done([{ kind: "note", path: note, at: at < 0 ? null : path.slice(at + 4) }]);
  }

  const sent = resolveLine(expanded, cwd);
  const intro = name === "help" && args.length === 0 ? [text(helpBlock)] : [];
  return ask({ call: "sh", command: sent }, { ...entry, sent }, generation, signal, (view) =>
    useConsole.getState().add({ ...entry, sent, output: [...intro, ...shown(view, sent)] }),
  );
}

/** Tab's list of matches, as zsh prints it: under the line as typed, which is neither run nor remembered. */
export function listMatches(line: string, matches: string[]) {
  const { cwd, add } = useConsole.getState();
  add({ id: nextId++, cwd, typed: line, output: [text(matches.join("  "))] }, false);
}

async function ask(request: ConsoleRequest, running: Running, generation: number, signal: AbortSignal, answered: (view: ConsoleView) => void) {
  useConsole.setState({ running });
  const current = () => useConsole.getState().generation === generation;
  let view: ConsoleView;
  try {
    view = await consoleCall(request, signal);
  } catch (e) {
    // A 401 has reset the console already, and the app is on its way to sign-in.
    if (!current()) return;
    return useConsole.getState().add({ ...running, output: [error(signal.aborted ? "Stopped, as the page was left." : errorText(e))] });
  }
  if (current()) answered(view);
}
