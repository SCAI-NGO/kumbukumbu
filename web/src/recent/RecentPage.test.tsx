// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, navigateAway, renderApp, user } from "../test/app";
import { FakeSocket } from "../test/socket";
import type { RecentItem, RecentView } from "./api";

beforeEach(() => {
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(min-width: 64rem)", media: query, addEventListener() {}, removeEventListener() {} }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  localStorage.clear();
  sessionStorage.clear();
});

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

/** A row as the server answers it, for a path from the root (`/Work/Pump/Certificate.md`). */
function item(path: string, extra: Partial<RecentItem> = {}): RecentItem {
  const segments = path.split("/").slice(1);
  const name = segments.at(-1)!;
  return {
    path,
    vault: segments[0],
    title: name.replace(/\.md$/, ""),
    folder: segments.slice(0, -1).join("/"),
    kind: "note",
    flag: null,
    writer: "Bob",
    writerKind: "user",
    writtenAt: minutesAgo(10),
    ...extra,
  };
}

const certificate = item("/Work/Pump/Certificate.md", { flag: "red" });
const sketch = item("/Work/Pump/sketch.png", { kind: "image", title: "sketch.png", writer: "alice", writerKind: "self", writtenAt: minutesAgo(30) });
const garden = item("/Home/Garden.md", { writer: "liz", writerKind: "agent", writtenAt: minutesAgo(180) });

const vault = (name: string) => ({ name, notes: 4, embeddings: "internal", canWrite: true, level: "owner" });

/**
 * Recent changes answers `answer` for each request, which `asked` records as its query. `shown` names the discreet
 * vaults the session shows at the time of each read; while there are some, the session says shown.
 */
