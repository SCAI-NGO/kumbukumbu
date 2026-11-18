// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api } from "../api/client";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import fixture from "./console-fixture.json";
import { useConsole } from "./store";

// The phone's suggestions list folders as typing goes; outside the completion tests they stay out of `posted`.
const listing = vi.hoisted(() => ({ on: false }));
vi.mock("./entries", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./entries")>();
  return { ...actual, listFolder: (folder: string) => (listing.on ? actual.listFolder(folder) : Promise.resolve(null)) };
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  useDiscreet.setState({ gone: new Set() });
  useConsole.getState().reset();
});

type Body = { call: string; command?: string; path?: string };
type Answer = (body: Body) => Response | Promise<Response>;

/** The session, and the console's endpoint answered by `answer`; `posted` collects what it was sent. */
function consoleApi(answer: Answer = () => text("")) {
  const posted: Body[] = [];
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "POST /api/console": async (request) => {
      const body = (await request.json()) as Body;
      posted.push(body);
      return answer(body);
    },
  });
  return { fetch, posted };
}

const part = (text: string) => ({ type: "text", text, mediaType: null, data: null });
const text = (output: string, isError = false) => json(200, { parts: [part(output)], isError, version: null });

/** A promise to answer with later, for a command still running. */
function later() {
  let answer!: (response: Response) => void;
  const response = new Promise<Response>((resolve) => (answer = resolve));
  return { response, answer };
}

const welcome = "Kumbu² console, not a bash, and read-only. Type help for the commands, help <command> for details.";
const log = () => screen.getByRole("log");
// A peeked note's blocks carry its own line numbers as data-line: they are no lines of the console.
const lines = () => Array.from(log().querySelectorAll("[data-line]")).filter((line) => !line.closest("[data-peek]")).map((line) => line.textContent);
const input = () => screen.getByRole("textbox", { name: "Command" });
const prompt = () => screen.getByRole("form", { name: "Command line" });

async function open(cwd = "/") {
  useConsole.setState({ cwd });
  const router = renderApp("/console");
  await screen.findByRole("heading", { name: "Console" });
  return router;
}

/** Types `line` and Enter, and waits for the input to take the next one. */
async function run(line: string) {
  await userEvent.type(input(), `${line}{Enter}`);
  await waitFor(() => expect(input()).toBeEnabled());
}

describe("the console page", () => {
  it("opens with the welcome, the prompt at / and the focus in the input", async () => {
    consoleApi();
    await open();
    expect(screen.getByText(welcome)).toBeInTheDocument();
    expect(within(prompt()).getByText("/")).toHaveClass("text-muted");
    expect(input()).toHaveFocus();
    expect(lines()).toEqual([]);
  });

  it("sends the line with its paths made absolute, and shows what it ran", async () => {
    const { posted } = consoleApi(() => text("/Gartenarbeit/Shopping/plan.md:12:Tomaten, zwei Sorten"));
    await open("/Gartenarbeit/Shopping");
    await run("rg -n Tomaten plan.md");
    expect(posted).toEqual([{ call: "sh", command: "rg -n Tomaten /Gartenarbeit/Shopping/plan.md" }]);
    expect(lines()).toEqual([
      "/Gartenarbeit/Shopping",
      "> rg -n Tomaten plan.md",
      "→ rg -n Tomaten /Gartenarbeit/Shopping/plan.md",
      "/Gartenarbeit/Shopping/plan.md:12:Tomaten, zwei Sorten",
    ]);
    expect(screen.getByText("rg -n Tomaten /Gartenarbeit/Shopping/plan.md").closest("[data-line]")).toHaveClass("text-dim");
  });

  it("shows the → line also when the line went as typed", async () => {
    const { posted } = consoleApi(() => text("plan.md"));
    await open();
    await run("ls /Work");
    expect(posted).toEqual([{ call: "sh", command: "ls /Work" }]);
    expect(lines()).toEqual(["/", "> ls /Work", "→ ls /Work", "plan.md"]);
  });

  it("colours the vault apart from the rest of the path, and repeats the path only when it changed", async () => {
    consoleApi((body) => (body.call === "cd" ? text(body.path!) : text("x")));
    await open("/Gartenarbeit/Shopping");
    expect(within(prompt()).getByText("/Gartenarbeit")).toHaveClass("text-accent-text");
    expect(within(prompt()).getByText("/Shopping")).toHaveClass("text-muted");

    await run("ls");
    await run("ls");
    await run("cd ..");
    await run("ls");
    expect(lines()).toEqual([
      "/Gartenarbeit/Shopping",
      "> ls",
      "→ ls /Gartenarbeit/Shopping",
      "x",
      "> ls",
      "→ ls /Gartenarbeit/Shopping",
      "x",
      "> cd ..",
      "/Gartenarbeit",
      "> ls",
      "→ ls /Gartenarbeit",
      "x",
    ]);
    expect(within(prompt()).getByText("/Gartenarbeit")).toHaveClass("text-accent-text");
    expect(within(prompt()).queryByText("/Shopping")).not.toBeInTheDocument();
  });
});

