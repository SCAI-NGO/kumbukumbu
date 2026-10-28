// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
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

describe("the note view", () => {
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
