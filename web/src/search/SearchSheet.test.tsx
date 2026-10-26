// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const scrollIntoView = vi.fn();
beforeEach(() => {
  scrollIntoView.mockClear();
  Element.prototype.scrollIntoView = scrollIntoView;
});

const hit = (path: string, over: Record<string, unknown> = {}) => ({
  path,
  address: "",
  startLine: 1,
  endLine: 3,
  snippet: "a snippet",
  score: 0.5,
  reason: "exact",
  ...over,
});

type Searched = { q: string; mode: string; signal: AbortSignal };

function api(results: (q: string, mode: string) => unknown, searched: Searched[] = [], extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }]),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    "GET /api/notes/*": (r) =>
      json(200, {
        path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)),
        content: "# Plan\n\ntext\n\n## Goals\n\nmore\n",
        version: "v1",
        lastWriter: "ada",
        lastWriteAt: "2026-10-25T09:00:00Z",
      }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/search": (r) => {
      const u = new URL(r.url);
      const q = u.searchParams.get("q")!;
      const mode = u.searchParams.get("mode")!;
      searched.push({ q, mode, signal: r.signal });
      return json(200, results(q, mode));
    },
    ...extra,
  });
}

const view = (results: unknown[], notice: string | null = null) => ({ results, notIndexed: 0, notice });
const sheet = () => screen.getByRole("dialog", { name: "Search" });
const input = () => within(sheet()).getByRole("searchbox", { name: "Search" });

async function openSheet(path = "/notes/Work/inbox.md") {
  renderApp(path);
  await userEvent.click(await screen.findByRole("button", { name: /^Search/ }));
  // Loaded on first use.
  return screen.findByRole("dialog", { name: "Search" });
}