describe("cd", () => {
  it("asks the server with the resolved path and takes its answer as the directory", async () => {
    const { posted } = consoleApi(() => text("/Work/projects"));
    await open("/Work");
    await run("cd ./projects/");
    expect(posted).toEqual([{ call: "cd", path: "/Work/projects" }]);
    expect(prompt()).toHaveTextContent("/Work/projects");
    expect(useConsole.getState().cwd).toBe("/Work/projects");
    expect(lines()).toEqual(["/Work", "> cd ./projects/"]);
  });

  it("prints a refusal and stays", async () => {
    consoleApi(() => text("cd: /Work/plan.md: not a folder", true));
    await open("/Work");
    await run("cd plan.md");
    expect(lines()).toEqual(["/Work", "> cd plan.md", "! Error: cd: /Work/plan.md: not a folder"]);
    expect(useConsole.getState().cwd).toBe("/Work");
  });

  it.each([
    ["cd", "/Work/projects", "/"],
    ["cd /", "/Work", "/"],
    ["cd ..", "/", "/"],
    ["cd ../..", "/Work", "/"],
  ])("%s at %s goes to %s without a request", async (line, from, to) => {
    const { posted } = consoleApi();
    await open(from);
    await run(line);
    expect(posted).toEqual([]);
    expect(useConsole.getState().cwd).toBe(to);
  });

  it("refuses more than one path", async () => {
    const { posted } = consoleApi();
    await open("/Work");
    await run("cd a b");
    expect(posted).toEqual([]);
    expect(lines()).toEqual(["/Work", "> cd a b", "! Error: cd: too many arguments"]);
  });

  it.each([
    ["cd x | head", "cd"],
    ["pwd | head", "pwd"],
    ["clear | head", "clear"],
    ["ls | cd x", "cd"],
  ])("refuses %s, as a local command can't be part of a pipeline", async (line, name) => {
    const { posted } = consoleApi();
    await open();
    await run(line);
    expect(posted).toEqual([]);
    expect(lines()).toEqual(["/", `> ${line}`, `! Error: ${name}: can't be part of a pipeline`]);
  });

  it("lets the next command say that the directory has gone, and cd / still works", async () => {
    const { posted } = consoleApi(() => text("ls: /Work/gone: no such folder", true));
    await open("/Work/gone");
    await run("ls");
    expect(lines()).toContain("! Error: ls: /Work/gone: no such folder");
    expect(useConsole.getState().cwd).toBe("/Work/gone");
    await run("cd /");
    expect(posted).toHaveLength(1);
    expect(useConsole.getState().cwd).toBe("/");
    expect(within(prompt()).getByText("/")).toBeInTheDocument();
  });
});

describe("local commands", () => {
  it("prints the directory for pwd without a request", async () => {
    const { posted } = consoleApi();
    await open("/Work/projects");
    await run("pwd");
    expect(posted).toEqual([]);
    expect(lines()).toEqual(["/Work/projects", "> pwd", "/Work/projects"]);
  });

  it("empties the scrollback for clear and keeps the history", async () => {
    consoleApi();
    await open();
    await run("pwd");
    await run("help pwd");
    await run("clear");
    expect(lines()).toEqual([]);
    expect(useConsole.getState().history).toEqual(["pwd", "help pwd", "clear"]);
  });

  it("goes back to the notes for exit, and keeps directory, scrollback and history for coming back", async () => {
    const { posted } = consoleApi();
    const router = await open("/Work");
    await run("pwd");
    await userEvent.type(input(), "exit{Enter}");
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(posted).toEqual([]);
    expect(useConsole.getState().cwd).toBe("/Work");
    expect(useConsole.getState().entries.map((entry) => entry.typed)).toEqual(["pwd", "exit"]);
    expect(useConsole.getState().history).toEqual(["pwd", "exit"]);
  });

  it("stays for exit with an argument, and in a pipeline", async () => {
    const { posted } = consoleApi();
    const router = await open();
    await run("exit now");
    await run("ls | exit");
    expect(router.state.location.pathname).toBe("/console");
    expect(posted).toEqual([]);
    expect(lines()).toEqual(["/", "> exit now", "! Error: exit: too many arguments", "> ls | exit", "! Error: exit: can't be part of a pipeline"]);
  });

  it("takes .. alone for cd ..", async () => {
    const { posted } = consoleApi((body) => text(body.path!));
    await open("/Gartenarbeit/Shopping");
    await run("..");
    expect(posted).toEqual([{ call: "cd", path: "/Gartenarbeit" }]);
    expect(useConsole.getState().cwd).toBe("/Gartenarbeit");
    expect(lines()).toEqual(["/Gartenarbeit/Shopping", "> .."]);
    await run("..");
    expect(useConsole.getState().cwd).toBe("/");
    await run("..");
    expect(useConsole.getState().cwd).toBe("/");
  });

  it("sends .. with an argument to the shell, as any other word", async () => {
    const { posted } = consoleApi(() => text("..: command not found", true));
    await open("/Work");
    await run(".. x");
    expect(posted).toEqual([{ call: "sh", command: ".. x" }]);
    expect(useConsole.getState().cwd).toBe("/Work");
  });

  it("shows its own block for help, then the agents' manual from the server", async () => {
    const { posted } = consoleApi(() => text("Kumbu² shell. All paths are absolute."));
    await open();
    await run("help");
    expect(posted).toEqual([{ call: "sh", command: "help" }]);
    expect(lines()).toEqual([
      "/",
      "> help",
      "→ help",
      [
        "In this console:",
        "  cd [path] (.. alone is cd ..), pwd, clear, exit",
        "  l, la and lsa for ls -l, ls -a and ls -la; p <note> shows a note as it reads in the app (help p).",
        "  Relative paths work here; the console turns them into absolute ones, and → shows the result.",
        "  It only reads: pdfimages, restore, mv, archive, mkdir, rm, mkcp, rmcp, publish add, update and rm and skills copy are refused, and the write tools aren't here.",
        "",
        "The manual agents get, unchanged:",
      ].join("\n"),
      "Kumbu² shell. All paths are absolute.",
    ]);
  });

  it.each(["cd", "pwd", "clear"])("answers help %s itself", async (name) => {
    const { posted } = consoleApi();
    await open();
    await run(`help ${name}`);
    expect(posted).toEqual([]);
    expect(lines()[2]).toMatch(new RegExp(`^${name}`));
    expect(lines()).toHaveLength(3);
  });

  it.each(["help rg", "help | head"])("sends %s to the server", async (line) => {
    const { posted } = consoleApi(() => text("manual"));
    await open();
    await run(line);
    expect(posted).toEqual([{ call: "sh", command: line }]);
    expect(lines()).toEqual(["/", `> ${line}`, `→ ${line}`, "manual"]);
  });

  it("names exactly the refused commands of the server in its help", async () => {
    consoleApi(() => text(""));
    await open();
    await run("help");
    const block = lines()[3]!;
    // publish is allowed for ls alone, skills for all but copy; their other subcommands are refused (ConsoleApi.ShAsync).
    const refused = `${fixture.refused.join(", ")}, publish add, update and rm and skills copy`;
    expect(refused).toBe("pdfimages, restore, mv, archive, mkdir, rm, mkcp, rmcp, publish add, update and rm and skills copy");
    expect(block).toContain(`It only reads: ${refused} are refused`);
  });
});

