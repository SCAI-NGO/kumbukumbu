// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { aiPrompt } from "../note/copyForAi";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const checkpoint = { name: "before-cleanup", createdAt: "2026-10-19T12:02:00Z", notes: 2 };
const tree = {
  checkpoint,
  root: {
    name: "Work",
    path: "/Work",
    folders: [{ name: "ideas", path: "/Work/ideas", folders: [], notes: [{ name: "xyz.md", path: "/Work/ideas/xyz.md", size: 5 }] }],
    notes: [{ name: "plan.md", path: "/Work/plan.md", size: 40 }],
  },
};
const plan = {
  path: "/Work/plan.md",
  content: "# Plan\n\n- [ ] task\n\n[[xyz]] and [[gone]] and [[two]]\n\n![[xyz]]\n",
  links: [
    { line: 5, raw: "[[xyz]]", kind: "note", path: "/Work/ideas/xyz.md" },
    { line: 5, raw: "[[gone]]", kind: "not_found", target: "gone" },
    { line: 5, raw: "[[two]]", kind: "ambiguous", candidates: ["/Work/a/two.md", "/Work/b/two.md"] },
    { line: 7, raw: "![[xyz]]", kind: "note", path: "/Work/ideas/xyz.md" },
  ],
};

function api(extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    "GET /api/changes": () => json(200, []),
    "GET /api/vaults/Work/checkpoints/before-cleanup/tree": () => json(200, tree),
    "GET /api/vaults/Work/checkpoints/before-cleanup/notes/plan.md": () => json(200, plan),
    ...extra,
  });
}

