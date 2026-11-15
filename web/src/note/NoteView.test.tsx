// SPDX-License-Identifier: AGPL-3.0-or-later
import { onlineManager, QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { startsWithH1 } from "./NoteView";
import PropertyHint from "./PropertyHint";
import { keptTagOf, withCameFrom } from "../calendar/backLink";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  onlineManager.setOnline(true);
});

const PLAN = "/Work/projects/plan.md";
const planUrl = "/notes/Work/projects/plan.md";
const source = ["---", "tags: [work]", "---", "# Plan", "", "## Goals", "", "- [ ] write the [[roadmap]]", "- [x] read", "", "See [[Home/garden]] and [[todo]]."].join("\n");

const noteView = (path: string, content: string, version = "v1") => ({ path, content, version, lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" });
const tree = {
  damaged: 0,
  root: {
    name: "Work",
    path: "/Work",
    folders: [
      {
        name: "projects",
        path: "/Work/projects",
        folders: [],
        notes: ["alpha.md", "plan.md", "roadmap.md"].map((n) => ({ name: n, path: `/Work/projects/${n}`, size: 1 })),
      },
    ],
    notes: [{ name: "inbox.md", path: "/Work/inbox.md", size: 1 }],
  },
};

const planLinks = [
  { line: 8, raw: "[[roadmap]]", target: "roadmap", kind: "note", path: "/Work/projects/roadmap.md", candidates: [] },
  // Another vault's note is never a link target: the server answers "not found" within this vault.
  { line: 11, raw: "[[Home/garden]]", target: "Home/garden", kind: "not_found", path: null, candidates: [] },
  { line: 11, raw: "[[todo]]", target: "todo", kind: "not_found", path: null, candidates: [] },
];

function noteApi(extra: Parameters<typeof fakeApi>[0] = {}, previousSignInAt: string | null = "2026-10-24T08:00:00Z") {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4 }]),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, source)),
    [`GET /api/links${PLAN}`]: () => json(200, planLinks),
    [`GET /api/backlinks${PLAN}`]: () =>
      json(200, [
        { path: "/Work/inbox.md", line: 3, text: "see [[plan]]", ambiguous: false },
        { path: "/Work/inbox.md", line: 9, text: "and [[plan]] again", ambiguous: false },
        { path: "/Work/projects/roadmap.md", line: 1, text: "[[plan]]", ambiguous: false },
      ]),
    "GET /api/changes": () => json(200, []),
    "GET /api/notes/*": (r) => json(200, noteView(decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), "other")),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    ...extra,
  });
}

const view = () => screen.getByRole("article");
const asked = (fetch: ReturnType<typeof fakeApi>, path: string) =>
  fetch.mock.calls.filter(([r]) => (r as Request).method === "GET" && new URL((r as Request).url).pathname === path).length;