describe("the search sheet", () => {
  it("has the input and the Hybrid, Meaning and Exact chips at the bottom, below the results", async () => {
    api(() => view([]));
    await openSheet();
    expect(input()).toHaveClass("text-[16px]");
    const chips = within(sheet()).getByRole("group", { name: "Search mode" });
    expect(within(chips).getAllByRole("button").map((b) => b.textContent)).toEqual(["Hybrid", "Meaning", "Exact"]);
    expect(within(chips).getByRole("button", { name: "Hybrid" })).toHaveAttribute("aria-pressed", "true");
    // The input and chips come after the results in the document, so they sit under them.
    const results = within(sheet()).getByRole("region", { name: "Results" });
    expect(results.compareDocumentPosition(input()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(results.compareDocumentPosition(chips) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(input()).toHaveFocus();
  });

  it("closes from the button in its header, giving the focus back to the dock", async () => {
    api(() => view([]));
    await openSheet();
    const close = within(sheet()).getByRole("button", { name: "Close search" });
    expect(within(sheet()).getByRole("banner")).toContainElement(close);
    await userEvent.click(close);
    expect(screen.queryByRole("dialog", { name: "Search" })).not.toBeInTheDocument();
    expect(useEditor.getState().overlay).toBe("none");
    expect(screen.getByRole("button", { name: /^Search/ })).toHaveFocus();
  });

  it("takes the focus inside the tap, for iOS to raise the keyboard, and hands it to the sheet's input", async () => {
    api(() => view([]));
    renderApp("/notes/Work/inbox.md");
    const search = await screen.findByRole("button", { name: /^Search/ });
    const focused: Element[] = [];
    const record = (e: FocusEvent) => focused.push(e.target as Element);
    document.addEventListener("focusin", record);
    fireEvent.click(search);
    await waitFor(() => expect(input()).toHaveFocus());
    document.removeEventListener("focusin", record);
    // First the stand-in, within the click itself; then the sheet's input.
    expect(focused[0]).toHaveAttribute("data-focus-return", "dock-search");
    expect(focused[0].tagName).toBe("INPUT");
    expect(focused.at(-1)).toBe(input());
    await userEvent.keyboard("{Escape}");
    expect(screen.getByRole("button", { name: /^Search/ })).toHaveFocus();
  });

  it("closes on Escape too", async () => {
    api(() => view([]));
    await openSheet();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "Search" })).not.toBeInTheDocument();
  });

  it("shows the five recent notes while the query is empty, and opens one", async () => {
    api(() => view([]));
    const paths = ["/Work/a.md", "/Work/b.md", "/Work/c.md", "/Work/d.md", "/Work/e.md", "/Work/f.md"];
    renderApp("/notes/Work/inbox.md");
    await screen.findByRole("heading", { name: "inbox" });
    act(() => paths.forEach((p) => useEditor.getState().openNote(p)));
    act(() => useEditor.getState().setOverlay("search"));
    const list = within(await screen.findByRole("dialog", { name: "Search" })).getByRole("list", { name: "Recent notes" });
    expect(within(list).getAllByRole("listitem")).toHaveLength(5);
    await userEvent.click(within(list).getByRole("button", { name: /^d/ }));
    expect(useEditor.getState().currentPath).toBe("/Work/d.md");
    expect(useEditor.getState().overlay).toBe("none");
  });

  it("debounces by 200 ms and cancels the request of an older query", async () => {
    const searched: Searched[] = [];
    api(() => view([hit("/Work/plan.md")]), searched);
    await openSheet();
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });

    fireEvent.change(input(), { target: { value: "pl" } });
    await act(() => vi.advanceTimersByTimeAsync(199));
    fireEvent.change(input(), { target: { value: "plan" } });
    await act(() => vi.advanceTimersByTimeAsync(199));
    expect(searched).toHaveLength(0);
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(searched.map((s) => s.q)).toEqual(["plan"]);
    // Let the response settle on the fake clock before the real one is back.
    await act(() => vi.advanceTimersByTimeAsync(50));
    vi.useRealTimers();
    expect(await screen.findByText("plan")).toBeInTheDocument();
  });

  it("never shows the results of an older query after a newer one", async () => {
    const searched: Searched[] = [];
    let release: () => void = () => {};
    const gate = new Promise<void>((r) => (release = r));
    fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }]),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
      "GET /api/notes/*": () => json(200, { path: "/Work/inbox.md", content: "x", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
      "GET /api/links/*": () => json(200, []),
      "GET /api/backlinks/*": () => json(200, []),
      "GET /api/changes": () => json(200, []),
      "GET /api/search": async (r) => {
        const q = new URL(r.url).searchParams.get("q")!;
        searched.push({ q, mode: "", signal: r.signal });
        if (q === "old") await gate;
        return json(200, view([hit(q === "old" ? "/Work/old-result.md" : "/Work/new-result.md")]));
      },
    });
    await openSheet();
    await userEvent.type(input(), "old");
    await waitFor(() => expect(searched).toHaveLength(1));
    await userEvent.clear(input());
    await userEvent.type(input(), "new");
    expect(await screen.findByText("new-result")).toBeInTheDocument();
    release();
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByText("old-result")).not.toBeInTheDocument();
  });

  it("shows the count, the mode and its explanation, and each result's title, place, snippet, score and reason", async () => {
    api(() =>
      view([
        hit("/Work/projects/plan.md", { snippet: "the plan for **spring**", score: 0.724, reason: "all words" }),
        hit("/Work/inbox.md", { score: 3, reason: "title" }),
      ]),
    );
    await openSheet();
    await userEvent.type(input(), "plan");
    const header = await within(sheet()).findByRole("heading", { name: /2 results/ });
    expect(header).toHaveTextContent("hybrid");
    expect(within(sheet()).getByText("Words and meaning together.")).toBeInTheDocument();

    const card = within(sheet()).getByRole("button", { name: /plan/ });
    expect(within(card).getByText("plan")).toBeInTheDocument();
    expect(within(card).getByText("Work / projects")).toBeInTheDocument();
    expect(within(card).getByText("the plan for spring")).toBeInTheDocument();
    expect(within(card).getByText("72%")).toBeInTheDocument();
    expect(within(card).getByText("all words")).toBeInTheDocument();
    expect(within(card).getByRole("meter")).toHaveAttribute("aria-valuenow", "72");
    // A score above 1 is clamped.
    const other = within(sheet()).getByRole("button", { name: /inbox/ });
    expect(within(other).getByText("100%")).toBeInTheDocument();
    expect(within(other).getByText("title")).toBeInTheDocument();
  });

  it("shows no score or bar for a result ranked by words only (score null), but keeps its reason", async () => {
    api(() => view([hit("/Work/plan.md", { score: null, reason: "all words" })]));
    await openSheet();
    await userEvent.type(input(), "plan");
    const card = await within(sheet()).findByRole("button", { name: /plan/ });
    expect(within(card).getByText("all words")).toBeInTheDocument();
    expect(within(card).queryByRole("meter")).not.toBeInTheDocument();
    expect(within(card).queryByText(/%/)).not.toBeInTheDocument();
  });

  it("searches in the chosen mode and says so", async () => {
    const searched: Searched[] = [];
    api(() => view([hit("/Work/plan.md")]), searched);
    await openSheet();
    await userEvent.click(within(sheet()).getByRole("button", { name: "Exact" }));
    await userEvent.type(input(), "plan");
    await waitFor(() => expect(searched.at(-1)).toMatchObject({ q: "plan", mode: "exact" }));
    expect(await within(sheet()).findByRole("heading", { name: /1 result\b/ })).toHaveTextContent("exact");
  });

  it("offers 'Search by meaning instead' on no results in Exact mode, keeping the query", async () => {
    const searched: Searched[] = [];
    api((_, mode) => view(mode === "meaning" ? [hit("/Work/plan.md", { reason: "meaning" })] : []), searched);
    await openSheet();
    await userEvent.click(within(sheet()).getByRole("button", { name: "Exact" }));
    await userEvent.type(input(), "gardening");
    await userEvent.click(await within(sheet()).findByRole("button", { name: "Search by meaning instead" }));
    expect(useEditor.getState().searchMode).toBe("meaning");
    expect(input()).toHaveValue("gardening");
    expect(within(sheet()).getByRole("button", { name: "Meaning" })).toHaveAttribute("aria-pressed", "true");
    expect(await within(sheet()).findByText("plan")).toBeInTheDocument();
    // In the header and as the result's reason.
    expect(within(sheet()).getAllByText("meaning")).toHaveLength(2);
  });

  it("does not offer it in Hybrid mode, and shows the server's notice", async () => {
    api(() => view([], "Ranked by words only: nothing is indexed for meaning yet."));
    await openSheet();
    await userEvent.type(input(), "nothing");
    expect(await within(sheet()).findByText("No notes found.")).toBeInTheDocument();
    expect(within(sheet()).getByText("Ranked by words only: nothing is indexed for meaning yet.")).toBeInTheDocument();
    expect(within(sheet()).queryByRole("button", { name: "Search by meaning instead" })).not.toBeInTheDocument();
  });

  it("opens a result: closes the sheet, opens the note, expands its folders and scrolls to its section", async () => {
    api(() => view([hit("/Work/projects/deep/plan.md", { address: "#Plan#Goals", startLine: 5, endLine: 7 })]));
    await openSheet();
    await userEvent.type(input(), "goals");
    await userEvent.click(await within(sheet()).findByRole("button", { name: /plan/ }));
    expect(screen.queryByRole("dialog", { name: "Search" })).not.toBeInTheDocument();
    const s = useEditor.getState();
    expect(s.currentPath).toBe("/Work/projects/deep/plan.md");
    expect(s.mode).toBe("read");
    expect(s.expandedFolders).toMatchObject({ "/Work/projects": true, "/Work/projects/deep": true });
    await screen.findByRole("heading", { name: "Goals" });
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalled());
    expect(scrollIntoView.mock.contexts.at(-1)).toBe(screen.getByRole("heading", { name: "Goals" }));
  });

  it("opens a result without a section at the top", async () => {
    api(() => view([hit("/Work/plan.md")]));
    await openSheet();
    await userEvent.type(input(), "plan");
    await userEvent.click(await within(sheet()).findByRole("button", { name: /plan/ }));
    await screen.findByRole("heading", { name: "Goals" });
    expect(scrollIntoView).not.toHaveBeenCalled();
  });

  it("closes on Escape", async () => {
    api(() => view([]));
    await openSheet();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "Search" })).not.toBeInTheDocument();
  });

});