describe("the history", () => {
  it("walks up and down, holds adjacent duplicates once, and skips empty lines", async () => {
    const { posted } = consoleApi();
    await open();
    await run("pwd");
    await run("help pwd");
    await run("help pwd");
    await userEvent.type(input(), "   {Enter}");
    expect(input()).toHaveValue("");
    expect(posted).toEqual([]);
    expect(lines().filter((l) => l?.startsWith(">"))).toEqual(["> pwd", "> help pwd", "> help pwd"]);
    expect(useConsole.getState().history).toEqual(["pwd", "help pwd"]);

    await userEvent.type(input(), "pw");
    await userEvent.keyboard("{ArrowUp}");
    expect(input()).toHaveValue("help pwd");
    await userEvent.keyboard("{ArrowUp}");
    expect(input()).toHaveValue("pwd");
    await userEvent.keyboard("{ArrowUp}");
    expect(input()).toHaveValue("pwd");
    await userEvent.keyboard("{ArrowDown}");
    expect(input()).toHaveValue("help pwd");
    await userEvent.keyboard("{ArrowDown}");
    expect(input()).toHaveValue("pw");
  });

  it("keeps the last 200 lines", () => {
    for (let i = 0; i < 205; i++) useConsole.getState().remember(`ls /n${i}`);
    const { history } = useConsole.getState();
    expect(history).toHaveLength(200);
    expect(history[0]).toBe("ls /n5");
    expect(history.at(-1)).toBe("ls /n204");
  });
});

describe("the output", () => {
  it.each([
    ["image/png", true],
    ["image/jpeg", true],
    ["image/gif", true],
    ["image/webp", true],
    ["image/svg+xml", false],
    ["text/html", false],
  ])("shows an image part of %s as an image: %s", async (mediaType, shownAsImage) => {
    consoleApi(() => json(200, { parts: [{ type: "image", text: null, mediaType, data: "AAAA" }], isError: false, version: null }));
    await open();
    await run("img /Work/shot");
    const image = log().querySelector("img");
    if (shownAsImage) expect(image).toHaveAttribute("src", `data:${mediaType};base64,AAAA`);
    else {
      expect(image).toBeNull();
      expect(lines()).toContain("! Error: (an image the console can't show)");
    }
  });

  it("prints a version on its own line and shows an image part as an image", async () => {
    consoleApi(() =>
      json(200, {
        parts: [part("shot.png"), { type: "image", text: null, mediaType: "image/png", data: "iVBORw0KGgo=" }],
        isError: false,
        version: { path: "/Work/a.md", token: "v7" },
      }),
    );
    await open();
    await run("img /Work/shot.png");
    expect(lines()).toEqual(["/", "> img /Work/shot.png", "→ img /Work/shot.png", "shot.png", "", "version of /Work/a.md: v7"]);
    const image = within(log()).getByRole("img", { name: "Image from img /Work/shot.png" });
    expect(image).toHaveAttribute("src", "data:image/png;base64,iVBORw0KGgo=");
  });

  it("marks an error, by colour and by a marker", async () => {
    consoleApi(() => text("rm: not in this console, which only reads", true));
    await open();
    await run("rm /Work/a.md");
    const error = screen.getByText("rm: not in this console, which only reads", { exact: false }).closest("[data-line]")!;
    expect(error).toHaveClass("text-warning");
    expect(error).toHaveTextContent(/^! Error: rm:/);
  });

  it.each([
    // AccountLimits.HistoryScansRefused and VaultBusyException.BeingReencrypted.
    [429, "Too many searches of earlier versions or reads of activity; try again in a minute."],
    [503, "This vault is being re-encrypted; try again in a moment."],
  ])("prints the sentence of a %i", async (status, sentence) => {
    consoleApi(() => json(status, { error: sentence }));
    await open();
    await run("history /Work");
    expect(lines()).toEqual(["/", "> history /Work", "→ history /Work", `! Error: ${sentence}`]);
  });

  it("leaves a 401 to the app, which goes to sign-in", async () => {
    consoleApi(() => json(401, { error: "Sign in first." }));
    const router = await open();
    await userEvent.type(input(), "ls{Enter}");
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(useConsole.getState().entries).toEqual([]);
  });

  it("shows a note's HTML as text", async () => {
    consoleApi(() => text("<img src=x onerror=alert(1)>"));
    await open();
    await run("cat /Work/a.md");
    expect(lines()).toContain("<img src=x onerror=alert(1)>");
    expect(log().querySelector("img")).toBeNull();
  });

  it("wraps a long line", async () => {
    consoleApi(() => text("a".repeat(500)));
    await open();
    await run("cat /Work/a.md");
    const output = screen.getByText("a".repeat(500));
    expect(output).toHaveClass("whitespace-pre-wrap");
    expect(output).toHaveClass("break-words");
  });
});