describe("the note view", () => {
  it("reads the note again by itself once its vault is no longer being re-encrypted", async () => {
    let busy = true;
    noteApi({
      [`GET /api/notes${PLAN}`]: () => (busy ? json(503, { error: "This vault is being re-encrypted; try again in a moment." }) : json(200, noteView(PLAN, source))),
    });
    renderApp(planUrl);
    expect(await screen.findByText("This vault is being re-encrypted; try again in a moment.")).toBeInTheDocument();
    busy = false;
    expect(await screen.findByRole("heading", { level: 2, name: "Goals" }, { timeout: 4500 })).toBeInTheDocument();
    expect(screen.queryByText("This vault is being re-encrypted; try again in a moment.")).not.toBeInTheDocument();
  });

  it("renders the note, with its front matter as properties above the body", async () => {
    noteApi();
    renderApp(planUrl);
    expect(await screen.findByRole("heading", { level: 2, name: "Goals" })).toBeInTheDocument();
    const frontmatter = screen.getByLabelText("Properties");
    expect(frontmatter.tagName).toBe("DL");
    expect(within(frontmatter).getByRole("term")).toHaveTextContent("tags");
    expect(within(frontmatter).getByRole("definition")).toHaveTextContent("work");
    expect(frontmatter.compareDocumentPosition(screen.getByRole("heading", { name: "Plan", level: 1 })) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(view()).queryByText("tags: [work]", { selector: "p" })).not.toBeInTheDocument();
  });

  it("shows the front matter's tags as chips that open the search filtered by one", async () => {
    noteApi();
    renderApp(planUrl);
    const frontmatter = await screen.findByLabelText("Properties");
    const chip = within(within(frontmatter).getByRole("list", { name: "Tags" })).getByRole("button", { name: "Notes tagged #work" });
    await userEvent.click(chip);
    expect(useEditor.getState().searchTag).toBe("work");
    expect(useEditor.getState().overlay).toBe("search");
  });

  it("opening a note in Read mode leaves the focus where a screen reader expects it, on the note's heading", async () => {
    noteApi();
    renderApp(planUrl);
    const title = await screen.findByRole("heading", { level: 1, name: "Plan" });
    await waitFor(() => expect(title).toHaveFocus());
    expect(title).toHaveAttribute("tabindex", "-1");
  });

  it("takes the focus to the file name's heading when the note has no H1 of its own", async () => {
    noteApi({ [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, "Text.")) });
    renderApp(planUrl);
    await waitFor(() => expect(screen.getByRole("heading", { level: 1, name: "plan" })).toHaveFocus());
  });

  it("leaves the focus where it is when something outside the note holds it as the note arrives", async () => {
    let release!: () => void;
    const slow = new Promise<void>((resolve) => (release = resolve));
    noteApi({
      [`GET /api/notes${PLAN}`]: async () => {
        await slow;
        return json(200, noteView(PLAN, source));
      },
    });
    renderApp(planUrl);
    const opener = await screen.findByRole("button", { name: "Open file tree" });
    act(() => opener.focus());
    release();
    const title = await screen.findByRole("heading", { level: 1, name: "Plan" });
    expect(opener).toHaveFocus();
    expect(title).not.toHaveFocus();
  });

  it("going to Write mode puts the caret in the text", async () => {
    noteApi();
    renderApp(planUrl);
    await screen.findByRole("heading", { level: 1, name: "Plan" });
    await userEvent.click(screen.getByRole("button", { name: "Write" }));
    const editor = await screen.findByRole("textbox", { name: "Markdown source" });
    await waitFor(() => expect(editor).toHaveFocus());
  });

  it("shows one title: the note's own H1 replaces the file name's", async () => {
    noteApi();
    renderApp(planUrl);
    expect(await screen.findByRole("heading", { level: 1, name: "Plan" })).toBeInTheDocument();
    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
  });

  it("counts a setext H1 as the note's own title", async () => {
    noteApi({ [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, "\nPlan\n====\n\n## Goals\n")) });
    renderApp(planUrl);
    await screen.findByRole("heading", { level: 2, name: "Goals" });
    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Plan");
  });

  it("takes an empty H1 for none: a new note's `# ` shows the file name's title", async () => {
    expect(startsWithH1("# ")).toBe(false);
    expect(startsWithH1("#\n\ntext")).toBe(false);
    expect(startsWithH1("# #")).toBe(false);
    expect(startsWithH1("# A")).toBe(true);
    expect(startsWithH1("# A #")).toBe(true);
    noteApi({ [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, "# ")) });
    renderApp(planUrl);
    expect(await screen.findByRole("heading", { level: 1, name: "plan" })).toBeInTheDocument();
  });

  it("a note opening with a closed-hash H1 shows it as its title", () => {
    expect(startsWithH1("# Plan #\n\ntext")).toBe(true);
    expect(startsWithH1("#\tPlan\r\n")).toBe(true);
    expect(startsWithH1("## Plan\n")).toBe(false);
    expect(startsWithH1("####### Plan\n")).toBe(false);
    expect(startsWithH1("# \n")).toBe(false);
  });

  it("keeps the file name's title when the first line is an indented code block, not a heading", async () => {
    noteApi({ [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, "    # x\n\n## Goals\n")) });
    renderApp(planUrl);
    await screen.findByRole("heading", { level: 2, name: "Goals" });
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("plan");
  });

  it("shows the file name as the title of a note without an H1", async () => {
    noteApi({ [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, "---\ntags: [x]\n---\n\nJust text.\n\n## Goals\n")) });
    renderApp(planUrl);
    await screen.findByRole("heading", { level: 2, name: "Goals" });
    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("plan");
  });

  it("shows the links as the server resolved them, never across vaults", async () => {
    const fetch = noteApi();
    renderApp(planUrl);
    expect(await screen.findByRole("link", { name: "roadmap" })).toHaveAttribute("href", "/notes/Work/projects/roadmap.md");
    expect(screen.getByRole("button", { name: "Create the note “Home/garden”" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Home/garden" })).not.toBeInTheDocument();
    expect(fetch.mock.calls.some(([r]) => new URL(r.url).pathname.includes("/Home"))).toBe(false);
  });

  it("ticks a task as a write of that one line with the known version, then writes on with the new one", async () => {
    const bodies: { content: string; version: string }[] = [];
    noteApi({
      [`PUT /api/notes${PLAN}`]: async (r) => {
        const body = (await r.json()) as { content: string; version: string };
        bodies.push(body);
        return json(200, noteView(PLAN, body.content, `v${bodies.length + 1}`));
      },
    });
    renderApp(planUrl);
    await userEvent.click(await screen.findByRole("checkbox", { name: "write the roadmap" }));
    await waitFor(() => expect(screen.getByRole("checkbox", { name: "write the roadmap" })).toHaveAttribute("aria-checked", "true"));
    expect(bodies[0]).toEqual({ content: source.replace("- [ ] write", "- [x] write"), version: "v1" });

    await userEvent.click(screen.getByRole("checkbox", { name: "read" }));
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1].version).toBe("v2");
    expect(bodies[1].content.split("\n")[8]).toBe("- [ ] read");
  });

  it("on a conflict shows the newer version instead of overwriting it", async () => {
    const newer = source.replace("- [x] read", "- [x] read\n- [ ] added by liz");
    let puts = 0;
    noteApi({
      [`PUT /api/notes${PLAN}`]: () => {
        puts++;
        return json(409, { error: "changed", current: { content: newer, version: "v7" } });
      },
    });
    renderApp(planUrl);
    await userEvent.click(await screen.findByRole("checkbox", { name: "write the roadmap" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Changed elsewhere — showing the newer version; tap again.");
    expect(screen.getByRole("checkbox", { name: "added by liz" })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "write the roadmap" })).toHaveAttribute("aria-checked", "false");
    expect(puts).toBe(1);
  });

  it("offline, a task's box ticked fails at once with its notice, and nothing is sent later", async () => {
    let online = false;
    let puts = 0;
    noteApi({
      [`PUT /api/notes${PLAN}`]: async (r) => {
        const body = (await r.json()) as { content: string; version: string };
        puts++;
        if (!online) throw new TypeError("Failed to fetch");
        return json(200, noteView(PLAN, body.content, "v2"));
      },
    });
    renderApp(planUrl);
    const box = await screen.findByRole("checkbox", { name: "write the roadmap" });
    onlineManager.setOnline(false);
    await userEvent.click(box);
    expect(await screen.findByRole("alert")).toHaveTextContent("The server can't be reached right now. Try again in a moment.");
    expect(puts).toBe(1);

    online = true;
    act(() => {
      onlineManager.setOnline(true);
      window.dispatchEvent(new Event("online"));
    });
    await act(() => new Promise((resolve) => setTimeout(resolve, 100)));
    expect(puts).toBe(1);
    expect(screen.getByRole("checkbox", { name: "write the roadmap" })).toHaveAttribute("aria-checked", "false");
  });

  it("lists the notes linking here below the note, and opens one on tap", async () => {
    noteApi();
    const router = renderApp(planUrl);
    const backlinks = await screen.findByRole("region", { name: "Linked from" });
    expect(within(backlinks).getByRole("heading")).toHaveTextContent("Linked from · 2");
    expect(view().compareDocumentPosition(backlinks) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    await userEvent.click(within(backlinks).getByRole("button", { name: /^inbox/ }));
    expect(router.state.location.pathname).toBe("/notes/Work/inbox.md");
  });

  it("creates a missing link's note in the current folder and opens it in Write mode", async () => {
    let created: unknown;
    noteApi({
      "PUT /api/notes/Work/projects/todo.md": async (r) => {
        created = await r.json();
        return json(201, noteView("/Work/projects/todo.md", ""));
      },
    });
    const router = renderApp(planUrl);
    await userEvent.click(await screen.findByRole("button", { name: "Create the note “todo”" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/todo.md"));
    expect(created).toEqual({ content: "" });
    expect(useEditor.getState().mode).toBe("write");
  });

  it("asks before deleting, naming the notes that link here, and then opens the folder's next note", async () => {
    let deleted = 0;
    noteApi({
      [`DELETE /api/notes${PLAN}`]: () => {
        deleted++;
        return noContent();
      },
    });
    const router = renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });

    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    let dialog = screen.getByRole("dialog", { name: "Delete plan?" });
    expect(await within(dialog).findByText("inbox")).toBeInTheDocument();
    expect(within(dialog).getByText("roadmap")).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(deleted).toBe(0);

    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    dialog = screen.getByRole("dialog", { name: "Delete plan?" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/roadmap.md"));
    expect(deleted).toBe(1);
    expect(useEditor.getState().recentPaths).not.toContain(PLAN);
  });

  it("says that deleting a published note withdraws the link", async () => {
    const withdraws = "It is published: deleting it withdraws the link.";
    const notPublished = (r: Request) => json(404, { error: `${new URL(r.url).pathname} is not published.` });
    // The dialog learns it from the vault's list, which holds no link: the note's own route, with the link, says no.
    noteApi({
      "GET /api/vaults/Work/publications": () =>
        json(200, { publications: [{ path: PLAN, snapshotAt: "2026-11-05T14:32:00Z", older: false, changedAt: null, publishedBy: "ada", link: null }], unpublishable: [] }),
      "GET /api/publications/*": notPublished,
    });
    renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    expect(await within(screen.getByRole("dialog", { name: "Delete plan?" })).findByText(withdraws)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    cleanup();

    // Not published: nothing to withdraw.
    noteApi({ "GET /api/vaults/Work/publications": () => json(200, { publications: [], unpublishable: [] }), "GET /api/publications/*": notPublished });
    renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    let dialog = screen.getByRole("dialog", { name: "Delete plan?" });
    expect(await within(dialog).findByText("inbox")).toBeInTheDocument();
    expect(within(dialog).queryByText(withdraws)).toBeNull();
    cleanup();

    // A member is told nothing, and asks nothing of publications.
    const fetch = noteApi({
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, owner: '"Alice"', level: "write", shared: true, membership: "m1" }]),
      "GET /api/vaults/Work/publications": () =>
        json(200, { publications: [{ path: PLAN, snapshotAt: "2026-11-05T14:32:00Z", older: false, changedAt: null, publishedBy: null, link: null }], unpublishable: [] }),
      "GET /api/publications/*": () => json(200, { path: PLAN, snapshotAt: null, older: null, changedAt: null, publishedBy: null, link: null }),
    });
    const asked = () => fetch.mock.calls.map(([r]) => new URL((r as Request).url).pathname).filter((p) => p.includes("publications"));
    renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });
    // The bar's own questions, the shared vault's list and then that it is published by the vault's owner; the dialog adds none.
    expect(await screen.findByText("Published by the vault's owner")).toBeInTheDocument();
    const before = asked();
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    dialog = screen.getByRole("dialog", { name: "Delete plan?" });
    expect(await within(dialog).findByText("inbox")).toBeInTheDocument();
    expect(within(dialog).queryByText(withdraws)).toBeNull();
    expect(asked()).toEqual(before);
    expect(before).toEqual(["/api/vaults/Work/publications", `/api/publications${PLAN}`]);
  });

  it("goes to the vaults after deleting the only note of a folder", async () => {
    const lone = "/Work/inbox.md";
    noteApi({
      [`GET /api/notes${lone}`]: () => json(200, noteView(lone, "hello")),
      [`GET /api/backlinks${lone}`]: () => json(200, []),
      [`DELETE /api/notes${lone}`]: () => noContent(),
    });
    const router = renderApp("/notes/Work/inbox.md");
    await screen.findByText("hello");
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    const dialog = screen.getByRole("dialog", { name: "Delete inbox?" });
    expect(await within(dialog).findByText("No other note links here.")).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(useEditor.getState().recentPaths).toEqual([]);
  });

  it("shows the agent banner for this note's changes since the last sign-in, and toggles the highlights", async () => {
    let since: string | null = null;
    noteApi({
      "GET /api/changes": (r) => {
        since = new URL(r.url).searchParams.get("since");
        return json(200, [
          { path: "/Work/other.md", writer: "bob", lastWriteAt: new Date().toISOString(), version: "x" },
          { path: PLAN, writer: "liz", lastWriteAt: new Date(Date.now() - 3 * 60_000).toISOString(), version: "v1" },
        ]);
      },
    });
    renderApp(planUrl);
    const banner = await screen.findByRole("region", { name: "Agent changes" });
    expect(since).toBe("2026-10-24T08:00:00Z");
    expect(banner).toHaveTextContent("Agent key liz edited 3 minutes ago");
    await userEvent.click(within(banner).getByRole("button", { name: "Show" }));
    expect(useEditor.getState().showAgentHighlights).toBe(true);
    expect(within(banner).getByRole("button", { name: "Hide" })).toBeInTheDocument();
  });

  it("makes a device's write after a long pause stand out, names the device and leads to Access", async () => {
    const resumedAt = new Date(Date.now() - 2 * 3_600_000).toISOString();
    noteApi({
      "GET /api/changes": () =>
        json(200, [
          {
            path: PLAN,
            writer: "Work – Obsidian Pixel 9",
            lastWriteAt: resumedAt,
            version: "v1",
            writerKind: "device",
            quietSince: new Date(Date.parse(resumedAt) - 22 * 86_400_000).toISOString(),
            resumedAt,
          },
        ]),
      "GET /api/keys": () => json(200, []),
    });
    const router = renderApp(planUrl);
    const banner = await screen.findByRole("region", { name: "Device changes" });
    expect(screen.queryByRole("region", { name: "Agent changes" })).not.toBeInTheDocument();
    expect(banner).toHaveTextContent("Obsidian on Pixel 9 wrote here 2 hours ago, for the first time in 3 weeks. If that wasn't you, revoke the device in Access.");
    expect(within(banner).getByRole("button", { name: "Show" })).toBeInTheDocument();
    await userEvent.click(within(banner).getByRole("link", { name: "Access" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/access/keys"));
  });

  it("on Show, tints the blocks changed since the version the user last saw, read through ?version=", async () => {
    const earlier = source.replace("- [x] read", "- [ ] read").replace("See [[Home/garden]] and [[todo]].", "");
    const asked: (string | null)[] = [];
    noteApi({
      "GET /api/changes": () => json(200, [{ path: PLAN, writer: "liz", lastWriteAt: new Date().toISOString(), version: "v1" }]),
      [`GET /api/history${PLAN}`]: () =>
        json(200, [
          { version: "v1", writtenAt: new Date().toISOString(), writer: "liz", lines: 11, current: true, changeset: "c3" },
          { version: "v0b", writtenAt: new Date(Date.now() - 60_000).toISOString(), writer: "liz (mv)", lines: 11, current: false, changeset: "c2" },
          { version: "v0a", writtenAt: "2026-10-20T08:00:00Z", writer: "ada", lines: 10, current: false, changeset: "c1" },
        ]),
      [`GET /api/notes${PLAN}`]: (r) => {
        const version = new URL(r.url).searchParams.get("version");
        asked.push(version);
        return version === "v0a" ? json(200, { ...noteView(PLAN, earlier, "v0a"), current: false }) : json(200, noteView(PLAN, source));
      },
    });
    renderApp(planUrl);
    const banner = await screen.findByRole("region", { name: "Agent changes" });
    expect(document.querySelector("[data-changed]")).toBeNull();
    await userEvent.click(within(banner).getByRole("button", { name: "Show" }));

    await waitFor(() => expect(document.querySelectorAll("[data-changed]")).toHaveLength(2));
    expect([...document.querySelectorAll("[data-changed]")].map((e) => e.textContent?.trim())).toEqual(["read", "See Home/garden and todo."]);
    expect(document.querySelector("[data-changed]")).toHaveAttribute("data-writer", "liz");
    expect(asked).toContain("v0a");

    await userEvent.click(within(banner).getByRole("button", { name: "Hide" }));
    expect(document.querySelector("[data-changed]")).toBeNull();
  });

  it("tints the whole note when the agent wrote it from the start", async () => {
    const asked: (string | null)[] = [];
    noteApi({
      "GET /api/changes": () => json(200, [{ path: PLAN, writer: "liz", lastWriteAt: new Date().toISOString(), version: "v1" }]),
      [`GET /api/history${PLAN}`]: () => json(200, [{ version: "v1", writtenAt: new Date().toISOString(), writer: "liz", lines: 11, current: true, changeset: "c1" }]),
      [`GET /api/notes${PLAN}`]: (r) => {
        asked.push(new URL(r.url).searchParams.get("version"));
        return json(200, noteView(PLAN, source));
      },
    });
    renderApp(planUrl);
    await userEvent.click(within(await screen.findByRole("region", { name: "Agent changes" })).getByRole("button", { name: "Show" }));
    await waitFor(() => expect(document.querySelectorAll("[data-changed]").length).toBeGreaterThan(3));
    expect(screen.getByRole("heading", { name: "Goals" })).toHaveAttribute("data-changed");
    expect(screen.getByLabelText("Properties")).toHaveAttribute("data-changed");
    expect(asked.every((v) => v === null)).toBe(true);
  });

  it("asks for no changes and shows no banner on the first sign-in ever", async () => {
    const fetch = noteApi({}, null);
    renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });
    expect(screen.queryByRole("region", { name: "Agent changes" })).not.toBeInTheDocument();
    expect(fetch.mock.calls.some(([r]) => new URL(r.url).pathname === "/api/changes")).toBe(false);
  });

  it("says so in the delete dialog when it couldn't check the links to the note", async () => {
    noteApi({ [`GET /api/backlinks${PLAN}`]: () => json(500, {}) });
    renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    const dialog = screen.getByRole("dialog", { name: "Delete plan?" });
    expect(await within(dialog).findByText("Couldn't check which notes link here.")).toBeInTheDocument();
    expect(within(dialog).queryByText("No other note links here.")).not.toBeInTheDocument();
  });

  it("shows no banner when no agent changed this note", async () => {
    noteApi({ "GET /api/changes": () => json(200, [{ path: "/Work/other.md", writer: "bob", lastWriteAt: "2026-10-25T08:00:00Z", version: "x" }]) });
    renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });
    await waitFor(() => expect(screen.queryByRole("region", { name: "Agent changes" })).not.toBeInTheDocument());
  });

  it("says so when the note isn't there", async () => {
    noteApi({ [`GET /api/notes${PLAN}`]: () => json(404, { error: "There is no note at /Work/projects/plan.md." }) });
    renderApp(planUrl);
    expect(await screen.findByText("There is no note at /Work/projects/plan.md.")).toBeInTheDocument();
  });
});