describe("search in earlier versions", () => {
  const earlier = { path: "/Work/plan.md", version: "old1", writtenAt: "2026-10-20T09:00:00Z", writer: "liz", startLine: 0, endLine: 3, snippet: "Zebras first.", before: null, beforeLost: false, writerKind: "agent" };

  it("the_switch_is_off_by_default", async () => {
    const asked: string[] = [];
    api(() => view([hit("/Work/plan.md")]), [], { "GET /api/search/earlier": (r) => (asked.push(r.url), json(200, [earlier])) });
    await openSheet();
    expect(within(sheet()).getByRole("switch", { name: "Include earlier versions" })).toHaveAttribute("aria-checked", "false");

    await userEvent.type(input(), "zebras");
    await within(sheet()).findByText("1 result", { exact: false });

    expect(asked).toEqual([]);
    expect(within(sheet()).queryByText(/Earlier version ·/)).not.toBeInTheDocument();
  });

  it("the_switch_adds_earlier_versions", async () => {
    const asked: URL[] = [];
    api(() => view([hit("/Work/plan.md")]), [], {
      "GET /api/search/earlier": (r) => (asked.push(new URL(r.url)), json(200, [earlier])),
    });
    await openSheet();
    await userEvent.type(input(), "zebras");
    await userEvent.click(within(sheet()).getByRole("switch", { name: "Include earlier versions" }));

    const list = await within(sheet()).findByRole("list", { name: "Earlier versions" });
    expect(within(list).getByText(/^Earlier version · /)).toBeInTheDocument();
    expect(list).toHaveTextContent("by Agent liz");
    expect(asked.at(-1)!.searchParams.get("q")).toBe("zebras");

    // A tap compares that version with today's text.
    await userEvent.click(within(list).getByRole("button"));
    const compare = await screen.findByRole("dialog", { name: "plan.md" });
    expect(within(compare).getByRole("button", { name: "Against now" })).toHaveAttribute("aria-pressed", "true");
    expect(within(compare).getByText("This version")).toBeInTheDocument();
  });
});
