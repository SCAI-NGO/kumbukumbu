// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../api/client";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import fixture from "./console-fixture.json";
import { useConsole } from "./store";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
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
const lines = () => Array.from(log().querySelectorAll("[data-line]"), (line) => line.textContent);
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
        "  cd [path], pwd, clear",
        "  Relative paths work here; the console turns them into absolute ones, and → shows the result.",
        "  It only reads: restore, mv, mkdir, rm, mkcp and rmcp are refused, and the write tools aren't here.",
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
    const refused = `${fixture.refused.slice(0, -1).join(", ")} and ${fixture.refused.at(-1)}`;
    expect(refused).toBe("restore, mv, mkdir, rm, mkcp and rmcp");
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
    [429, "Too many history scans at once. Try again in a minute."],
    [503, "The vault is busy. Try again in a moment."],
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
    await new Promise((resolve) => setTimeout(resolve, 20));
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
