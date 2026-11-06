// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, within } from "@testing-library/react";
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

function api(publications: unknown[]) {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: false, publishEnabled: true }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }]),
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/vault-events": () => json(200, []),
    "GET /api/publications": () => json(200, publications),
  });
}

describe("Published notes", () => {
  it("lists her publications with who published them", async () => {
    api([
      // The session's writer is her username; a key's or connection's its name.
      { path: "/Work/Routing/Questions.md", snapshotAt: at("2026-11-05T14:32:00"), older: false, changedAt: null, publishedBy: user.username },
      { path: "/Work/plan.md", snapshotAt: at("2026-11-04T09:05:00"), older: true, changedAt: at("2026-11-05T10:00:00"), publishedBy: "Claude" },
    ]);
    renderApp("/settings");
    const section = await screen.findByRole("region", { name: "Published notes" });
    const items = await within(section).findAllByRole("listitem");
    expect(items).toHaveLength(2);

    expect(items[0]).toHaveTextContent("/Work/Routing/Questions.md");
    expect(items[0]).toHaveTextContent("Snapshot of 5 Nov 2026, 14:32");
    expect(items[0]).toHaveTextContent("by you");
    expect(items[0]).not.toHaveTextContent("older than the note");
    expect(within(items[0]).getByRole("link", { name: "/Work/Routing/Questions.md" })).toHaveAttribute("href", "/notes/Work/Routing/Questions.md");

    expect(items[1]).toHaveTextContent("/Work/plan.md");
    expect(items[1]).toHaveTextContent("Snapshot of 4 Nov 2026, 09:05");
    expect(items[1]).toHaveTextContent("older than the note");
    expect(items[1]).toHaveTextContent("by “Claude”");
    expect(items[1]).not.toHaveTextContent("by you");
  });

  it("says when there are none", async () => {
    api([]);
    renderApp("/settings");
    const section = await screen.findByRole("region", { name: "Published notes" });
    expect(await within(section).findByText("No published notes.")).toBeInTheDocument();
    expect(within(section).queryByRole("listitem")).toBeNull();
  });
});