describe("a checkpoint in the web app", () => {
  it("shows the checkpoint's tree under a read-only banner", async () => {
    api();
    renderApp("/checkpoints/Work/before-cleanup");

    const banner = await screen.findByRole("region", { name: "Checkpoint" });
    expect(banner).toHaveTextContent("Checkpoint before-cleanup");
    await waitFor(() => expect(banner).toHaveTextContent("19 Oct 2026"));
    expect(banner).toHaveTextContent("read only");
    expect(within(banner).getByRole("button", { name: "Back to today" })).toBeInTheDocument();
    expect(await screen.findByRole("link", { name: "xyz" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "plan" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Actions for/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "New note here" })).toBeNull();
    expect(screen.queryByRole("button", { name: /^(Read|Write)$/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Note actions" })).toBeNull();
  });

  it("says so when the checkpoint holds no notes", async () => {
    // A folder checkpoint of an empty folder: the folders above it, and no note anywhere.
    const empty = {
      checkpoint: { ...checkpoint, notes: 0 },
      root: { name: "Work", path: "/Work", folders: [{ name: "ideas", path: "/Work/ideas", folders: [], notes: [] }], notes: [] },
    };
    api({ "GET /api/vaults/Work/checkpoints/before-cleanup/tree": () => json(200, empty) });
    renderApp("/checkpoints/Work/before-cleanup");

    expect(await screen.findByText("This checkpoint holds no notes.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "ideas" })).toBeNull();
    const banner = screen.getByRole("region", { name: "Checkpoint" });
    expect(within(banner).getByRole("button", { name: "Back to today" })).toBeInTheDocument();
  });

  /** The plan note, open. */
  async function openPlan() {
    const fetch = api();
    renderApp("/checkpoints/Work/before-cleanup/plan.md");
    return { fetch, article: await screen.findByRole("article") };
  }

  it("opens a note read-only and keeps links inside the checkpoint", async () => {
    const { article } = await openPlan();
    const inside = "/checkpoints/Work/before-cleanup/ideas/xyz.md";
    const xyz = within(article).getAllByRole("link", { name: /xyz/ });
    expect(xyz).toHaveLength(2);
    for (const link of xyz) expect(link).toHaveAttribute("href", inside);

    expect(within(article).getByText("gone")).toBeInTheDocument();
    expect(within(article).getByText("not in this checkpoint")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Create the note “gone”" })).toBeNull();

    await userEvent.click(within(article).getByRole("button", { name: /two/ }));
    const dialog = screen.getByRole("dialog");
    const hrefs = within(dialog).getAllByRole("link").map((a) => a.getAttribute("href"));
    expect(hrefs).toEqual(["/checkpoints/Work/before-cleanup/a/two.md", "/checkpoints/Work/before-cleanup/b/two.md"]);
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(within(article).getByRole("checkbox")).toBeDisabled();
    expect(screen.queryByRole("textbox")).toBeNull();
  });

  it("keeps Markdown links inside the checkpoint", async () => {
    api({
      "GET /api/vaults/Work/checkpoints/before-cleanup/notes/plan.md": () =>
        json(200, {
          path: "/Work/plan.md",
          content: "[the idea](ideas/xyz.md) and [gone](/Work/gone.md)\n",
          links: [
            { line: 1, raw: "[the idea](ideas/xyz.md)", kind: "note", target: "ideas/xyz.md", path: "/Work/ideas/xyz.md", candidates: [] },
            { line: 1, raw: "[gone](/Work/gone.md)", kind: "not_found", target: "/Work/gone.md", path: null, candidates: [] },
          ],
        }),
    });
    renderApp("/checkpoints/Work/before-cleanup/plan.md");
    const article = await screen.findByRole("article");
    expect(await within(article).findByRole("link", { name: "the idea" })).toHaveAttribute("href", "/checkpoints/Work/before-cleanup/ideas/xyz.md");
    expect(within(article).getByText("not in this checkpoint")).toBeInTheDocument();
    expect(within(article).queryByRole("link", { name: "gone" })).toBeNull();
    expect(screen.queryByRole("button", { name: /Create the note/ })).toBeNull();
  });

  it("never asks for today's notes or links", async () => {
    const { fetch, article } = await openPlan();
    await userEvent.click(within(article).getByRole("button", { name: /two/ }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));

    // Nothing of today's vault is asked for or written.
    const calls = fetch.mock.calls.map(([r]) => ({ method: (r as Request).method, path: new URL((r as Request).url).pathname }));
    expect(calls.every((c) => c.method === "GET")).toBe(true);
    for (const c of calls) expect(c.path).not.toMatch(/^\/api\/(notes|links|backlinks|version)\/|^\/api\/vaults\/Work\/tree$/);
  });

  it("encodes names in the route and the API", async () => {
    const fetch = api({
      "GET /api/vaults/My%20Diary/checkpoints/cp/tree": () =>
        json(200, { checkpoint: { ...checkpoint, name: "cp" }, root: { name: "My Diary", path: "/My Diary", folders: [], notes: [] } }),
      "GET /api/vaults/My%20Diary/checkpoints/cp/notes/B%C3%BCcher/%C3%9C%C2%B2.md": () =>
        json(200, { path: "/My Diary/Bücher/Ü².md", content: "Hello there\n", links: [] }),
    });
    renderApp("/checkpoints/My%20Diary/cp/B%C3%BCcher/%C3%9C%C2%B2.md");

    expect(await screen.findByText("Hello there")).toBeInTheDocument();
    const paths = fetch.mock.calls.map(([r]) => new URL((r as Request).url).pathname);
    expect(paths).toContain("/api/vaults/My%20Diary/checkpoints/cp/notes/B%C3%BCcher/%C3%9C%C2%B2.md");
  });

  it("a checkpoint that is gone says so", async () => {
    api({ "GET /api/vaults/Work/checkpoints/before-cleanup/tree": () => json(404, { error: "There is no checkpoint 'before-cleanup' in Work." }) });
    renderApp("/checkpoints/Work/before-cleanup");

    expect(await screen.findByText("There is no checkpoint 'before-cleanup' in Work.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Back to today" })).toBeInTheDocument();
  });

  it("shows no cached tree of a checkpoint that was removed meanwhile", async () => {
    api({ "GET /api/vaults/Work/checkpoints/before-cleanup/tree": () => json(404, { error: "There is no checkpoint 'before-cleanup' in Work." }) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(["checkpoint-tree", "Work", "before-cleanup"], tree);
    renderApp("/checkpoints/Work/before-cleanup", queryClient);

    expect(await screen.findByText("There is no checkpoint 'before-cleanup' in Work.")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "plan" })).toBeNull();
    expect(screen.getByRole("button", { name: "Back to today" })).toBeInTheDocument();
  });

  it("Back to today leaves the checkpoint", async () => {
    api();
    renderApp("/checkpoints/Work/before-cleanup");

    await userEvent.click(await screen.findByRole("button", { name: "Back to today" }));

    expect(await screen.findByTestId("shell")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("region", { name: "Checkpoint" })).toBeNull());
  });

  it("copies the note for your AI", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    await openPlan();

    await userEvent.click(screen.getByRole("button", { name: "Copy for your AI" }));

    expect(writeText).toHaveBeenCalledTimes(1);
    expect(writeText).toHaveBeenCalledWith(aiPrompt("Work", "before-cleanup", "2026-10-19T12:02:00Z", "/Work/plan.md"));
    expect(await screen.findByRole("status")).toHaveTextContent("Copied. Paste it into your AI's chat.");
  });

  it("does not carry the Copied status over to another note", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    api({ "GET /api/vaults/Work/checkpoints/before-cleanup/notes/ideas/xyz.md": () => json(200, { path: "/Work/ideas/xyz.md", content: "Other note\n", links: [] }) });
    renderApp("/checkpoints/Work/before-cleanup/plan.md");
    const article = await screen.findByRole("article");
    await userEvent.click(screen.getByRole("button", { name: "Copy for your AI" }));
    expect(await screen.findByRole("status")).toBeInTheDocument();

    await userEvent.click(within(article).getAllByRole("link", { name: /xyz/ })[0]);

    expect(await screen.findByText("Other note")).toBeInTheDocument();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("says so when the clipboard refuses", async () => {
    const writeText = vi.fn().mockRejectedValue(new Error("denied"));
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    await openPlan();

    await userEvent.click(screen.getByRole("button", { name: "Copy for your AI" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't copy. Select the text and copy it by hand.");
    expect(screen.queryByText(/^Copied/)).toBeNull();
  });

  it("says so when there is no clipboard", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: undefined });
    await openPlan();

    await userEvent.click(screen.getByRole("button", { name: "Copy for your AI" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't copy. Select the text and copy it by hand.");
    expect(screen.queryByText(/^Copied/)).toBeNull();
  });
});
