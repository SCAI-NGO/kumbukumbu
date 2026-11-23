// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

// Local times, so the rows show them as written whatever the test machine's time zone.
const at = (local: string) => new Date(local).toISOString();

const fresh = { path: "/Work/Routing/Questions.md", snapshotAt: at("2026-11-05T14:32:00"), older: false, changedAt: null, publishedBy: user.username };
const stale = { path: "/Work/plan.md", snapshotAt: at("2026-11-04T09:05:00"), older: true, changedAt: at("2026-11-05T10:00:00"), publishedBy: "Claude" };

function api(publications: unknown[], extra: Parameters<typeof fakeApi>[0] = {}, publishEnabled = true) {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true, publishEnabled }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }]),
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/vault-events": () => json(200, []),
    "GET /api/publications": () => json(200, publications),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    ...extra,
  });
}

const list = async () => screen.findByRole("list", { name: "Published notes" });
const rows = async () => within(await list()).findAllByRole("listitem");

describe("Published", () => {
  it("is a window of its own", async () => {
    api([fresh]);
    renderApp("/published");
    expect(await screen.findByRole("dialog", { name: "Published" })).toBeInTheDocument();
    expect(await rows()).toHaveLength(1);
  });

  it("lists her publications with who published them, and whether the copy is older", async () => {
    api([fresh, stale]);
    renderApp("/published");
    const items = await rows();
    expect(items).toHaveLength(2);

    expect(items[0]).toHaveTextContent("Snapshot of 5 Nov 2026, 14:32");
    expect(items[0]).toHaveTextContent("Up to date");
    expect(items[0]).toHaveTextContent("by you");
    expect(items[0]).toHaveAttribute("data-tone", "green");
    expect(within(items[0]).getByRole("link", { name: "/Work/Routing/Questions.md" })).toHaveAttribute("href", "/notes/Work/Routing/Questions.md");

    expect(items[1]).toHaveTextContent("Snapshot of 4 Nov 2026, 09:05");
    expect(items[1]).toHaveTextContent("Copy is older than the note");
    expect(items[1]).toHaveTextContent("by “Claude”");
    expect(items[1]).not.toHaveTextContent("by you");
    expect(items[1]).toHaveAttribute("data-tone", "yellow");
    expect(screen.getByText("1 of 2 copies is older than its note.")).toBeInTheDocument();
  });

  it("names a bundle's linked notes", async () => {
    api([{ ...fresh, notes: 3, depth: 1, without: [] }]);
    renderApp("/published");
    expect((await rows())[0]).toHaveTextContent("with 2 linked notes");
  });

  it("offers Update copy only where the copy is older, and updates it in place", async () => {
    let older = true;
    const put = vi.fn(() => {
      older = false;
      return json(200, { link: "https://kumbu.example/s#code", snapshotAt: at("2026-11-23T10:00:00"), plain: [] });
    });
    api([fresh, stale], {
      "GET /api/publications": () => json(200, [fresh, { ...stale, older }]),
      "PUT /api/publications/Work/plan.md": put,
    });
    renderApp("/published");
    const items = await rows();
    expect(within(items[0]).queryByRole("button", { name: /Update copy/ })).toBeNull();
    await userEvent.click(within(items[1]).getByRole("button", { name: "Update copy of /Work/plan.md" }));
    expect(put).toHaveBeenCalledTimes(1);
    await waitFor(async () => expect((await rows())[1]).toHaveTextContent("Up to date"));
  });

  it("shows a bundle's list before updating it, as the note's bar does", async () => {
    api([{ ...stale, notes: 2, depth: 1, without: [] }], {
      "GET /api/publication-previews/Work/plan.md": () => json(200, { notes: [{ path: "/Work/plan.md", title: "plan", depth: 0 }, { path: "/Work/b.md", title: "b", depth: 1 }], leftOut: [], plain: [], bytes: 0, tooMany: false, tooLarge: false }),
    });
    renderApp("/published");
    await userEvent.click(within((await rows())[0]).getByRole("button", { name: "Update copy of /Work/plan.md" }));
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
  });

  describe("Compare", () => {
    const note = (r: Request) => {
      const version = new URL(r.url).searchParams.get("version");
      if (version === "gone") return json(404, { error: "no such version" });
      return json(200, { path: "/Work/plan.md", content: version === "v1" ? "The plan.\nOld line." : "The plan.\nNew line.", version: version ?? "v2", lastWriter: "ada", lastWriteAt: "2026-11-17T09:00:00Z" });
    };

    it("shows the copy's version beside the note now, only where the copy is older", async () => {
      api([
        { ...fresh, copied: [{ path: fresh.path, version: "v9" }] },
        { ...stale, copied: [{ path: stale.path, version: "v1" }] },
      ], { "GET /api/notes/Work/plan.md": note });
      renderApp("/published");
      const items = await rows();
      expect(within(items[0]).queryByRole("button", { name: /Compare/ })).toBeNull();
      await userEvent.click(within(items[1]).getByRole("button", { name: "Compare copy of /Work/plan.md" }));
      const sheet = await screen.findByRole("dialog", { name: "Compare plan" });
      expect(within(sheet).queryByRole("button", { name: "Against the version before" })).toBeNull();
      expect(await within(sheet).findByRole("list", { name: "Published copy" })).toHaveTextContent("Old line.");
      expect(await within(sheet).findByRole("list", { name: "Now" })).toHaveTextContent("New line.");
    });

    it("says so when the version the copy was made from is no longer kept", async () => {
      api([{ ...stale, copied: [{ path: stale.path, version: "gone" }] }], { "GET /api/notes/Work/plan.md": note });
      renderApp("/published");
      await userEvent.click(within((await rows())[0]).getByRole("button", { name: "Compare copy of /Work/plan.md" }));
      expect(await within(await screen.findByRole("dialog", { name: "Compare plan" })).findByText("This version is no longer kept")).toBeInTheDocument();
    });

    it("picks a note of a bundle", async () => {
      api([{ ...stale, notes: 2, depth: 1, without: [], copied: [{ path: stale.path, version: "v1" }, { path: "/Work/b.md", version: "b1" }] }], {
        "GET /api/notes/Work/plan.md": note,
        "GET /api/notes/Work/b.md": (r) =>
          json(200, { path: "/Work/b.md", content: new URL(r.url).searchParams.get("version") ? "B then." : "B now.", version: "b2", lastWriter: "ada", lastWriteAt: "2026-11-17T09:00:00Z" }),
      });
      renderApp("/published");
      await userEvent.click(within((await rows())[0]).getByRole("button", { name: "Compare copy of /Work/plan.md" }));
      const sheet = await screen.findByRole("dialog", { name: "Compare plan" });
      await userEvent.selectOptions(within(sheet).getByLabelText("Note of the copy"), "/Work/b.md");
      expect(await within(sheet).findByRole("list", { name: "Published copy" })).toHaveTextContent("B then.");
    });
  });

  it("unpublishes after asking", async () => {
    let listed = [fresh, stale];
    const remove = vi.fn(() => {
      listed = [fresh];
      return new Response(null, { status: 204 });
    });
    api([], { "GET /api/publications": () => json(200, listed), "DELETE /api/publications/Work/plan.md": remove });
    renderApp("/published");
    await userEvent.click(within((await rows())[1]).getByRole("button", { name: "Unpublish /Work/plan.md" }));
    const ask = await screen.findByRole("alertdialog");
    await userEvent.click(within(ask).getByRole("button", { name: "Keep" }));
    expect(remove).not.toHaveBeenCalled();

    await userEvent.click(within((await rows())[1]).getByRole("button", { name: "Unpublish /Work/plan.md" }));
    await userEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Unpublish" }));
    expect(remove).toHaveBeenCalledTimes(1);
    await waitFor(async () => expect(await rows()).toHaveLength(1));
  });

  it("switched off, says so and offers only Unpublish", async () => {
    api([stale], {}, false);
    renderApp("/published");
    const item = (await rows())[0];
    expect(screen.getByText(/Publishing is switched off on this server/)).toBeInTheDocument();
    expect(within(item).queryByRole("button", { name: /Update copy/ })).toBeNull();
    expect(within(item).getByRole("button", { name: "Unpublish /Work/plan.md" })).toBeInTheDocument();
  });

  it("opens a note in the split view's focused pane, the other kept", async () => {
    api([stale]);
    useEditor.setState({ currentPath: "/Work/b.md", focused: "right", panes: { left: { path: "/Work/a.md", mode: "read" }, right: { path: "/Work/b.md", mode: "read" } } });
    renderApp("/published");
    const link = await within(await list()).findByRole("link", { name: "/Work/plan.md" });
    expect(decodeURIComponent(link.getAttribute("href")!)).toBe("/notes/Work/a.md?beside=Work/plan.md");
  });

  it("says when there are none", async () => {
    api([]);
    renderApp("/published");
    expect(await screen.findByText("No published notes.")).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: "Published notes" })).toBeNull();
  });

  it("opens a published note in place of the window", async () => {
    api([stale], {
      "GET /api/embeddings": () => json(200, []),
      "GET /api/member-notices": () => json(200, []),
      "GET /api/notes/Work/plan.md": () => json(200, { path: "/Work/plan.md", content: "The plan.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-17T09:00:00Z" }),
      "GET /api/version/Work/plan.md": () => json(200, { path: "/Work/plan.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-17T09:00:00Z" }),
      "GET /api/links/*": () => json(200, []),
      "GET /api/backlinks/*": () => json(200, []),
      "GET /api/changes": () => json(200, []),
      "POST /api/ws-ticket": () => json(503, { error: "later" }),
    });
    const router = renderApp(["/", "/published"]);
    await userEvent.click(await within(await list()).findByRole("link", { name: "/Work/plan.md" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/plan.md"));
    expect(router.state.historyAction).toBe("REPLACE");
  });
});