describe("the way back", () => {
  it("leads a note opened from What's on back to it", async () => {
    noteApi();
    renderApp({ pathname: planUrl, state: withCameFrom(undefined, { label: "What's on", to: "/whats-on" }) });
    const back = await screen.findByRole("link", { name: "Back to What's on" });
    expect(back).toHaveAttribute("href", "/whats-on");
    expect(back).toHaveTextContent("What's on");
    expect(back).toHaveClass("min-h-12");
    expect(screen.getByRole("navigation", { name: "Back" })).toContainElement(back);
  });

  it("leads back to the same week of the calendar, and still opens at the line it was opened at", async () => {
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    noteApi();
    renderApp({ pathname: planUrl, state: withCameFrom({ line: 6 }, { label: "Calendar", to: "/calendar/Work?week=2026-W46&tag=x" }) });
    expect(await screen.findByRole("link", { name: "Back to the calendar" })).toHaveAttribute("href", "/calendar/Work?week=2026-W46&tag=x");
    const goals = screen.getByRole("heading", { level: 2, name: "Goals" });
    await waitFor(() => expect(scrollIntoView.mock.contexts).toContain(goals));
    expect(goals).toHaveClass("k-flash");
  });

  it("takes What's on's tag back with it, in the history's state and not the address", async () => {
    noteApi({ "GET /api/calendar/whats-on": () => json(200, { items: [], count: 0, overdue: false, vaults: [], couldntRead: 0, couldntReadVaults: [] }) });
    const router = renderApp({ pathname: planUrl, state: withCameFrom(undefined, { label: "What's on", to: "/whats-on", tag: "kumbu" }) });
    await userEvent.click(await screen.findByRole("link", { name: "Back to What's on" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/whats-on"));
    expect(router.state.location.search).toBe("");
    expect(keptTagOf(router.state.location.state)).toBe("kumbu");
  });

  it("shows no way back for a note opened any other way", async () => {
    noteApi();
    renderApp({ pathname: planUrl, state: { line: 6 } });
    await screen.findByRole("heading", { level: 2, name: "Goals" });
    expect(screen.queryByRole("navigation", { name: "Back" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /^Back to (the calendar|What's on)$/ })).not.toBeInTheDocument();
  });

  it("shows no way back in Write mode", async () => {
    noteApi();
    renderApp({ pathname: planUrl, state: withCameFrom(undefined, { label: "What's on", to: "/whats-on" }) });
    await screen.findByRole("link", { name: "Back to What's on" });
    await userEvent.click(screen.getByRole("button", { name: "Write" }));
    await screen.findByRole("textbox", { name: "Markdown source" });
    expect(screen.queryByRole("link", { name: "Back to What's on" })).not.toBeInTheDocument();
  });
});

describe("a note that isn't there in a vault the list doesn't name", () => {
  const HEUTE = "/Tagebuch/Heute.md";
  const missing = () => json(404, { error: `There is no note at ${HEUTE} (if it was removed, it is in the trash).` });
  const NO_SUCH_NOTE = "There is no such note (if it was removed, it is in the trash).";

  it("is said without its path, and has no heading made from it", async () => {
    noteApi({ "GET /api/vaults": () => json(200, [{ name: "Home", notes: 1 }]), [`GET /api/notes${HEUTE}`]: missing });
    renderApp(`/notes${HEUTE}`);
    expect(await screen.findByText(NO_SUCH_NOTE)).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Heute" })).not.toBeInTheDocument();
    expect(document.body.textContent).not.toContain("There is no note at");
  });

  it("is said so as well while the list is still loading", async () => {
    noteApi({ "GET /api/vaults": () => new Promise<Response>(() => {}), [`GET /api/notes${HEUTE}`]: missing });
    renderApp(`/notes${HEUTE}`);
    expect(await screen.findByText(NO_SUCH_NOTE)).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Heute" })).not.toBeInTheDocument();
  });

  it("keeps the server's sentence and the heading in a vault the list names", async () => {
    noteApi({ "GET /api/vaults": () => json(200, [{ name: "Home", notes: 1 }]), "GET /api/notes/Home/a.md": () => json(404, { error: "There is no note at /Home/a.md." }) });
    renderApp("/notes/Home/a.md");
    expect(await screen.findByText("There is no note at /Home/a.md.")).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1, name: "a" })).toBeInTheDocument();
  });
});

describe("Embeds", () => {
  const ROADMAP = "/Work/projects/roadmap.md";
  const embedding = ["![[roadmap]]", "", "![[roadmap#Next]]", "", "Inline ![[roadmap]] here.", "", "![[plan]]", "", "![[chart.png]]", "", "![[roadmap#Nope]]"].join("\n");
  const roadmap = ["---", "status: draft", "---", "# Roadmap", "", "intro text", "", "## Next", "", "- [ ] next thing", "", "## Later", "", "later text, see ![[plan]]"].join("\n");
  const embedLinks = [
    { line: 1, raw: "![[roadmap]]", target: "roadmap", kind: "note", path: ROADMAP, candidates: [] },
    { line: 3, raw: "![[roadmap#Next]]", target: "roadmap", kind: "note", path: ROADMAP, candidates: [] },
    { line: 5, raw: "![[roadmap]]", target: "roadmap", kind: "note", path: ROADMAP, candidates: [] },
    { line: 7, raw: "![[plan]]", target: "plan", kind: "note", path: PLAN, candidates: [] },
    { line: 9, raw: "![[chart.png]]", target: "chart.png", kind: "attachment", path: null, candidates: [] },
    { line: 11, raw: "![[roadmap#Nope]]", target: "roadmap", kind: "note", path: ROADMAP, candidates: [] },
  ];

  it("shows embedded notes and sections one level deep, with the way to open them; other embeds stay links", async () => {
    noteApi({
      [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, embedding)),
      [`GET /api/links${PLAN}`]: () => json(200, embedLinks),
      [`GET /api/notes${ROADMAP}`]: () => json(200, noteView(ROADMAP, roadmap, "r1")),
      [`GET /api/links${ROADMAP}`]: () => json(200, [{ line: 14, raw: "![[plan]]", target: "plan", kind: "note", path: PLAN, candidates: [] }]),
    });
    renderApp(planUrl);

    const whole = await screen.findByRole("region", { name: "Embedded: roadmap" });
    expect(await within(whole).findByRole("heading", { name: "Roadmap", level: 1 })).toBeInTheDocument();
    expect(whole).toHaveTextContent("later text");
    expect(whole).not.toHaveTextContent("status: draft");
    expect(within(whole).getByRole("link", { name: /^roadmap/ })).toHaveAttribute("href", "/notes/Work/projects/roadmap.md");
    // Its own embed of this note stays a link: one level deep, no cycle.
    expect(await within(whole).findByRole("link", { name: "plan" })).toHaveAttribute("href", planUrl);
    expect(within(whole).getByRole("checkbox", { name: "next thing" })).toBeDisabled();

    const part = screen.getByRole("region", { name: "Embedded: roadmap › Next" });
    expect(part).toHaveTextContent("next thing");
    expect(part).not.toHaveTextContent("intro text");
    expect(part).not.toHaveTextContent("later text");

    expect(screen.getByRole("region", { name: "Embedded: roadmap › Nope" })).toHaveTextContent("This note has no heading “Nope”.");
    expect(screen.getAllByRole("region", { name: /^Embedded/ })).toHaveLength(3);
    // Inline, an embed is a link; this note embedding itself is one too; an attachment is named.
    const article = screen.getByRole("article");
    expect(within(article).getAllByRole("link", { name: "roadmap" }).some((a) => a.closest("p")?.textContent?.startsWith("Inline"))).toBe(true);
    expect(within(article).getAllByRole("link", { name: "plan" }).some((a) => !a.closest("section"))).toBe(true);
    expect(within(article).getByText("chart.png")).not.toHaveAttribute("href");
  });
});

describe("Embeds and the note's own lines", () => {
  it("jumps from the outline to the note's own heading, never to an embedded one on the same line number", async () => {
    const ROADMAP = "/Work/projects/roadmap.md";
    const scrolled: Element[] = [];
    Element.prototype.scrollIntoView = vi.fn(function (this: Element) {
      scrolled.push(this);
    });
    noteApi({
      [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, "![[roadmap]]\n\n## Mine\n\ntext")),
      [`GET /api/links${PLAN}`]: () => json(200, [{ line: 1, raw: "![[roadmap]]", target: "roadmap", kind: "note", path: ROADMAP, candidates: [] }]),
      [`GET /api/notes${ROADMAP}`]: () => json(200, noteView(ROADMAP, "x\n\n## Theirs\n\ny", "r1")),
      [`GET /api/links${ROADMAP}`]: () => json(200, []),
    });
    renderApp(planUrl);
    const theirs = await screen.findByRole("heading", { name: "Theirs" });
    expect(theirs).not.toHaveAttribute("data-line");
    await userEvent.click(screen.getByRole("button", { name: "Outline" }));
    await userEvent.click(within(screen.getByRole("dialog", { name: "Outline" })).getByRole("button", { name: "Mine" }));
    expect(scrolled.at(-1)).toBe(screen.getByRole("heading", { name: "Mine" }));
  });
});

describe("Outline and find in the note", () => {
  const long = ["# Plan", "", "Intro about the road.", "", "## Goals", "", "```", "# not a heading, road", "```", "", "### Road **ahead**", "", "The road `road` ends."].join("\n");
  const scrolled: Element[] = [];
  const scroll = vi.fn(function (this: Element) {
    scrolled.push(this);
  });

  it("lists the headings from the title bar, and jumps to one", async () => {
    Element.prototype.scrollIntoView = scroll;
    noteApi({ [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, long)) });
    renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });
    await userEvent.click(screen.getByRole("button", { name: "Outline" }));
    const sheet = screen.getByRole("dialog", { name: "Outline" });
    expect(within(sheet).getAllByRole("button", { name: /^(?!Close)/ }).map((b) => b.textContent)).toEqual(["Plan", "Goals", "Road ahead"]);
    await userEvent.click(within(sheet).getByRole("button", { name: "Road ahead" }));
    expect(screen.queryByRole("dialog", { name: "Outline" })).not.toBeInTheDocument();
    expect(scrolled.at(-1)).toBe(screen.getByRole("heading", { name: "Road ahead", level: 3 }));
  });

  it("has no outline button in Write mode", async () => {
    noteApi();
    renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });
    await userEvent.click(screen.getByRole("button", { name: "Write" }));
    expect(screen.queryByRole("button", { name: "Outline" })).not.toBeInTheDocument();
  });

  it("finds in the note: marks every match, steps through them, and closes back to the dock", async () => {
    Element.prototype.scrollIntoView = scroll;
    noteApi({ [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, long)) });
    renderApp(planUrl);
    await screen.findByRole("heading", { name: "Goals" });
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Find in note" }));

    const bar = screen.getByRole("search", { name: "Find in note" });
    expect(screen.queryByRole("navigation", { name: "Vault actions" })).not.toBeInTheDocument();
    const input = within(bar).getByRole("searchbox", { name: "Find in note" });
    expect(input).toHaveFocus();
    await userEvent.type(input, "ROAD");

    // Intro, the code block, the heading, the text and the inline code: five.
    await waitFor(() => expect(within(bar).getByRole("status")).toHaveTextContent("1 of 5"));
    const marks = () => [...document.querySelectorAll("mark[data-find]")];
    expect(marks()).toHaveLength(5);
    expect(marks()[0]).toHaveAttribute("data-current");
    expect(marks()[1].closest("pre")).not.toBeNull();
    expect(marks()[4].closest("code")).not.toBeNull();

    await userEvent.keyboard("{Enter}");
    expect(within(bar).getByRole("status")).toHaveTextContent("2 of 5");
    await waitFor(() => expect(marks()[1]).toHaveAttribute("data-current"));
    expect(scrolled.at(-1)).toBe(marks()[1]);
    await userEvent.click(within(bar).getByRole("button", { name: "Previous match" }));
    await userEvent.click(within(bar).getByRole("button", { name: "Previous match" }));
    expect(within(bar).getByRole("status")).toHaveTextContent("5 of 5");

    await userEvent.clear(input);
    await userEvent.type(input, "nothing like it");
    await waitFor(() => expect(within(bar).getByRole("status")).toHaveTextContent("No matches"));
    expect(within(bar).getByRole("button", { name: "Next match" })).toBeDisabled();

    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("search", { name: "Find in note" })).not.toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Vault actions" })).toBeInTheDocument();
    expect(marks()).toHaveLength(0);
    expect(screen.getByRole("button", { name: "Note actions" })).toHaveFocus();
  });

  it("unfolds a folded callout to show the match, keeps the index within the matches, and forgets the query on another note", async () => {
    Element.prototype.scrollIntoView = scroll;
    const folded = "Road one, road two, road three.\n\n> [!tip]- Hidden\n> the road inside";
    noteApi({ [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, folded)) });
    renderApp(planUrl);
    await screen.findByText(/Road one/);
    act(() => useEditor.getState().setNoteTool("find"));
    const input = await screen.findByRole("searchbox", { name: "Find in note" });
    await userEvent.type(input, "road");
    const status = () => within(screen.getByRole("search", { name: "Find in note" })).getByRole("status");
    await waitFor(() => expect(status()).toHaveTextContent("1 of 4"));
    await userEvent.click(screen.getByRole("button", { name: "Previous match" }));
    expect(status()).toHaveTextContent("4 of 4");
    await waitFor(() => expect(document.querySelector("details")).toHaveAttribute("open"));

    // An index past the matches (fewer of them since) comes back to the last one.
    act(() => useEditor.getState().setFind({ index: 9 }));
    await waitFor(() => expect(status()).toHaveTextContent("4 of 4"));

    act(() => useEditor.getState().openNote("/Work/inbox.md"));
    expect(useEditor.getState().find).toEqual({ query: "", index: 0, count: 0 });
    expect(useEditor.getState().noteTool).toBeNull();
  });
});

