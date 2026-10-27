// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, createEvent, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

type Card = { path: string; title: string; due: string | null; overdue: boolean; tags: string[]; reason: string | null };
const card = (title: string, extra: Partial<Card> = {}): Card => ({ path: `/Work/Board/${title}.md`, title, due: null, overdue: false, tags: [], reason: null, ...extra });
const column = (name: string, cards: Card[], flags: { archivable?: boolean; needsReason?: boolean } = {}) => ({
  name,
  archivable: !!flags.archivable,
  needsReason: !!flags.needsReason,
  cards,
});

const board = (problems: { path: string; what: string }[] = []) => ({
  title: "Work",
  instructions: null,
  columns: [
    column("Backlog", [card("Write spec", { due: "2026-10-20", overdue: true, tags: ["docs"] }), card("Plan", { due: "2026-11-02" }), card("Review")]),
    column("Doing", []),
    column("Waiting", [card("Order parts", { reason: "Supplier closed until Monday" })], { needsReason: true }),
    column("Done", [card("Ship")], { archivable: true }),
  ],
  problems,
});

/** The board after Plan went to Doing. */
const moved = () => {
  const b = board();
  b.columns[0].cards = b.columns[0].cards.filter((c) => c.title !== "Plan");
  b.columns[1].cards = [card("Plan")];
  return b;
};

function api(extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults/Work/board": () => json(200, board()),
    ...extra,
  });
}

/** The JSON bodies of the requests to `path`, in order. */
const bodies = async (fetch: ReturnType<typeof fakeApi>, path: string) =>
  Promise.all(
    fetch.mock.calls
      .map(([r]) => r as Request)
      .filter((r) => r.method === "POST" && new URL(r.url).pathname === path)
      .map((r) => r.clone().json()),
  );

const lane = (name: string) => screen.getByRole("list", { name });

function laptop() {
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: query === "(min-width: 64rem)",
    media: query,
    addEventListener() {},
    removeEventListener() {},
  }));
}

async function chooseFromMenu(title: string, item: string) {
  await userEvent.click(await screen.findByRole("button", { name: `Actions for ${title}` }));
  await userEvent.click(screen.getByRole("menuitem", { name: item }));
}

