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

function api(events: unknown[]) {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, []),
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/vault-events": () => json(200, events),
  });
}

describe("Shared vaults", () => {
  it("lists events across vaults, also of a vault she has lost", async () => {
    api([
      { kind: "role-changed", at: "2026-11-01T09:00:00Z", text: "Alice changed your role in Gartenarbeit to write.", role: "write", vault: "Gartenarbeit" },
      { kind: "membership-removed", at: "2026-11-01T11:00:00Z", text: "Alice removed you from a vault shared with you.", role: null, vault: null },
      { kind: "member-export", at: "2026-10-30T08:00:00Z", text: "You exported Work.", role: null, vault: "Work" },
    ]);
    renderApp("/settings");
    const section = await screen.findByRole("region", { name: "Shared vaults" });
    const items = await within(section).findAllByRole("listitem");
    // Newest first, whatever order the server gives.
    expect(items.map((li) => li.textContent)).toEqual([
      expect.stringContaining("Alice removed you from a vault shared with you."),
      expect.stringContaining("Alice changed your role in Gartenarbeit to write."),
      expect.stringContaining("You exported Work."),
    ]);
    expect(items[0]).toHaveTextContent("No longer shared with you");
    expect(items[1]).not.toHaveTextContent("No longer shared with you");
    // The day as the runner's time zone has it: 11:00 UTC is already 2 November in some.
    expect(items[0]).toHaveTextContent(new Date("2026-11-01T11:00:00Z").toLocaleDateString("en-GB", { dateStyle: "medium" }));
  });

  it("shows nothing for someone with no events", async () => {
    api([]);
    renderApp("/settings");
    const section = await screen.findByRole("region", { name: "Shared vaults" });
    expect(await within(section).findByText("No sharing events yet.")).toBeInTheDocument();
    expect(within(section).queryByRole("listitem")).toBeNull();
  });
});