describe("the board's own note", () => {
  const BOARD = "/Work/Board/Board.md";
  const definition = ["---", "kumbu-board: 1", "columns: [Ideas, Doing]", "---", "## Instructions", "", "Keep it small."].join("\n");

  it("Board.md opens with a link to the board above it", async () => {
    noteApi({ [`GET /api/notes${BOARD}`]: () => json(200, noteView(BOARD, definition)) });
    renderApp(`/notes${BOARD}`);
    const link = await screen.findByRole("link", { name: "Open the board" });
    expect(link).toHaveAttribute("href", "/board/Work");
    expect(link.compareDocumentPosition(await screen.findByRole("article")) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("no other note has the link, not even a Board.md elsewhere", async () => {
    const elsewhere = "/Work/Board/Archive/Board.md";
    noteApi({ [`GET /api/notes${elsewhere}`]: () => json(200, noteView(elsewhere, definition)) });
    renderApp(`/notes${elsewhere}`);
    await screen.findByRole("article");
    expect(screen.queryByRole("link", { name: "Open the board" })).not.toBeInTheDocument();
  });
});

describe("the note's flag in its header", () => {
  const flag = (flag: string | null, canWrite = true) => ({ "GET /api/vaults/Work/flag": () => json(200, { flag, inText: null, openTask: false, canWrite }) });

  it("shows the note's flag, which opens the flag menu", async () => {
    noteApi(flag("red"));
    renderApp(planUrl);
    const line = await screen.findByRole("group", { name: "Flag" });
    const button = within(line).getByRole("button", { name: "Red flag" });
    expect(within(button).getByRole("img", { hidden: true, name: "Red flag" })).toBeInTheDocument();
    // A target a thumb hits.
    expect(button).toHaveClass("min-h-12");
    await userEvent.click(button);
    expect(await screen.findByRole("dialog", { name: "Flag" })).toBeInTheDocument();
  });

  it("shows nothing for a note without a flag", async () => {
    const fetch = noteApi(flag(null));
    renderApp(planUrl);
    await screen.findByRole("article");
    await waitFor(() => expect(fetch.mock.calls.some(([r]) => new URL((r as Request).url).pathname === "/api/vaults/Work/flag")).toBe(true));
    expect(screen.queryByRole("group", { name: "Flag" })).not.toBeInTheDocument();
    expect(screen.queryByRole("img", { name: /flag/i })).not.toBeInTheDocument();
  });

  it("asks the flag again with each new version of the note", async () => {
    let version = "v1";
    let shown: string | null = null;
    noteApi({
      ...{ "GET /api/vaults/Work/flag": () => json(200, { flag: shown, inText: null, openTask: false, canWrite: true }) },
      [`GET /api/notes${PLAN}`]: () => json(200, noteView(PLAN, source, version)),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp(planUrl, queryClient);
    await screen.findByRole("article");
    shown = "green";
    version = "v2";
    await act(() => queryClient.invalidateQueries({ queryKey: ["note", PLAN] }));
    expect(await screen.findByRole("button", { name: "Green flag" })).toBeInTheDocument();
  });
});

describe("properties at the top", () => {
  const ALIAS = "/Work/aliastest.md";
  // Chris's note (spec §2.2): a rule and a setext H2, not properties.
  const chris = "# aliastest\n---\naliases: [kumbu]\n---\n";
  const HINT = "Properties go at the very top of a note.";
  const hint = async () => (await screen.findByText(HINT)).closest<HTMLElement>("[role=note]")!;

  /** The note as written, read again as the last write left it; every PUT's body is kept. */
  function aliasApi(content: string, extra: Parameters<typeof fakeApi>[0] = {}, path = ALIAS) {
    let current = noteView(path, content);
    const puts: { content: string; version: string }[] = [];
    fetched = noteApi({
      "GET /api/vaults/Work/calendar/entry": () => json(200, { entry: null, couldntRead: null, canWrite: true }),
      [`GET /api/notes${path}`]: () => json(200, current),
      [`PUT /api/notes${path}`]: async (r) => {
        const body = (await r.json()) as { content: string; version: string };
        puts.push(body);
        current = noteView(path, body.content, `v${puts.length + 1}`);
        return json(200, current);
      },
      ...extra,
    });
    return puts;
  }
  let fetched: ReturnType<typeof fakeApi>;
  const move = async () => userEvent.click(await within(await hint()).findByRole("button", { name: "Move to properties" }));

  it("asks the calendar again after the move: a `when:` moved changes its line", async () => {
    const puts = aliasApi(chris);
    renderApp(`/notes${ALIAS}`);
    await waitFor(() => expect(asked(fetched, "/api/vaults/Work/calendar/entry")).toBeGreaterThan(0));
    const [entries, vaults] = [asked(fetched, "/api/vaults/Work/calendar/entry"), asked(fetched, "/api/vaults")];
    await move();
    await waitFor(() => expect(puts).toHaveLength(1));
    await waitFor(() => expect(asked(fetched, "/api/vaults/Work/calendar/entry")).toBeGreaterThan(entries));
    expect(asked(fetched, "/api/vaults")).toBeGreaterThan(vaults);
  });

  it("writes nothing while the editor's text isn't saved, and says so", async () => {
    const puts = aliasApi(chris);
    renderApp(`/notes${ALIAS}`);
    await hint();
    act(() => useEditor.getState().setBeforeRead(async () => false));
    await move();
    expect(await within(await hint()).findByText("Your typing isn't saved yet; try again in a moment.")).toBeInTheDocument();
    await act(() => new Promise((resolve) => setTimeout(resolve, 50)));
    expect(puts).toEqual([]);
  });

  it("moves in the text as the editor's save left it, on that version", async () => {
    const puts = aliasApi(chris);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp(`/notes${ALIAS}`, queryClient);
    await hint();
    // The save the tap waits for brings a newer version.
    act(() =>
      useEditor.getState().setBeforeRead(async () => {
        queryClient.setQueryData(["note", ALIAS], noteView(ALIAS, `${chris}\nMore.\n`, "v9"));
        return true;
      }),
    );
    await move();
    await waitFor(() => expect(puts).toEqual([{ content: "---\naliases: [kumbu]\n---\n# aliastest\n\nMore.\n", version: "v9" }]));
  });

  it("the board's own note, below maintain, offers neither the move nor + Add property", async () => {
    const BOARD_NOTE = "/Work/Board/Board.md";
    aliasApi(`---\nowner: Bob\n---\n${chris}`, { "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, canWrite: true, level: "write", owner: '"Ada"', shared: true }]) }, BOARD_NOTE);
    renderApp(`/notes${BOARD_NOTE}`);
    await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeDisabled());
    expect(await hint()).toHaveTextContent(HINT);
    expect(within(await hint()).queryByRole("button")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Properties")).toHaveTextContent("owner");
    expect(screen.queryByRole("button", { name: "Add property" })).not.toBeInTheDocument();
  });

  it("says why when the moved keys wouldn't read back", () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <PropertyHint path={ALIAS} onMove={vi.fn()} said={{ unwritable: true }} />
      </QueryClientProvider>,
    );
    expect(screen.getByRole("status")).toHaveTextContent("That can't be written as properties here, so nothing was moved.");
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("points out the block right above its rule, and moves it to the top on a tap, as one write with the note's version", async () => {
    const puts = aliasApi(chris);
    renderApp(`/notes${ALIAS}`);
    const note = await hint();
    expect(document.querySelector("article hr")!.previousElementSibling).toBe(note);
    await userEvent.click(await within(note).findByRole("button", { name: "Move to properties" }));
    await waitFor(() => expect(puts).toEqual([{ content: "---\naliases: [kumbu]\n---\n# aliastest\n", version: "v1" }]));
    expect(within(await screen.findByLabelText("Properties")).getByRole("term")).toHaveTextContent("aliases");
    expect(screen.queryByText(HINT)).not.toBeInTheDocument();
  });

  it("refuses a key the properties have already, naming it, and writes nothing", async () => {
    const puts = aliasApi(`---\naliases: [old]\n---\n${chris}`);
    renderApp(`/notes${ALIAS}`);
    await userEvent.click(await within(await hint()).findByRole("button", { name: "Move to properties" }));
    expect(await within(await hint()).findByText("aliases", { selector: "code" })).toBeInTheDocument();
    expect(await hint()).toHaveTextContent("aliases is already in the properties: change it there.Change it in Write mode");
    // The way there, and nothing else: no Move to properties to meet the refusal again.
    expect(within(await hint()).getAllByRole("button").map((b) => b.textContent)).toEqual(["Change it in Write mode"]);
    await act(() => new Promise((resolve) => setTimeout(resolve, 50)));
    expect(puts).toEqual([]);
  });

  it("after that refusal, Change it in Write mode opens the note in Write mode, as the top bar's Write does", async () => {
    aliasApi(`---\naliases: [old]\n---\n${chris}`);
    renderApp(`/notes${ALIAS}`);
    await move();
    const change = await within(await hint()).findByRole("button", { name: "Change it in Write mode" });
    expect(change).toHaveClass("min-h-12");
    await userEvent.click(change);
    expect(useEditor.getState().mode).toBe("write");
    expect(screen.getByRole("button", { name: "Write", pressed: true })).toBeInTheDocument();
  });

  it("offers no Change it in Write mode while the note is being renamed, as Write is off then", async () => {
    aliasApi(`---\naliases: [old]\n---\n${chris}`);
    renderApp(`/notes${ALIAS}`);
    await move();
    expect(await within(await hint()).findByRole("button", { name: "Change it in Write mode" })).toBeInTheDocument();
    act(() => useEditor.getState().setRenaming(ALIAS, true));
    await waitFor(async () => expect(within(await hint()).queryByRole("button")).not.toBeInTheDocument());
    expect(screen.getByRole("button", { name: "Write" })).toBeDisabled();
  });

  it("moves nothing when the properties at the top can't be read, and says so", async () => {
    const puts = aliasApi(`---\nabout: |\n  more\n---\n${chris}`);
    renderApp(`/notes${ALIAS}`);
    await userEvent.click(await within(await hint()).findByRole("button", { name: "Move to properties" }));
    expect(await within(await hint()).findByText("The properties at the top can't be read here, so nothing was moved.")).toBeInTheDocument();
    await act(() => new Promise((resolve) => setTimeout(resolve, 50)));
    expect(puts).toEqual([]);
  });

  it("draws the hint's buttons with the accent's text colour as their edge, at least 3:1 on the hint in every scheme", async () => {
    aliasApi(`---\naliases: [old]\n---\n${chris}`);
    renderApp(`/notes${ALIAS}`);
    // accent-text is held at 4.5:1 on surface-2, the hint's ground, by the schemes' contrast test; line is not.
    const edged = (b: HTMLElement) => {
      expect(b).toHaveClass("border", "border-accent-text", "text-accent-text");
      expect(b).not.toHaveClass("border-line");
    };
    edged(await within(await hint()).findByRole("button", { name: "Move to properties" }));
    await move();
    edged(await within(await hint()).findByRole("button", { name: "Change it in Write mode" }));
  });

  it("without write, says where properties go and offers nothing", async () => {
    aliasApi(chris, { "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, canWrite: false, level: "read", owner: '"Ada"', shared: true }]) });
    renderApp(`/notes${ALIAS}`);
    await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeDisabled());
    expect(await hint()).toHaveTextContent(HINT);
    expect(within(await hint()).queryByRole("button")).not.toBeInTheDocument();
  });

  it("offers the move only once the vault list says the note may be changed", async () => {
    aliasApi(chris, { "GET /api/vaults": () => new Promise<Response>(() => {}) });
    renderApp(`/notes${ALIAS}`);
    expect(await hint()).toHaveTextContent(HINT);
    expect(within(await hint()).queryByRole("button")).not.toBeInTheDocument();
  });

  it("on a conflict writes nothing more, says so and shows the newer version", async () => {
    const newer = `${chris}\nAdded elsewhere.\n`;
    let puts = 0;
    aliasApi(chris, {
      [`PUT /api/notes${ALIAS}`]: () => {
        puts++;
        return json(409, { error: "changed", current: { content: newer, version: "v7" } });
      },
    });
    renderApp(`/notes${ALIAS}`);
    await userEvent.click(await within(await hint()).findByRole("button", { name: "Move to properties" }));
    expect(await within(await hint()).findByText("This note changed elsewhere; try again.")).toBeInTheDocument();
    expect(screen.getByText("Added elsewhere.")).toBeInTheDocument();
    expect(puts).toBe(1);
  });

  it("leaves a block in fenced code alone", async () => {
    aliasApi("# aliastest\n\n```\n---\naliases: [kumbu]\n---\n```\n");
    renderApp(`/notes${ALIAS}`);
    expect(await screen.findByText(/aliases: \[kumbu\]/)).toBeInTheDocument();
    expect(screen.queryByText(HINT)).not.toBeInTheDocument();
  });

  it("is a Read mode hint: Write mode has the text itself", async () => {
    aliasApi(chris);
    renderApp(`/notes${ALIAS}`);
    await hint();
    const write = screen.getByRole("button", { name: "Write" });
    await waitFor(() => expect(write).toBeEnabled());
    await userEvent.click(write);
    await screen.findByRole("textbox", { name: "Markdown source" });
    expect(screen.queryByText(HINT)).not.toBeInTheDocument();
  });
});

describe("adding a property", () => {
  /** The plan, read again as the last write left it; every PUT's body is kept. */
  function planApi(extra: Parameters<typeof fakeApi>[0] = {}) {
    let current = noteView(PLAN, source);
    const puts: { content: string; version: string }[] = [];
    fetched = noteApi({
      "GET /api/vaults/Work/calendar/entry": () => json(200, { entry: null, couldntRead: null, canWrite: true }),
      [`GET /api/notes${PLAN}`]: () => json(200, current),
      [`PUT /api/notes${PLAN}`]: async (r) => {
        const body = (await r.json()) as { content: string; version: string };
        puts.push(body);
        current = noteView(PLAN, body.content, `v${puts.length + 1}`);
        return json(200, current);
      },
      ...extra,
    });
    return puts;
  }
  async function adding(name: string, value: string) {
    renderApp(planUrl);
    await userEvent.click(await screen.findByRole("button", { name: "Add property" }));
    const dialog = await screen.findByRole("dialog", { name: "Add property" });
    await userEvent.type(within(dialog).getByLabelText("Name"), name);
    if (value) await userEvent.type(within(dialog).getByLabelText("Value"), value);
    await userEvent.click(within(dialog).getByRole("button", { name: "Add" }));
    return dialog;
  }
  const settle = () => act(() => new Promise((resolve) => setTimeout(resolve, 50)));
  let fetched: ReturnType<typeof fakeApi>;

  it("asks the calendar again after the write: a `when:` added changes its line", async () => {
    const puts = planApi();
    await adding("owner", "Alice");
    await waitFor(() => expect(puts).toHaveLength(1));
    // Once on opening the note, and again after the write.
    await waitFor(() => expect(asked(fetched, "/api/vaults/Work/calendar/entry")).toBeGreaterThan(1));
    expect(asked(fetched, "/api/vaults")).toBeGreaterThan(1);
  });

  it("writes nothing while the editor's text isn't saved, says so and stays open", async () => {
    const puts = planApi();
    useEditor.getState().setBeforeRead(async () => false);
    const dialog = await adding("owner", "Alice");
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("Your typing isn't saved yet; try again in a moment.");
    await settle();
    expect(puts).toEqual([]);
    expect(dialog).toBeInTheDocument();
  });

  it("writes name: value as the properties' last line with the note's version, every other byte as it was", async () => {
    const puts = planApi();
    const dialog = await adding("owner", "Alice");
    await waitFor(() => expect(puts).toEqual([{ content: source.replace("tags: [work]\n", "tags: [work]\nowner: Alice\n"), version: "v1" }]));
    await waitFor(() => expect(dialog).not.toBeInTheDocument());
    expect(within(screen.getByLabelText("Properties")).getAllByRole("term").map((t) => t.textContent)).toEqual(["tags", "owner"]);
  });

  it("writes a value YAML would take for something else in quotes", async () => {
    const puts = planApi();
    await adding("done", "true");
    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0].content.split("\n").slice(0, 4)).toEqual(["---", "tags: [work]", 'done: "true"', "---"]);
  });

  it("refuses a name that is there already, naming it, and writes nothing", async () => {
    const puts = planApi();
    const dialog = await adding("tags", "more");
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("tags is already in the properties: change it in the note.");
    expect(within(within(dialog).getByRole("alert")).getByText("tags", { selector: "code" })).toBeInTheDocument();
    await settle();
    expect(puts).toEqual([]);
  });

  it("refuses a name that isn't a plain key", async () => {
    const puts = planApi();
    const dialog = await adding("2nd", "x");
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("Use letters, digits, - and _, starting with a letter.");
    await settle();
    expect(puts).toEqual([]);
  });

  it("refuses a value that wouldn't read back as given", async () => {
    const puts = planApi();
    renderApp(planUrl);
    await userEvent.click(await screen.findByRole("button", { name: "Add property" }));
    const dialog = await screen.findByRole("dialog", { name: "Add property" });
    await userEvent.type(within(dialog).getByLabelText("Name"), "bell");
    fireEvent.change(within(dialog).getByLabelText("Value"), { target: { value: "ring\u0007" } });
    await userEvent.click(within(dialog).getByRole("button", { name: "Add" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("That value can't be written as a property here.");
    await settle();
    expect(puts).toEqual([]);
  });

  it("on a conflict says so in the dialog and shows the newer version", async () => {
    const newer = source.replace("- [x] read", "- [x] read\n- [ ] added by liz");
    let puts = 0;
    planApi({
      [`PUT /api/notes${PLAN}`]: () => {
        puts++;
        return json(409, { error: "changed", current: { content: newer, version: "v7" } });
      },
    });
    const dialog = await adding("owner", "Alice");
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("This note changed elsewhere; try again.");
    expect(screen.getByRole("checkbox", { name: "added by liz" })).toBeInTheDocument();
    expect(puts).toBe(1);
  });
});