describe("one command at a time", () => {
  it("posts once for two quick Enters and waits with the input disabled", async () => {
    const pending = later();
    const { posted } = consoleApi(() => pending.response);
    await open();
    await userEvent.type(input(), "ls /Work{Enter}{Enter}");
    fireEvent.submit(prompt());
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(input()).toBeDisabled();

    await act(async () => pending.answer(text("plan.md")));
    await waitFor(() => expect(input()).toBeEnabled());
    expect(posted).toHaveLength(1);
    expect(input()).toHaveFocus();
  });

  it("aborts the request on leaving, and keeps directory, scrollback and history for coming back", async () => {
    consoleApi();
    const signals: AbortSignal[] = [];
    // jsdom's AbortSignal doesn't reach a Request in this environment, so the call itself is observed.
    vi.spyOn(api, "POST").mockImplementation(((_: string, init: { signal: AbortSignal }) => {
      signals.push(init.signal);
      return new Promise((_, reject) => init.signal.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError"))));
    }) as never);
    await open("/Work");
    await run("pwd");
    await userEvent.type(input(), "ls{Enter}");
    expect(signals).toHaveLength(1);
    expect(signals[0].aborted).toBe(false);

    cleanup();
    expect(signals[0].aborted).toBe(true);
    await waitFor(() => expect(useConsole.getState().running).toBeNull());

    renderApp("/console");
    await screen.findByRole("heading", { name: "Console" });
    expect(prompt()).toHaveTextContent("/Work");
    expect(lines()).toEqual(["/Work", "> pwd", "/Work", "> ls", "→ ls /Work", "! Error: Stopped, as the page was left."]);
    await userEvent.click(input());
    await userEvent.keyboard("{ArrowUp}");
    expect(input()).toHaveValue("ls");
  });
});

describe("the end of a session", () => {
  const bob = { ...user, id: "u2", username: "bob", display: "Bob" };

  it.each([
    ["signing out", () => useSession.getState().signOut()],
    ["the session ending", () => useSession.getState().end()],
    ["another user signing in", () => useSession.getState().signedIn({ user: bob, previousSignInAt: null })],
  ])("forgets everything on %s, and drops an answer that comes after", async (_, leave) => {
    const pending = later();
    consoleApi((body) => (body.call === "cd" ? pending.response : text("x")));
    await open("/Work");
    await run("pwd");
    await userEvent.type(input(), "cd projects{Enter}");
    await waitFor(() => expect(useConsole.getState().running).not.toBeNull());

    act(() => leave());
    expect(useConsole.getState()).toMatchObject({ cwd: "/", entries: [], history: [], running: null });

    await act(async () => pending.answer(text("/Work/projects")));
    // The answer's promise chain settles within act.
    await act(async () => {});
    expect(useConsole.getState()).toMatchObject({ cwd: "/", entries: [], history: [], running: null });
  });

  it("keeps everything when the same user's session is read again", async () => {
    consoleApi();
    await open("/Work");
    await run("pwd");
    act(() => useSession.getState().signedIn({ user, previousSignInAt: null }));
    expect(useConsole.getState().entries).toHaveLength(1);
    expect(useConsole.getState().cwd).toBe("/Work");
  });
});

describe("browser storage", () => {
  const stored = () =>
    [localStorage, sessionStorage].map((storage) =>
      Array.from({ length: storage.length }, (_, i) => [storage.key(i), storage.getItem(storage.key(i)!)]),
    );

  it("keeps no command, path or output", async () => {
    consoleApi((body) => (body.call === "cd" ? text(body.path!) : text("Tomaten, zwei Sorten")));
    await open();
    const before = stored();
    await run("cd /Gartenarbeit");
    await run("rg -n Tomaten plan.md");
    await run("pwd");
    const after = stored();
    expect(after).toEqual(before);
    expect(JSON.stringify(after)).not.toMatch(/Tomaten|Gartenarbeit|plan\.md/);
  });
});

describe("the console scheme", () => {
  const desktop = () => vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(min-width: 64rem)", media: query, addEventListener() {}, removeEventListener() {} }));
  const stored = () => localStorage.getItem("kumbu.consoleScheme");
  afterEach(() => localStorage.clear());

  it("switches on the desktop with a group of three buttons, and keeps the choice", async () => {
    desktop();
    consoleApi();
    await open();
    const group = screen.getByRole("group", { name: "Console scheme" });
    expect(within(group).getByRole("button", { name: "Plain" })).toHaveAttribute("aria-pressed", "true");
    expect(log()).toHaveAttribute("data-scheme", "plain");
    await userEvent.click(within(group).getByRole("button", { name: "Amber" }));
    expect(within(group).getByRole("button", { name: "Amber" })).toHaveAttribute("aria-pressed", "true");
    expect(log()).toHaveAttribute("data-scheme", "amber");
    expect(stored()).toBe("amber");
    cleanup();
    await open();
    expect(log()).toHaveAttribute("data-scheme", "amber");
  });

  it("starts in Plain, without a crash, where storage throws", async () => {
    desktop();
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    consoleApi();
    await open();
    expect(log()).toHaveAttribute("data-scheme", "plain");
    await userEvent.click(screen.getByRole("button", { name: "Green" }));
    expect(log()).toHaveAttribute("data-scheme", "green");
  });

  it("cycles with one button on the phone", async () => {
    consoleApi();
    await open();
    expect(screen.queryByRole("group", { name: "Console scheme" })).not.toBeInTheDocument();
    for (const [now, next] of [["Plain", "Green"], ["Green", "Amber"], ["Amber", "Plain"]]) {
      await userEvent.click(screen.getByRole("button", { name: `Console scheme: ${now}` }));
      expect(log()).toHaveAttribute("data-scheme", next.toLowerCase());
    }
    expect(stored()).toBe("plain");
  });

  it("shows an error in inverse video with the word, and keeps the output selectable", async () => {
    localStorage.setItem("kumbu.consoleScheme", "green");
    consoleApi(() => text("rm: not in this console, which only reads", true));
    await open();
    await run("rm /Work/a.md");
    const error = screen.getByText("rm: not in this console, which only reads", { exact: false }).closest("[data-line]")!;
    expect(error).toHaveAttribute("data-error");
    expect(error).toHaveTextContent(/error/i);
    const scanlines = document.querySelector("[data-scanlines]")!;
    expect(scanlines).toHaveAttribute("aria-hidden", "true");
    expect(scanlines).toHaveClass("pointer-events-none");
    // Over the output only, not the command line.
    expect(scanlines.parentElement).toContainElement(log());
    expect(scanlines.parentElement).not.toContainElement(prompt());
  });

  it.each([
    ["green", "#33ff66", "#030a05", "#22b348"],
    ["amber", "#ffb000", "#0a0600", "#b37b00"],
  ])("pins the %s colours", async (scheme, fg, bg, dim) => {
    localStorage.setItem("kumbu.consoleScheme", scheme);
    consoleApi();
    await open();
    const screenBox = document.querySelector<HTMLElement>("[data-crt]")!;
    expect(screenBox.style.getPropertyValue("--crt-fg")).toBe(fg);
    expect(screenBox.style.getPropertyValue("--crt-bg")).toBe(bg);
    expect(screenBox.style.getPropertyValue("--crt-dim")).toBe(dim);
  });

  it("draws a decorative block cursor in the phosphor schemes only, following the caret", async () => {
    consoleApi();
    await open();
    expect(document.querySelector("[data-cursor]")).toBeNull();
    cleanup();
    localStorage.setItem("kumbu.consoleScheme", "green");
    await open();
    const cursor = document.querySelector<HTMLElement>("[data-cursor]")!;
    expect(cursor).toHaveAttribute("aria-hidden", "true");
    expect(cursor).toHaveClass("motion-safe:animate-[k-blink_1s_steps(1)_infinite]");
    expect(cursor.style.left).toBe("calc(0ch - 0px)");
    await userEvent.type(input(), "help");
    expect(cursor.style.left).toBe("calc(4ch - 0px)");
    await userEvent.keyboard("{ArrowLeft}{ArrowLeft}");
    expect(cursor.style.left).toBe("calc(2ch - 0px)");
    // A line longer than the field scrolls it: the cursor stays on the caret.
    input().scrollLeft = 20;
    fireEvent.scroll(input());
    expect(cursor.style.left).toBe("calc(2ch - 20px)");
    input().blur();
    await waitFor(() => expect(document.querySelector("[data-cursor]")).toBeNull());
  });

  it("shows the word error in Plain too", async () => {
    consoleApi(() => text("rm: nope", true));
    await open();
    await run("rm /Work/a.md");
    expect(screen.getByText("Error:")).not.toHaveClass("sr-only");
  });
});

describe("the shortcuts for ls", () => {
  it.each([
    ["l", "/Work", "ls -l /Work"],
    ["la", "/Work", "ls -a /Work"],
    ["lsa", "/Work", "ls -la /Work"],
    ["l", "/", "ls -l"],
    ["l projects", "/Work", "ls -l /Work/projects"],
    ["la /Home", "/Work", "ls -a /Home"],
    ["l /Work | rg md", "/", "ls -l /Work | rg md"],
    ["lsa|head -n 3", "/Work", "ls -la /Work | head -n 3"],
    ["  l  /Work", "/", "ls -l  /Work"],
  ])("sends %s at %s as %s, and shows it", async (line, cwd, sent) => {
    const { posted } = consoleApi(() => text("plan.md"));
    await open(cwd);
    await run(line);
    expect(posted).toEqual([{ call: "sh", command: sent }]);
    expect(lines()).toContain(`→ ${sent}`);
    expect(lines()).toContain(`> ${line}`);
    expect(useConsole.getState().history).toEqual([line]);
  });

  it.each([
    ["cat /Work/a.md | l", "cat /Work/a.md | l"],
    ["ls l", "ls /Work/l"],
    ["rg -n la", "rg -n la /Work"],
    ["ll", "ll"],
    ["lsa-x", "lsa-x"],
  ])("leaves %s alone", async (line, sent) => {
    const { posted } = consoleApi(() => text(""));
    await open("/Work");
    await run(line);
    expect(posted).toEqual([{ call: "sh", command: sent }]);
  });
});

describe("peek", () => {
  const PLAN = "/Work/projects/plan.md";
  const source = [
    "---",
    "status: draft",
    "---",
    "# Plan",
    "",
    "See [[roadmap]] and [[Secret/diary]].",
    "",
    "- [ ] write",
    "- [x] read",
    "",
    "```mermaid",
    "graph TD; A-->B",
    "```",
    "",
    "<script>alert(1)</script>",
    "",
    'A <img src=x onerror="alert(1)"> and <b onclick="x()">bold</b> word',
    "",
    "## Goals",
    "",
    "Ship the [[alpha]]. ^goal",
    "",
    "## Later",
    "",
    "Nothing yet. #work",
  ].join("\n");
  const planLinks = [
    { line: 6, raw: "[[roadmap]]", target: "roadmap", kind: "note", path: "/Work/projects/roadmap.md", candidates: [] },
    // A discreet vault's note, while hidden, is no link target: the server answers "not found", as for the note view.
    { line: 6, raw: "[[Secret/diary]]", target: "Secret/diary", kind: "not_found", path: null, candidates: [] },
    { line: 21, raw: "[[alpha]]", target: "alpha", kind: "note", path: "/Work/projects/alpha.md", candidates: [] },
  ];
  const noteView = (path: string, content: string) => ({ path, content, version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-18T08:00:00Z" });
  afterEach(() => localStorage.clear());

  /** The notes API as the note view reads it, and the console's endpoint, which peek never asks. */
  function peekApi(extra: Parameters<typeof fakeApi>[0] = {}) {
    const posted: Body[] = [];
    const fetch = fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "POST /api/console": async (request) => {
        posted.push((await request.json()) as Body);
        return text("");
      },
      [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, source)),
      [`GET /api/links${PLAN}`]: () => json(200, planLinks),
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2 }]),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
      "GET /api/notes/*": (r) => json(200, noteView(decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), "# Roadmap")),
      "GET /api/links/*": () => json(200, []),
      "GET /api/backlinks/*": () => json(200, []),
      "GET /api/changes": () => json(200, []),
      ...extra,
    });
    return { fetch, posted };
  }

  const peeked = () => log().querySelector<HTMLElement>("[data-peek]")!;
  const reads = (fetch: ReturnType<typeof fakeApi>, path: string) =>
    fetch.mock.calls.filter(([r]) => (r as Request).method === "GET" && new URL((r as Request).url).pathname === `/api/notes${path}`).length;

  async function peek(line: string, cwd = "/Work/projects") {
    await open(cwd);
    await run(line);
    await waitFor(() => expect(peeked()).not.toBeNull());
  }

  it("reads the note as the note view does, with its path made absolute, and renders it under its path", async () => {
    const { posted } = peekApi();
    await peek("p plan.md");
    expect(posted).toEqual([]);
    expect(lines()).toEqual(["/Work/projects", "> p plan.md", PLAN]);
    expect(within(log()).getByText("/projects/plan.md")).toHaveClass("text-muted");
    expect(within(peeked()).getByRole("heading", { level: 1, name: "Plan" })).toBeInTheDocument();
    expect(await within(peeked()).findByRole("link", { name: "roadmap" })).toHaveAttribute("href", "/notes/Work/projects/roadmap.md");
    // Shown as the note view shows it: the front matter as properties, no version line.
    expect(within(peeked()).getByText("draft")).toBeInTheDocument();
    expect(peeked()).not.toHaveTextContent("status: draft");
    expect(log()).not.toHaveTextContent("version of");
  });

  it("shows a note too long for one call of the shell whole", async () => {
    const long = `# Long\n\n${Array.from({ length: 4000 }, (_, i) => `Line ${i} of a long note.`).join("\n\n")}\n\nThe end.`;
    peekApi({ [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, long)) });
    await peek("p plan.md");
    expect(within(peeked()).getByText("The end.")).toBeInTheDocument();
  });

  it("shows task boxes disabled and a Mermaid block as code", async () => {
    peekApi();
    await peek("p plan.md");
    expect(within(peeked()).getByRole("checkbox", { name: "write" })).toBeDisabled();
    expect(within(peeked()).getByRole("checkbox", { name: "read" })).toBeDisabled();
    const code = peeked().querySelector("pre code")!;
    expect(code).toHaveTextContent("graph TD; A-->B");
    expect(code).toHaveClass("language-mermaid");
  });

  it("shows raw HTML as text and never renders it, as the note view does", async () => {
    peekApi();
    await peek("p plan.md");
    expect(peeked().querySelector("script, b, img")).toBeNull();
    expect(within(peeked()).getByText("<script>alert(1)</script>")).toBeInTheDocument();
    expect(peeked()).toHaveTextContent('A <img src=x onerror="alert(1)"> and <b onclick="x()">bold</b> word');
  });

  it("opens a link's note in the console window's place, which closes it", async () => {
    peekApi();
    useConsole.setState({ cwd: "/Work/projects" });
    const router = renderApp(["/", "/console"]);
    await screen.findByRole("heading", { name: "Console" });
    await run("p plan.md");
    await userEvent.click(await within(log()).findByRole("link", { name: "roadmap" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/roadmap.md"));
    expect(router.state.historyAction).toBe("REPLACE");
    await waitFor(() => expect(screen.queryByRole("heading", { name: "Console" })).not.toBeInTheDocument());
  });

  it("marks a link the server didn't resolve (as to a hidden discreet vault) missing, without a way to create the note", async () => {
    peekApi();
    await peek("p plan.md");
    await within(peeked()).findByRole("link", { name: "roadmap" });
    expect(within(peeked()).queryByRole("link", { name: "Secret/diary" })).toBeNull();
    expect(within(peeked()).queryByRole("button", { name: /Create the note/ })).toBeNull();
    expect(within(peeked()).getByText("(missing)")).toBeInTheDocument();
  });

  it("shows a section with its links, as they go by the whole note's lines", async () => {
    peekApi();
    await peek('p "plan.md#Goals"');
    expect(lines()).toContain(`${PLAN}#Goals`);
    expect(within(peeked()).getByRole("heading", { level: 2, name: "Goals" })).toBeInTheDocument();
    expect(await within(peeked()).findByRole("link", { name: "alpha" })).toHaveAttribute("href", "/notes/Work/projects/alpha.md");
    expect(peeked()).not.toHaveTextContent("Plan");
    expect(peeked()).not.toHaveTextContent("Nothing yet.");
    expect(within(peeked()).queryByText("draft")).toBeNull();
  });

  it("shows a block with its links", async () => {
    peekApi();
    await peek("p plan.md#^goal");
    expect(lines()).toContain(`${PLAN}#^goal`);
    expect(await within(peeked()).findByRole("link", { name: "alpha" })).toHaveAttribute("href", "/notes/Work/projects/alpha.md");
    expect(peeked()).not.toHaveTextContent("Goals");
    expect(peeked()).not.toHaveTextContent("Nothing yet.");
    expect(peeked()).not.toHaveTextContent("^goal");
  });

  it("names a tag without searching for it: the shell behind the window is not to be used", async () => {
    peekApi();
    await peek("p plan.md");
    expect(within(peeked()).getByText("#work")).toBeInTheDocument();
    expect(within(peeked()).queryByRole("button", { name: "Notes tagged #work" })).toBeNull();
  });

  it("says so for a heading the note doesn't have", async () => {
    peekApi();
    await open("/Work/projects");
    await run('p "plan.md#Nowhere"');
    await waitFor(() => expect(lines()).toContain(`! Error: p: ${PLAN}: no heading “Nowhere”`));
  });

  it.each([
    ["p", "p: missing note"],
    ["p a.md b.md", "p: too many arguments"],
    ["p projects", "p: /Work/projects: not a note"],
    ["p shot.png", "p: /Work/shot.png: not a note"],
    ["p plan.md | head", "p: can't be part of a pipeline"],
  ])("refuses %s without a request", async (line, said) => {
    const { fetch, posted } = peekApi();
    await open("/Work");
    const before = fetch.mock.calls.length;
    await run(line);
    expect(posted).toEqual([]);
    expect(fetch.mock.calls.length).toBe(before);
    expect(lines()).toEqual(["/Work", `> ${line}`, `! Error: ${said}`]);
  });

  it("prints an error for a note that isn't there", async () => {
    peekApi({ "GET /api/notes/Work/gone.md": () => json(404, { error: "There is no note /Work/gone.md." }) });
    await open("/Work");
    await run("p gone.md");
    await waitFor(() => expect(lines()).toEqual(["/Work", "> p gone.md", "/Work/gone.md", "! Error: p: There is no note /Work/gone.md."]));
    expect(log().querySelector("[data-peek]")).toBeNull();
  });

  it("collapses a note of a discreet vault hidden again to a neutral line, and reads it no more", async () => {
    const DIARY = "/Tagebuch/a.md";
    const { fetch } = peekApi({ [`GET /api/notes${DIARY}`]: () => json(200, noteView(DIARY, `Text of ${DIARY}.`)) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    useConsole.setState({ cwd: "/" });
    renderApp("/console", queryClient);
    await screen.findByRole("heading", { name: "Console" });
    await run(`p ${DIARY}`);
    await waitFor(() => expect(peeked()).toHaveTextContent(`Text of ${DIARY}`));
    const before = reads(fetch, DIARY);

    // What hiding again does first; the console's reset follows it.
    act(() => useDiscreet.setState({ gone: new Set(["Tagebuch"]), names: new Set() }));
    expect(lines()).toEqual(["/", `> p ${DIARY}`, "(a note that's hidden again)"]);
    expect(log().querySelector("[data-peek]")).toBeNull();
    // Nothing observes it any more: asking the active queries again reads nothing of it.
    await act(() => queryClient.invalidateQueries());
    expect(reads(fetch, DIARY)).toBe(before);
  });

  it("explains itself for help p, which doesn't name cat", async () => {
    const { posted } = peekApi();
    await open();
    await run("help p");
    expect(posted).toEqual([]);
    expect(lines()[2]).toMatch(/^p <note>/);
    expect(lines()[2]).not.toMatch(/\bcat\b/);
  });

  it.each(["l", "la", "lsa"])("explains help %s itself", async (name) => {
    const { posted } = peekApi();
    await open();
    await run(`help ${name}`);
    expect(posted).toEqual([]);
    expect(lines()[2]).toMatch(new RegExp(`^${name} \\[path\\]`));
  });

  it.each([
    ["green", "#33ff66"],
    ["amber", "#ffb000"],
  ])("draws the note in phosphor in %s, as the rest of the output", async (scheme, fg) => {
    localStorage.setItem("kumbu.consoleScheme", scheme);
    peekApi();
    await peek("p plan.md");
    const screenBox = document.querySelector<HTMLElement>("[data-crt]")!;
    expect(screenBox.style.getPropertyValue("--crt-fg")).toBe(fg);
    expect(screenBox).toContainElement(peeked());
    for (const name of ["--text", "--text-body", "--accent-text", "--muted", "--dim", "--line"]) {
      expect(peeked().style.getPropertyValue(name)).toMatch(/var\(--crt-/);
    }
  });

  it("keeps the app's colours in Plain", async () => {
    peekApi();
    await peek("p plan.md");
    expect(peeked().style.getPropertyValue("--text")).toBe("");
  });
});

describe("completion", () => {
  beforeEach(() => (listing.on = true));
  afterEach(() => (listing.on = false));
  const desktop = () => vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(min-width: 64rem)", media: query, addEventListener() {}, removeEventListener() {} }));
  const listings: Record<string, string> = {
    ls: "Work/",
    "ls -a /Work": "_archive/\nMeeting Notes.md\nMeeting Plan.md\nplan.md\nprojects/",
    "ls /Work/projects": "alpha.md",
  };
  /** The shell answering `ls` from `listings`, anything else with `answer`. */
  const shell = (answer: Answer = () => text("")) =>
    consoleApi((body) => (body.call === "sh" && body.command! in listings ? text(listings[body.command!]) : answer(body)));
  const listed = (posted: Body[]) => posted.filter((body) => body.command?.startsWith("ls"));
  const tab = () => fireEvent.keyDown(input(), { key: "Tab" });

  describe("Tab on the desktop", () => {
    it("completes a unique match and puts the cursor after it, keeping the focus", async () => {
      desktop();
      shell();
      await open("/Work");
      await userEvent.type(input(), "cat pl");
      await userEvent.keyboard("{Tab}");
      await waitFor(() => expect(input()).toHaveValue("cat plan.md "));
      expect(input()).toHaveFocus();
      expect((input() as HTMLInputElement).selectionStart).toBe(12);
    });

    it("completes several matches to their common prefix, the cursor at its end with the rest kept", async () => {
      desktop();
      shell();
      await open("/Work");
      await userEvent.type(input(), "cat Mee | wc");
      (input() as HTMLInputElement).setSelectionRange(7, 7);
      tab();
      await waitFor(() => expect(input()).toHaveValue("cat Meeting\\  | wc"));
      expect((input() as HTMLInputElement).selectionStart).toBe(13);
    });

    it("lists the matches as one line on a second Tab with nothing new, without running anything", async () => {
      desktop();
      const { posted } = shell();
      await open("/Work");
      await userEvent.type(input(), "cat Meeting\\ ");
      tab();
      await waitFor(() => expect(listed(posted)).toHaveLength(1));
      expect(lines()).toEqual([]);
      tab();
      await waitFor(() => expect(lines()).toEqual(["/Work", "> cat Meeting\\ ", "Meeting Notes.md  Meeting Plan.md"]));
      expect(input()).toHaveValue("cat Meeting\\ ");
      expect(posted.filter((body) => !body.command?.startsWith("ls"))).toEqual([]);
      expect(useConsole.getState().history).toEqual([]);
      // Listing ran nothing, so the folder's listing is still good.
      tab();
      await waitFor(() => expect(lines()).toHaveLength(5));
      expect(listed(posted)).toHaveLength(1);
    });

    it("lists on the Tab after one that put in only a common prefix, as bash does", async () => {
      desktop();
      shell();
      await open("/Work");
      await userEvent.type(input(), "cat Mee");
      tab();
      await waitFor(() => expect(input()).toHaveValue("cat Meeting\\ "));
      tab();
      await waitFor(() => expect(lines()).toEqual(["/Work", "> cat Meeting\\ ", "Meeting Notes.md  Meeting Plan.md"]));
    });

    it("doesn't list after a Tab that completed a unique match, or once the line changed", async () => {
      desktop();
      const { posted } = shell();
      await open("/Work");
      await userEvent.type(input(), "cat pl");
      tab();
      await waitFor(() => expect(input()).toHaveValue("cat plan.md "));
      tab();
      await act(async () => {});
      expect(lines()).toEqual([]);
      await userEvent.clear(input());
      await userEvent.type(input(), "cat Meeting\\ ");
      tab();
      await waitFor(() => expect(listed(posted).length).toBeGreaterThan(0));
      await act(async () => {});
      await userEvent.type(input(), "N{Backspace}");
      tab();
      await act(async () => {});
      expect(lines()).toEqual([]);
    });

    it("flashes the command line for no match, briefly, with no output", async () => {
      desktop();
      shell();
      await open("/Work");
      await userEvent.type(input(), "cat zz");
      tab();
      await waitFor(() => expect(prompt()).toHaveClass("k-bell"));
      await waitFor(() => expect(prompt()).not.toHaveClass("k-bell"), { timeout: 1000 });
      expect(lines()).toEqual([]);
      expect(input()).toHaveValue("cat zz");
    });

    it("keeps Tab in the input and leaves Shift+Tab to move the focus", async () => {
      desktop();
      const { posted } = shell();
      await open("/Work");
      await userEvent.type(input(), "cat pl");
      expect(fireEvent.keyDown(input(), { key: "Tab", shiftKey: true })).toBe(true);
      expect(tab()).toBe(false);
      await waitFor(() => expect(input()).toHaveValue("cat plan.md "));
      expect(listed(posted)).toHaveLength(1);
    });

    it("does nothing on an empty line or stage, and while a command runs", async () => {
      desktop();
      const pending = later();
      const { posted } = shell(() => pending.response);
      await open("/Work");
      tab();
      tab();
      await userEvent.type(input(), "cat plan.md | ");
      tab();
      tab();
      await act(async () => {});
      expect(posted).toEqual([]);
      expect(lines()).toEqual([]);
      expect(prompt()).not.toHaveClass("k-bell");

      await userEvent.clear(input());
      await userEvent.type(input(), "rg x{Enter}");
      await waitFor(() => expect(input()).toBeDisabled());
      tab();
      tab();
      await act(async () => {});
      expect(listed(posted)).toEqual([]);
      await act(async () => pending.answer(text("")));
    });

    it("lists a folder again after a command ran, clear included", async () => {
      desktop();
      const { posted } = shell();
      await open("/Work");
      await userEvent.type(input(), "cat pl");
      tab();
      await waitFor(() => expect(input()).toHaveValue("cat plan.md "));
      await userEvent.clear(input());
      await run("clear");
      await userEvent.type(input(), "cat pl");
      tab();
      await waitFor(() => expect(input()).toHaveValue("cat plan.md "));
      expect(listed(posted)).toHaveLength(2);
    });
  });

  describe("the phone's suggestion row", () => {
    const row = () => screen.queryByRole("group", { name: "Suggestions" });
    const chips = () => within(row()!).getAllByRole("button").map((chip) => chip.textContent);

    it("offers the matches for a path word, folders with their slash, as 48 px targets", async () => {
      shell();
      await open("/Work");
      await userEvent.type(input(), "cat p");
      await waitFor(() => expect(chips()).toEqual(["plan.md", "projects/"]));
      for (const chip of within(row()!).getAllByRole("button")) expect(chip).toHaveClass("min-h-12");
      expect(row()).toHaveClass("overflow-x-auto");
    });

    it("completes on a tap, keeping the focus in the input, and offers the folder's entries next", async () => {
      shell();
      await open("/Work");
      await userEvent.type(input(), "cat pro");
      const blurred = vi.fn();
      input().addEventListener("blur", blurred);
      await userEvent.click(await screen.findByRole("button", { name: "projects/" }));
      expect(input()).toHaveValue("cat projects/");
      expect(input()).toHaveFocus();
      expect(blurred).not.toHaveBeenCalled();
      expect((input() as HTMLInputElement).selectionStart).toBe(13);
      await waitFor(() => expect(chips()).toEqual(["alpha.md"]));
      await userEvent.click(screen.getByRole("button", { name: "alpha.md" }));
      expect(input()).toHaveValue("cat projects/alpha.md ");
      expect(blurred).not.toHaveBeenCalled();
      await waitFor(() => expect(chips()).toEqual(["Meeting Notes.md", "Meeting Plan.md", "plan.md", "projects/"]));
    });

    it("offers the working folder's entries for an empty word after a command, folders only for cd, and nothing for an empty first word", async () => {
      shell();
      await open("/Work");
      await userEvent.type(input(), "cat ");
      await waitFor(() => expect(chips()).toEqual(["Meeting Notes.md", "Meeting Plan.md", "plan.md", "projects/"]));
      await userEvent.clear(input());
      await userEvent.type(input(), "cd ");
      await waitFor(() => expect(chips()).toEqual(["projects/"]));
      await userEvent.clear(input());
      await userEvent.type(input(), " ");
      await act(async () => {});
      expect(row()).toBeNull();
    });

    it("drops the chips when the console resets, as a discreet vault hides", async () => {
      let hidden = false;
      // Once hidden, the vault is gone from the listing too.
      consoleApi((body) => text(body.command === "ls" && !hidden ? "Work/" : ""));
      await open("/");
      await userEvent.type(input(), "ls Wo");
      await waitFor(() => expect(chips()).toEqual(["Work/"]));
      hidden = true;
      act(() => useConsole.getState().reset());
      expect(row()).toBeNull();
      await act(async () => new Promise((resolve) => setTimeout(resolve, 50)));
      expect(row()).toBeNull();
    });

    it("offers commands for the first word", async () => {
      shell();
      await open("/Work");
      await userEvent.type(input(), "pw");
      await waitFor(() => expect(chips()).toEqual(["pwd"]));
      await userEvent.click(screen.getByRole("button", { name: "pwd" }));
      expect(input()).toHaveValue("pwd ");
    });

    it("hides when the input is empty, nothing matches, or a command runs", async () => {
      const pending = later();
      shell(() => pending.response);
      await open("/Work");
      expect(row()).toBeNull();
      await userEvent.type(input(), "cat zz");
      await act(async () => {});
      expect(row()).toBeNull();
      await userEvent.clear(input());
      await userEvent.type(input(), "cat pl");
      await waitFor(() => expect(row()).not.toBeNull());
      await userEvent.clear(input());
      await waitFor(() => expect(row()).toBeNull());
      await userEvent.type(input(), "rg pl");
      await waitFor(() => expect(row()).not.toBeNull());
      fireEvent.submit(prompt());
      await waitFor(() => expect(input()).toBeDisabled());
      expect(row()).toBeNull();
      await act(async () => pending.answer(text("")));
    });

    it("grows the output's bottom padding with the row, so it never covers the last line", async () => {
      shell();
      await open("/Work");
      expect(log()).not.toHaveClass("pb-16");
      await userEvent.type(input(), "cat pl");
      await waitFor(() => expect(row()).not.toBeNull());
      expect(log()).toHaveClass("pb-16");
      await userEvent.clear(input());
      await waitFor(() => expect(log()).not.toHaveClass("pb-16"));
    });

    it("outlines the chips in phosphor in Green", async () => {
      localStorage.setItem("kumbu.consoleScheme", "green");
      shell();
      await open("/Work");
      await userEvent.type(input(), "cat pl");
      const chip = await screen.findByRole("button", { name: "plan.md" });
      expect(chip).toHaveClass("border-(--crt-fg)");
      localStorage.clear();
    });
  });
});