describe("the board", () => {
  it("shows the lanes and their cards, with due dates and tags", async () => {
    api();
    renderApp("/board/Work");

    const backlog = await screen.findByRole("list", { name: "Backlog" });
    expect(screen.getByRole("heading", { level: 1, name: "Work" })).toBeInTheDocument();
    const items = within(backlog).getAllByRole("listitem");
    expect(items).toHaveLength(3);
    expect(within(items[0]).getByRole("link", { name: "Write spec" })).toHaveAttribute("href", "/notes/Work/Board/Write%20spec.md");
    expect(items[0]).toHaveTextContent("2026-10-20");
    expect(items[0]).toHaveTextContent("overdue");
    expect(items[0]).toHaveTextContent("#docs");
    expect(items[1]).toHaveTextContent("2026-11-02");
    expect(items[1]).not.toHaveTextContent("overdue");
    expect(within(lane("Waiting")).getByRole("listitem")).toHaveTextContent("Supplier closed until Monday");

    const tabs = screen.getByRole("navigation", { name: "Columns" });
    expect(within(tabs).getByRole("button", { name: "Backlog 3" })).toBeInTheDocument();
    expect(within(tabs).getByRole("button", { name: "Doing 0" })).toBeInTheDocument();
    // The archivable lane, collapsed on a laptop, shows its name and count.
    expect(screen.getByRole("button", { name: "Show Done, 1 card" })).toHaveAttribute("aria-expanded", "false");
  });

  it("says so when the vault has no board, and makes one", async () => {
    let created = false;
    api({
      "GET /api/vaults/Work/board": () => (created ? json(200, board()) : json(404, { error: "There is no board in Work." })),
      "POST /api/vaults/Work/board": () => {
        created = true;
        return json(201, board());
      },
    });
    renderApp("/board/Work");

    expect(await screen.findByText("There is no board in Work.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Create board" }));
    expect(await screen.findByRole("list", { name: "Backlog" })).toBeInTheDocument();
  });

  it("moves a card with Move to… and shows the new board", async () => {
    const fetch = api({ "POST /api/vaults/Work/board/move": () => json(200, moved()) });
    renderApp("/board/Work");
    await chooseFromMenu("Plan", "Move to…");

    const sheet = await screen.findByRole("dialog", { name: "Move Plan" });
    await userEvent.click(within(sheet).getByRole("button", { name: "Doing" }));
    await userEvent.click(within(sheet).getByRole("button", { name: "To the top" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(within(lane("Doing")).getByRole("link", { name: "Plan" })).toBeInTheDocument();
    expect(within(lane("Backlog")).queryByRole("link", { name: "Plan" })).not.toBeInTheDocument();
    expect(await bodies(fetch, "/api/vaults/Work/board/move")).toEqual([{ card: "Plan", column: "Doing", position: "top" }]);
  });

  it("says why a move didn't go through, in the sheet", async () => {
    api({ "POST /api/vaults/Work/board/move": () => json(409, { error: "/Work/Board/Board.md can't be read, so the board can't change: columns is missing" }) });
    renderApp("/board/Work");
    await chooseFromMenu("Plan", "Move to…");
    const sheet = await screen.findByRole("dialog", { name: "Move Plan" });
    await userEvent.click(within(sheet).getByRole("button", { name: "Doing" }));
    await userEvent.click(within(sheet).getByRole("button", { name: "To the bottom" }));

    expect(await within(sheet).findByRole("alert")).toHaveTextContent("columns is missing");
  });

  it("sends Up and Down as before and after the neighbouring card", async () => {
    const fetch = api({ "POST /api/vaults/Work/board/move": () => json(200, board()) });
    renderApp("/board/Work");

    await chooseFromMenu("Plan", "Up");
    await chooseFromMenu("Plan", "Down");

    await waitFor(async () =>
      expect(await bodies(fetch, "/api/vaults/Work/board/move")).toEqual([
        { card: "Plan", column: "Backlog", position: "before:Write spec" },
        { card: "Plan", column: "Backlog", position: "after:Review" },
      ]),
    );
    await userEvent.click(screen.getByRole("button", { name: "Actions for Write spec" }));
    expect(screen.getByRole("menuitem", { name: "Up" })).toBeDisabled();
    expect(screen.getByRole("menuitem", { name: "Down" })).toBeEnabled();
  });

  it("shows a failed Up in the lane", async () => {
    api({ "POST /api/vaults/Work/board/move": () => json(404, { error: "no card 'Plan' on the board" }) });
    renderApp("/board/Work");
    await chooseFromMenu("Plan", "Up");

    expect(await screen.findByRole("alert")).toHaveTextContent("no card 'Plan' on the board");
  });

  it("asks for the reason before a move into a reason column", async () => {
    const fetch = api({ "POST /api/vaults/Work/board/move": () => json(200, board()) });
    renderApp("/board/Work");

    await chooseFromMenu("Plan", "Move to…");
    await userEvent.click(within(await screen.findByRole("dialog", { name: "Move Plan" })).getByRole("button", { name: "Waiting" }));
    await userEvent.click(screen.getByRole("button", { name: "To the bottom" }));
    const ask = await screen.findByRole("dialog", { name: "Why does Plan go to Waiting?" });
    await userEvent.click(within(ask).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await bodies(fetch, "/api/vaults/Work/board/move")).toEqual([]);

    await chooseFromMenu("Plan", "Move to…");
    await userEvent.click(within(await screen.findByRole("dialog", { name: "Move Plan" })).getByRole("button", { name: "Waiting" }));
    await userEvent.click(screen.getByRole("button", { name: "To the top" }));
    const again = await screen.findByRole("dialog", { name: "Why does Plan go to Waiting?" });
    expect(within(again).getByRole("button", { name: "Move" })).toBeDisabled();
    await userEvent.type(within(again).getByRole("textbox", { name: "Reason" }), "Waiting for the review");
    await userEvent.click(within(again).getByRole("button", { name: "Move" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(await bodies(fetch, "/api/vaults/Work/board/move")).toEqual([{ card: "Plan", column: "Waiting", position: "top", reason: "Waiting for the review" }]);
  });

  it("shows problems in a red bar, each leading to its note", async () => {
    api({ "GET /api/vaults/Work/board": () => json(200, board([{ path: "/Work/Board/Stray.md", what: "column 'Later' is not on the board" }])) });
    renderApp("/board/Work");

    const problems = await screen.findByRole("region", { name: "Problems" });
    expect(problems).toHaveTextContent("column 'Later' is not on the board");
    expect(within(problems).getByRole("link", { name: "Stray" })).toHaveAttribute("href", "/notes/Work/Board/Stray.md");
  });

  it("adds a card to a lane with + Card", async () => {
    const fetch = api({
      "POST /api/vaults/Work/board/cards": () => {
        const b = board();
        b.columns[1].cards = [card("Call the printer")];
        return json(201, b);
      },
    });
    renderApp("/board/Work");

    await userEvent.click(await screen.findByRole("button", { name: "Add a card to Doing" }));
    const form = screen.getByRole("form", { name: "New card in Doing" });
    await userEvent.type(within(form).getByRole("textbox", { name: "Title" }), "Call the printer");
    await userEvent.type(within(form).getByRole("textbox", { name: "Text (optional)" }), "About the flyers.");
    await userEvent.click(within(form).getByRole("button", { name: "Add" }));

    expect(await within(lane("Doing")).findByRole("link", { name: "Call the printer" })).toBeInTheDocument();
    expect(await bodies(fetch, "/api/vaults/Work/board/cards")).toEqual([{ title: "Call the printer", column: "Doing", text: "About the flyers." }]);
    // No + Card in a reason column: a card can't start there.
    expect(screen.queryByRole("button", { name: "Add a card to Waiting" })).not.toBeInTheDocument();
  });

  it("moves a card by drag and drop on a laptop", async () => {
    laptop();
    const fetch = api({ "POST /api/vaults/Work/board/move": () => json(200, moved()) });
    renderApp("/board/Work");
    const plan = (await screen.findByRole("link", { name: "Plan" })).closest("li")!;
    expect(plan).toHaveAttribute("draggable", "true");

    const data = new Map<string, string>();
    const dataTransfer = { setData: (k: string, v: string) => data.set(k, v), getData: (k: string) => data.get(k) ?? "", effectAllowed: "", dropEffect: "" };
    fireEvent.dragStart(plan, { dataTransfer });
    fireEvent.dragOver(lane("Doing"), { dataTransfer });
    fireEvent.drop(lane("Doing"), { dataTransfer });

    await waitFor(() => expect(within(lane("Doing")).getByRole("link", { name: "Plan" })).toBeInTheDocument());

    // Onto a card: before it when above its middle, after it when below.
    const review = within(lane("Backlog")).getByRole("link", { name: "Review" }).closest("li")!;
    vi.spyOn(review, "getBoundingClientRect").mockReturnValue({ top: 100, height: 40, bottom: 140, left: 0, right: 200, width: 200, x: 0, y: 100, toJSON() {} });
    const plan2 = within(lane("Doing")).getByRole("link", { name: "Plan" }).closest("li")!;
    fireEvent.dragStart(plan2, { dataTransfer });
    // jsdom has no DragEvent, so the pointer's height is set by hand.
    const drop = createEvent.drop(review, { dataTransfer });
    Object.defineProperty(drop, "clientY", { value: 110 });
    fireEvent(review, drop);

    await waitFor(async () =>
      expect(await bodies(fetch, "/api/vaults/Work/board/move")).toEqual([
        { card: "Plan", column: "Doing", position: "bottom" },
        { card: "Plan", column: "Backlog", position: "before:Review" },
      ]),
    );
  });

  it("asks for the reason before a drop into a reason column", async () => {
    laptop();
    const fetch = api({ "POST /api/vaults/Work/board/move": () => json(200, board()) });
    renderApp("/board/Work");
    const plan = (await screen.findByRole("link", { name: "Plan" })).closest("li")!;
    const data = new Map<string, string>();
    const dataTransfer = { setData: (k: string, v: string) => data.set(k, v), getData: (k: string) => data.get(k) ?? "" };
    fireEvent.dragStart(plan, { dataTransfer });
    fireEvent.drop(lane("Waiting"), { dataTransfer });

    const ask = await screen.findByRole("dialog", { name: "Why does Plan go to Waiting?" });
    await userEvent.type(within(ask).getByRole("textbox", { name: "Reason" }), "Blocked");
    await userEvent.click(within(ask).getByRole("button", { name: "Move" }));
    await waitFor(async () => expect(await bodies(fetch, "/api/vaults/Work/board/move")).toEqual([{ card: "Plan", column: "Waiting", position: "bottom", reason: "Blocked" }]));
  });

  it("follows changes made elsewhere", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let reads = 0;
      api({ "GET /api/vaults/Work/board": () => json(200, ++reads === 1 ? board() : moved()) });
      renderApp("/board/Work");
      expect(await within(await screen.findByRole("list", { name: "Backlog" })).findByRole("link", { name: "Plan" })).toBeInTheDocument();

      await act(() => vi.advanceTimersByTimeAsync(5_000));

      expect(await within(lane("Doing")).findByRole("link", { name: "Plan" })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it("drops onto the collapsed strip of an archivable lane on a laptop", async () => {
    laptop();
    const fetch = api({ "POST /api/vaults/Work/board/move": () => json(200, board()) });
    renderApp("/board/Work");
    const plan = (await screen.findByRole("link", { name: "Plan" })).closest("li")!;
    const data = new Map<string, string>();
    const dataTransfer = { setData: (k: string, v: string) => data.set(k, v), getData: (k: string) => data.get(k) ?? "" };
    fireEvent.dragStart(plan, { dataTransfer });
    const strip = screen.getByRole("button", { name: "Show Done, 1 card" });
    expect(fireEvent.dragOver(strip, { dataTransfer })).toBe(false);
    fireEvent.drop(strip, { dataTransfer });

    await waitFor(async () => expect(await bodies(fetch, "/api/vaults/Work/board/move")).toEqual([{ card: "Plan", column: "Done", position: "bottom" }]));
  });

  it("renames a column", async () => {
    const fetch = api({
      "POST /api/vaults/Work/board/rename-column": () => {
        const b = board();
        b.columns[1].name = "In progress";
        return json(200, b);
      },
    });
    renderApp("/board/Work");
    await userEvent.click(await screen.findByRole("button", { name: "Column actions for Doing" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Rename…" }));
    const dialog = screen.getByRole("dialog", { name: "Rename Doing" });
    const name = within(dialog).getByRole("textbox", { name: "New name" });
    expect(within(dialog).getByRole("button", { name: "Rename" })).toBeDisabled();
    await userEvent.clear(name);
    await userEvent.type(name, "In progress");
    await userEvent.click(within(dialog).getByRole("button", { name: "Rename" }));

    expect(await screen.findByRole("list", { name: "In progress" })).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await bodies(fetch, "/api/vaults/Work/board/rename-column")).toEqual([{ from: "Doing", to: "In progress" }]);
  });

  it("says why a rename didn't go through, in the dialog", async () => {
    api({ "POST /api/vaults/Work/board/rename-column": () => json(400, { error: "there is a column named Backlog already" }) });
    renderApp("/board/Work");
    await userEvent.click(await screen.findByRole("button", { name: "Column actions for Doing" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Rename…" }));
    const dialog = screen.getByRole("dialog", { name: "Rename Doing" });
    await userEvent.clear(within(dialog).getByRole("textbox", { name: "New name" }));
    await userEvent.type(within(dialog).getByRole("textbox", { name: "New name" }), "backlog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Rename" }));

    expect(await within(dialog).findByRole("alert")).toHaveTextContent("there is a column named Backlog already");
  });

  it("changes a card's tags and sends nothing for the unchanged due date", async () => {
    const fetch = api({ "POST /api/vaults/Work/board/update": () => json(200, board()) });
    renderApp("/board/Work");
    await chooseFromMenu("Write spec", "Due and tags…");
    const dialog = screen.getByRole("dialog", { name: "Due and tags of Write spec" });
    expect(within(dialog).getByLabelText("Due")).toHaveValue("2026-10-20");
    const tags = within(dialog).getByRole("textbox", { name: "Tags" });
    expect(tags).toHaveValue("docs");
    expect(within(dialog).getByRole("button", { name: "Save" })).toBeDisabled();
    await userEvent.type(tags, ", #urgent later");
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(await bodies(fetch, "/api/vaults/Work/board/update")).toEqual([{ card: "Write spec", tags: ["docs", "urgent", "later"] }]);
  });

  it("clears a due date and tags the user emptied, and sets a new due date", async () => {
    const fetch = api({ "POST /api/vaults/Work/board/update": () => json(200, board()) });
    renderApp("/board/Work");
    await chooseFromMenu("Write spec", "Due and tags…");
    let dialog = screen.getByRole("dialog", { name: "Due and tags of Write spec" });
    fireEvent.change(within(dialog).getByLabelText("Due"), { target: { value: "" } });
    await userEvent.clear(within(dialog).getByRole("textbox", { name: "Tags" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    await chooseFromMenu("Review", "Due and tags…");
    dialog = screen.getByRole("dialog", { name: "Due and tags of Review" });
    fireEvent.change(within(dialog).getByLabelText("Due"), { target: { value: "2026-11-05" } });
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    expect(await bodies(fetch, "/api/vaults/Work/board/update")).toEqual([
      { card: "Write spec", clearDue: true, clearTags: true },
      { card: "Review", due: "2026-11-05" },
    ]);
  });

  it("says why an update didn't go through, in the dialog", async () => {
    api({ "POST /api/vaults/Work/board/update": () => json(400, { error: "a tag can't contain ':'" }) });
    renderApp("/board/Work");
    await chooseFromMenu("Plan", "Due and tags…");
    const dialog = screen.getByRole("dialog", { name: "Due and tags of Plan" });
    await userEvent.type(within(dialog).getByRole("textbox", { name: "Tags" }), "a:b");
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));

    expect(await within(dialog).findByRole("alert")).toHaveTextContent("a tag can't contain ':'");
  });

  it("adds a card with a due date and tags", async () => {
    const fetch = api({ "POST /api/vaults/Work/board/cards": () => json(201, board()) });
    renderApp("/board/Work");
    await userEvent.click(await screen.findByRole("button", { name: "Add a card to Doing" }));
    const form = screen.getByRole("form", { name: "New card in Doing" });
    await userEvent.type(within(form).getByRole("textbox", { name: "Title" }), "Print flyers");
    await userEvent.click(within(form).getByRole("button", { name: "More" }));
    fireEvent.change(within(form).getByLabelText("Due (optional)"), { target: { value: "2026-11-05" } });
    await userEvent.type(within(form).getByRole("textbox", { name: "Tags (optional)" }), "#print, flyers");
    await userEvent.click(within(form).getByRole("button", { name: "Add" }));

    await waitFor(async () =>
      expect(await bodies(fetch, "/api/vaults/Work/board/cards")).toEqual([{ title: "Print flyers", column: "Doing", due: "2026-11-05", tags: ["print", "flyers"] }]),
    );
  });

  it("doesn't poll while a change is under way, so the old board can't come back over the new one", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let reads = 0;
      let answer: (r: Response) => void = () => {};
      api({
        "GET /api/vaults/Work/board": () => {
          reads++;
          return json(200, board());
        },
        "POST /api/vaults/Work/board/move": () => new Promise<Response>((resolve) => (answer = resolve)),
      });
      renderApp("/board/Work");
      await chooseFromMenu("Plan", "Up");
      const before = reads;

      await act(() => vi.advanceTimersByTimeAsync(12_000));
      expect(reads).toBe(before);

      answer(json(200, moved()));
      expect(await within(lane("Doing")).findByRole("link", { name: "Plan" })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it("drags nothing on a phone", async () => {
    api();
    renderApp("/board/Work");
    const plan = (await screen.findByRole("link", { name: "Plan" })).closest("li")!;
    expect(plan).not.toHaveAttribute("draggable", "true");
  });
});

describe("the board from the vault menu", () => {
  const shell = (tree: unknown, extra: Parameters<typeof fakeApi>[0] = {}) =>
    api({
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal" }]),
      "GET /api/embeddings": () => json(200, []),
      "GET /api/vaults/Work/tree": () => json(200, tree),
      "GET /api/notes/Work/inbox.md": () => json(200, { path: "/Work/inbox.md", content: "# Inbox\n", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z", current: true }),
      "GET /api/version/Work/inbox.md": () => json(200, { path: "/Work/inbox.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
      "GET /api/links/*": () => json(200, []),
      "GET /api/backlinks/*": () => json(200, []),
      "GET /api/changes": () => json(200, []),
      ...extra,
    });
  const inbox = { name: "inbox.md", path: "/Work/inbox.md", size: 8 };

  async function openVaultMenu() {
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Actions for Work" }));
  }

  it("creates the board and opens it", async () => {
    let created = false;
    const fetch = shell(
      { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [inbox] } },
      {
        "GET /api/vaults/Work/board": () => (created ? json(200, board()) : json(404, { error: "There is no board in Work." })),
        "POST /api/vaults/Work/board": () => {
          created = true;
          return json(201, board());
        },
      },
    );
    await openVaultMenu();
    expect(screen.queryByRole("menuitem", { name: "Board" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("menuitem", { name: "Create board" }));

    expect(await screen.findByRole("list", { name: "Backlog" })).toBeInTheDocument();
    expect(fetch.mock.calls.some(([r]) => (r as Request).method === "POST" && new URL((r as Request).url).pathname === "/api/vaults/Work/board")).toBe(true);
  });

  it("opens the board the vault has", async () => {
    const boardFolder = { name: "Board", path: "/Work/Board", folders: [], notes: [{ name: "Board.md", path: "/Work/Board/Board.md", size: 80 }] };
    shell({ damaged: 0, root: { name: "Work", path: "/Work", folders: [boardFolder], notes: [inbox] } });
    await openVaultMenu();
    expect(screen.queryByRole("menuitem", { name: "Create board" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("menuitem", { name: "Board" }));

    expect(await screen.findByRole("list", { name: "Backlog" })).toBeInTheDocument();
  });
});

describe("a checkpoint's board", () => {
  const checkpoint = { name: "before-cleanup", scope: "/Work/", createdBy: "ada", createdAt: "2026-10-19T12:02:00Z", notes: 3 };

  it("is linked from the checkpoint and read-only", async () => {
    const fetch = api({
      "GET /api/vaults/Work/checkpoints/before-cleanup/tree": () =>
        json(200, {
          checkpoint,
          root: { name: "Work", path: "/Work", folders: [{ name: "Board", path: "/Work/Board", folders: [], notes: [{ name: "Board.md", path: "/Work/Board/Board.md", size: 80 }] }], notes: [] },
        }),
    });
    renderApp("/checkpoints/Work/before-cleanup");
    await userEvent.click(await screen.findByRole("link", { name: "Show the board" }));

    expect(await screen.findByRole("list", { name: "Backlog" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Checkpoint" })).toHaveTextContent("read only");
    expect(screen.queryByRole("button", { name: /^Actions for/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Add a card/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Column actions for/ })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Plan" })).toHaveAttribute("href", "/checkpoints/Work/before-cleanup/Board/Plan.md");
    expect(screen.getByRole("link", { name: "Plan" }).closest("li")).not.toHaveAttribute("draggable", "true");
    const read = fetch.mock.calls.map(([r]) => new URL((r as Request).url)).find((u) => u.pathname === "/api/vaults/Work/board")!;
    expect(read.searchParams.get("checkpoint")).toBe("before-cleanup");
  });

  it("isn't offered when the checkpoint holds none", async () => {
    api({
      "GET /api/vaults/Work/checkpoints/before-cleanup/tree": () =>
        json(200, { checkpoint, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: "plan.md", path: "/Work/plan.md", size: 4 }] } }),
    });
    renderApp("/checkpoints/Work/before-cleanup");
    expect(await screen.findByRole("link", { name: "plan" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Show the board" })).not.toBeInTheDocument();
  });
});