function api(answer: (query: URLSearchParams) => RecentView, vaults = ["Home", "Skills", "Work"], shown: () => string[] = () => []) {
  const asked: URLSearchParams[] = [];
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: shown().length > 0, until: null } }),
    "GET /api/vaults": () => json(200, vaults.map((name) => ({ ...vault(name), discreet: shown().includes(name) }))),
    "GET /api/calendar/whats-on": () => json(200, { items: [], count: 0, overdue: false, vaults: [], couldntRead: 0, couldntReadVaults: [] }),
    "GET /api/recent": (r) => {
      const query = new URL(r.url).searchParams;
      asked.push(query);
      return json(200, answer(query));
    },
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/*": (r) => {
      const [, , , name, what] = new URL(r.url).pathname.split("/").map(decodeURIComponent);
      if (what === "publications") return json(200, { publications: [], unpublishable: [] });
      return json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: [] } });
    },
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "Bob", lastWriteAt: minutesAgo(10) }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/files/*": () => new Response(new Blob(["png"], { type: "image/png" }), { status: 200 }),
  });
  return { asked, fetch, last: () => asked.at(-1)! };
}

const view = (items: RecentItem[], extra: Partial<RecentView> = {}): RecentView => ({ items, more: false, vaults: ["Home", "Skills", "Work"], ...extra });

const page = () => screen.findByRole("dialog", { name: "Recent changes" });
const rows = () => within(screen.getByRole("list", { name: "Recent changes" })).getAllByRole("listitem");
const chip = (name: string) => within(screen.getByRole("group", { name: "Vaults" })).getByRole("button", { name });

describe("Recent changes", () => {
  it("rows newest first with who and when", async () => {
    api(() => view([certificate, sketch, garden]));
    renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(3));
    const [first, second, third] = rows();
    expect(first).toHaveTextContent("Certificate");
    expect(within(first).getByRole("img", { name: "Red flag" })).toBeInTheDocument();
    expect(first).toHaveTextContent("Work/Pump");
    expect(first).toHaveTextContent("Bob · 10 minutes ago");
    expect(second).toHaveTextContent("sketch.png");
    expect(second).toHaveTextContent("You · 30 minutes ago");
    expect(third).toHaveTextContent("Garden");
    expect(third).toHaveTextContent("Home");
    expect(third).toHaveTextContent("Agent liz · 3 hours ago");
    // Our fields stay ours.
    expect(document.body).not.toHaveTextContent(/writerKind|writtenAt|self/);
  });

  it("chips for every vault, all on, a tap switches one off", async () => {
    const { last } = api((q) => view([certificate, sketch, garden].filter((i) => !q.getAll("skip").includes(i.vault))));
    renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(3));
    for (const name of ["Home", "Skills", "Work"]) expect(chip(name)).toHaveAttribute("aria-pressed", "true");
    expect(last().getAll("skip")).toEqual([]);

    await userEvent.click(chip("Work"));
    expect(chip("Work")).toHaveAttribute("aria-pressed", "false");
    await waitFor(() => expect(last().getAll("skip")).toEqual(["Work"]));
    await waitFor(() => expect(rows()).toHaveLength(1));
    expect(rows()[0]).toHaveTextContent("Garden");

    await userEvent.click(chip("Home"));
    await waitFor(() => expect(last().getAll("skip").sort()).toEqual(["Home", "Work"]));
    await waitFor(() => expect(screen.getByText("Nothing matches that.")).toBeInTheDocument());

    await userEvent.click(chip("Work"));
    expect(chip("Work")).toHaveAttribute("aria-pressed", "true");
    await waitFor(() => expect(last().getAll("skip")).toEqual(["Home"]));
    await waitFor(() => expect(rows()).toHaveLength(2));
  });

  it("words and flag narrow it", async () => {
    const { last } = api((q) => {
      const words = (q.get("words") ?? "").toLowerCase().split(" ").filter(Boolean);
      const flag = q.get("flag");
      return view(
        [certificate, sketch, garden].filter((i) => words.every((w) => `${i.folder}/${i.title}`.toLowerCase().includes(w)) && (!flag || (flag === "any" ? i.flag !== null : i.flag === flag))),
      );
    });
    renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(3));

    await userEvent.type(screen.getByRole("searchbox", { name: "Find by name" }), "work pump");
    await waitFor(() => expect(last().get("words")).toBe("work pump"));
    await waitFor(() => expect(rows()).toHaveLength(2));

    const flags = screen.getByRole("group", { name: "Find by flag" });
    // Here every flag, on one row that scrolls sideways: only the tree's Find by name shows the flags in use.
    expect(within(flags).getAllByRole("button").map((b) => b.textContent)).toEqual(["Any flag", "Red", "Green", "Blue", "Purple", "Yellow", "White", "Done", "Important"]);
    expect(flags).toHaveClass("flex-nowrap", "overflow-x-auto", "[&>button]:flex-none");
    await userEvent.click(within(flags).getByRole("button", { name: "Red" }));
    expect(within(flags).getByRole("button", { name: "Red" })).toHaveAttribute("aria-pressed", "true");
    await waitFor(() => expect(last().get("flag")).toBe("red"));
    await waitFor(() => expect(rows()).toHaveLength(1));
    expect(rows()[0]).toHaveTextContent("Certificate");

    // One at a time: Any flag takes the place of Red.
    await userEvent.click(within(flags).getByRole("button", { name: "Any flag" }));
    expect(within(flags).getByRole("button", { name: "Red" })).toHaveAttribute("aria-pressed", "false");
    await waitFor(() => expect(last().get("flag")).toBe("any"));

    await userEvent.clear(screen.getByRole("searchbox", { name: "Find by name" }));
    await userEvent.type(screen.getByRole("searchbox", { name: "Find by name" }), "nothing");
    expect(await screen.findByText("Nothing matches that.")).toBeInTheDocument();
  });

  it("opens with the focus in the field", async () => {
    api(() => view([certificate]));
    renderApp("/recent");
    await page();
    expect(screen.getByRole("searchbox", { name: "Find by name" })).toHaveFocus();
  });

  it("the field is a 48 px target, its browser ✕ hidden, and Clear empties it", async () => {
    const { last } = api(() => view([certificate, sketch, garden]));
    renderApp("/recent");
    await page();
    const field = screen.getByRole("searchbox", { name: "Find by name" });
    expect(field).toHaveClass("h-12", "[&::-webkit-search-cancel-button]:hidden");
    expect(screen.queryByRole("button", { name: "Clear" })).not.toBeInTheDocument();
    await userEvent.type(field, "pump");
    await waitFor(() => expect(last().get("words")).toBe("pump"));
    const clear = screen.getByRole("button", { name: "Clear" });
    expect(clear).toHaveClass("size-12");
    await userEvent.click(clear);
    expect(field).toHaveValue("");
    expect(field).toHaveFocus();
    await waitFor(() => expect(last().get("words")).toBeNull());
  });

  it("Show more", async () => {
    const many = Array.from({ length: 120 }, (_, i) => item(`/Work/Note ${i + 1}.md`, { writtenAt: minutesAgo(i + 1) }));
    const { last } = api((q) => {
      const limit = Number(q.get("limit"));
      return view(many.slice(0, limit), { more: many.length > limit });
    });
    renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(50));
    expect(last().get("limit")).toBe("50");

    await userEvent.click(screen.getByRole("button", { name: "Show more" }));
    await waitFor(() => expect(rows()).toHaveLength(100));
    expect(last().get("limit")).toBe("100");
    await userEvent.click(screen.getByRole("button", { name: "Show more" }));
    await waitFor(() => expect(rows()).toHaveLength(120));
    expect(screen.queryByRole("button", { name: "Show more" })).not.toBeInTheDocument();
  });

  it("a row opens the note", async () => {
    api(() => view([certificate, sketch, garden]));
    const router = renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(3));
    await userEvent.click(within(rows()[0]).getByRole("link"));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/Pump/Certificate.md"));
  });

  it("an image opens in the lightbox, which pages through the attachments listed", async () => {
    const manual = item("/Home/manual.pdf", { kind: "pdf", title: "manual.pdf" });
    api(() => view([certificate, sketch, garden, manual]));
    const router = renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(4));
    await userEvent.click(within(rows()[1]).getByRole("link"));
    await waitFor(() => expect(router.state.location.pathname).toBe("/view/Work/Pump/sketch.png"));
    expect(router.state.location.state).toEqual({ paths: ["/Work/Pump/sketch.png", "/Home/manual.pdf"] });
  });

  it("a skill's text file opens as the tree opens it, in the lightbox", async () => {
    const script = item("/Skills/pdf/run.py", { kind: "text", title: "run.py" });
    api(() => view([script]));
    const router = renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(1));
    await userEvent.click(within(rows()[0]).getByRole("link"));
    await waitFor(() => expect(router.state.location.pathname).toBe("/view/Skills/pdf/run.py"));
  });

  it("nothing of a hidden vault", async () => {
    // The server leaves a hidden discreet vault out, chips included: the page shows what it answers, nothing more.
    api(() => view([certificate, garden]), ["Home", "Skills", "Work"]);
    renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(2));
    expect(within(screen.getByRole("group", { name: "Vaults" })).getAllByRole("button").map((b) => b.textContent)).toEqual(["Home", "Skills", "Work"]);
    expect(document.body).not.toHaveTextContent("Tagebuch");
  });

  it("stops leaving out a vault the answer no longer lists", async () => {
    let listed = ["Home", "Skills", "Work"];
    const { last } = api(() => view([certificate, sketch], { vaults: listed }));
    renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(2));
    await userEvent.click(chip("Home"));
    await waitFor(() => expect(last().getAll("skip")).toEqual(["Home"]));

    listed = ["Skills", "Work"];
    await userEvent.type(screen.getByRole("searchbox", { name: "Find by name" }), "report");
    await waitFor(() => expect(last().get("words")).toBe("report"));
    await waitFor(() => expect(within(screen.getByRole("group", { name: "Vaults" })).getAllByRole("button").map((b) => b.textContent)).toEqual(["Skills", "Work"]));
    await waitFor(() => expect(last().getAll("skip")).toEqual([]));
    expect(screen.queryByRole("button", { name: "Home" })).not.toBeInTheDocument();
  });

  it("hiding the discreet vaults clears the filters, which may name one", async () => {
    for (const change of [
      () => useDiscreet.setState({ revealed: false }),
      () => useDiscreet.setState({ names: new Set() }),
      // Marked discreet in another session: the vault leaves the list and joins those gone.
      () => useDiscreet.setState({ gone: new Set(["Work"]) }),
    ]) {
      const { last } = api(() => view([certificate, sketch, garden]), ["Home", "Skills", "Work"], () => ["Home"]);
      renderApp("/recent");
      await page();
      await waitFor(() => expect(useDiscreet.getState()).toMatchObject({ revealed: true, names: new Set(["Home"]) }));
      await waitFor(() => expect(rows()).toHaveLength(3));
      await userEvent.click(chip("Home"));
      await userEvent.type(screen.getByRole("searchbox", { name: "Find by name" }), "Home");
      await userEvent.click(within(screen.getByRole("group", { name: "Find by flag" })).getByRole("button", { name: "Red" }));
      await waitFor(() => expect(last().get("words")).toBe("Home"));

      act(change);
      expect(screen.getByRole("searchbox", { name: "Find by name" })).toHaveValue("");
      await waitFor(() => expect(chip("Home")).toHaveAttribute("aria-pressed", "true"));
      await waitFor(() => expect([last().getAll("skip"), last().get("words"), last().get("flag")]).toEqual([[], null, null]));
      cleanup();
      useDiscreet.getState().reset();
      useDiscreet.setState({ gone: new Set() });
    }
  });

  it("the discreet vaults' names coming after the words were typed leave the filters be", async () => {
    // After a reload the session and the vault list may answer after something was typed: what they bring hides nothing.
    let shown: string[] = [];
    const { last } = api(() => view([certificate, sketch, garden]), ["Home", "Skills", "Work"], () => shown);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/recent", queryClient);
    await page();
    await waitFor(() => expect(rows()).toHaveLength(3));
    await userEvent.click(chip("Skills"));
    await userEvent.type(screen.getByRole("searchbox", { name: "Find by name" }), "Pump");
    await waitFor(() => expect([last().get("words"), last().getAll("skip")]).toEqual(["Pump", ["Skills"]]));

    shown = ["Home"];
    await act(() => queryClient.invalidateQueries({ queryKey: ["session"] }));
    await act(() => queryClient.invalidateQueries({ queryKey: ["vaults"] }));
    await waitFor(() => expect(useDiscreet.getState()).toMatchObject({ revealed: true, names: new Set(["Home"]) }));
    expect(screen.getByRole("searchbox", { name: "Find by name" })).toHaveValue("Pump");
    expect(chip("Skills")).toHaveAttribute("aria-pressed", "false");
    await waitFor(() => expect([last().get("words"), last().getAll("skip")]).toEqual(["Pump", ["Skills"]]));
  });

  it("hiding the discreet vaults shows nothing kept from before, not for a moment, under any filter", async () => {
    let hidden = false;
    const diary = item("/Tagebuch/Brief.md");
    api(
      () => (hidden ? view([certificate], { vaults: ["Work"] }) : view([diary, certificate], { vaults: ["Tagebuch", "Work"] })),
      ["Tagebuch", "Work"],
      () => (hidden ? [] : ["Tagebuch"]),
    );
    renderApp("/recent");
    await page();
    await waitFor(() => expect(useDiscreet.getState()).toMatchObject({ revealed: true, names: new Set(["Tagebuch"]) }));
    await waitFor(() => expect(rows()).toHaveLength(2));
    await userEvent.click(chip("Work"));
    await waitFor(() => expect(chip("Work")).toHaveAttribute("aria-pressed", "false"));
    hidden = true;
    // The filters start again, and the rows the page had without them are not shown again while it asks.
    act(() => useDiscreet.setState({ revealed: false, names: new Set() }));
    // In the window: the vault list behind it is not the page's to empty here.
    const dialog = screen.getByRole("dialog", { name: "Recent changes" });
    expect(dialog).not.toHaveTextContent("Tagebuch");
    await waitFor(() => expect(rows()).toHaveLength(1));
    expect(dialog).not.toHaveTextContent("Tagebuch");
  });

  it("Nothing changed here yet.", async () => {
    api(() => view([]));
    renderApp("/recent");
    await page();
    expect(await screen.findByText("Nothing changed here yet.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Show more" })).not.toBeInTheDocument();
  });

  it("the route names no vault, and the address bar never carries a vault name", async () => {
    api(() => view([certificate, sketch, garden]));
    const router = renderApp("/recent");
    await page();
    await waitFor(() => expect(rows()).toHaveLength(3));
    await userEvent.click(chip("Home"));
    await userEvent.type(screen.getByRole("searchbox", { name: "Find by name" }), "Work");
    await userEvent.click(within(screen.getByRole("group", { name: "Find by flag" })).getByRole("button", { name: "Red" }));
    const { pathname, search, hash, state } = router.state.location;
    expect(pathname + search + hash).toBe("/recent");
    expect(JSON.stringify([state, { ...localStorage }, { ...sessionStorage }])).not.toMatch(/Home|Work|Pump/);
  });

  it("opens as a light window over the note, and a click on the backdrop closes it", async () => {
    api(() => view([certificate]));
    const router = renderApp(["/notes/Work/A.md"]);
    await screen.findByText("Text.");
    await navigateAway(router, "/recent");
    const dialog = await page();
    expect(screen.queryByRole("heading", { level: 1, name: "Recent changes" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Back to the notes" })).not.toBeInTheDocument();
    await userEvent.click(dialog.parentElement!.querySelector<HTMLElement>(":scope > [aria-hidden]")!);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/A.md"));
    expect(screen.queryByRole("dialog", { name: "Recent changes" })).not.toBeInTheDocument();
  });

  it("a note opened from it replaces the window, and Back shows the note before", async () => {
    api(() => view([certificate]));
    const router = renderApp(["/notes/Home/A.md"]);
    await screen.findByText("Text.");
    await navigateAway(router, "/recent");
    await page();
    await userEvent.click(await screen.findByRole("link", { name: /Certificate/ }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/Pump/Certificate.md"));
    expect(router.state.historyAction).toBe("REPLACE");
    expect(screen.queryByRole("dialog", { name: "Recent changes" })).not.toBeInTheDocument();
    await act(() => router.navigate(-1));
    expect(router.state.location.pathname).toBe("/notes/Home/A.md");
    expect(screen.queryByRole("dialog", { name: "Recent changes" })).not.toBeInTheDocument();
  });

  it("an attachment opens in the lightbox, a full page, replacing the window", async () => {
    api(() => view([sketch]));
    const router = renderApp(["/notes/Home/A.md"]);
    await screen.findByText("Text.");
    await navigateAway(router, "/recent");
    await page();
    await userEvent.click(await screen.findByRole("link", { name: /sketch\.png/ }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/view/Work/Pump/sketch.png"));
    expect(router.state.historyAction).toBe("REPLACE");
    await act(() => router.navigate(-1));
    expect(router.state.location.pathname).toBe("/notes/Home/A.md");
  });
});
