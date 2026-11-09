// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, user } from "../test/app";
import TrashSheet from "./TrashSheet";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const entry = (path: string, version: string) => ({
  path,
  version,
  deletedAt: new Date(Date.now() - 2 * 86_400_000).toISOString(),
  deletedBy: "liz",
  expiresAt: new Date(Date.now() + 28 * 86_400_000).toISOString(),
  lines: 1,
  size: 4,
});

/** A trash of two notes in Work, emptied by whatever purge comes; `level` is the caller's in Work. */
function api({ level = "owner", windowOpen = true } = {}) {
  let notes = [entry("/Work/old.md", "t1"), entry("/Work/projects/plan.md", "t2")];
  const purges: unknown[] = [];
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: windowOpen, publishEnabled: false }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal", pending: false, canWrite: true, level }]),
    "GET /api/vaults/Work/trash": () => json(200, { retentionDays: 30, notes }),
    "GET /api/account/storage": () => json(200, { used: 1, limit: null }),
    "POST /api/vaults/Work/trash/purge": async (r) => {
      const body = (await r.json()) as { entries?: { path: string; version: string }[] };
      purges.push(body);
      const before = notes.length;
      notes = body.entries ? notes.filter((n) => !body.entries!.some((e) => e.path === n.path && e.version === n.version)) : [];
      return json(200, { deleted: before - notes.length });
    },
  });
  return { fetch, purges };
}

function renderSheet() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <TrashSheet vault="Work" onClose={() => {}} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return queryClient;
}

describe("TrashSheet", () => {
  it("empties the trash after the password", async () => {
    const { purges } = api({ windowOpen: false });
    renderSheet();
    await screen.findByRole("list", { name: "Removed notes" });
    await userEvent.click(await screen.findByRole("button", { name: "Empty trash…" }));

    const dialog = screen.getByRole("alertdialog", { name: "Empty the trash of Work?" });
    expect(dialog).toHaveTextContent("2 entries");
    expect(dialog).toHaveTextContent("their history goes with them");
    expect(dialog).toHaveTextContent("This can't be undone.");
    const confirm = within(dialog).getByRole("button", { name: "Empty trash" });
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "correct horse battery staple");
    await userEvent.click(confirm);

    expect(await screen.findByText("The trash is empty.")).toBeInTheDocument();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(purges).toEqual([{ password: "correct horse battery staple" }]);
  });

  it("deletes one entry permanently", async () => {
    const { purges } = api();
    renderSheet();
    const list = await screen.findByRole("list", { name: "Removed notes" });
    await userEvent.click(within(list).getByRole("button", { name: "Delete old permanently…" }));

    const dialog = screen.getByRole("alertdialog", { name: "Delete old permanently?" });
    expect(dialog).toHaveTextContent("Its history goes with it");
    expect(dialog).toHaveTextContent("This can't be undone.");
    // The password window is open: no password asked.
    expect(within(dialog).queryByLabelText("Your password")).not.toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete permanently" }));

    await vi.waitFor(() => expect(within(list).queryByText("old")).not.toBeInTheDocument());
    expect(within(list).getByText("plan")).toBeInTheDocument();
    expect(purges).toEqual([{ entries: [{ path: "/Work/old.md", version: "t1" }] }]);
  });

  it("offers neither to a member", async () => {
    api({ level: "maintain" });
    const queryClient = renderSheet();
    const list = await screen.findByRole("list", { name: "Removed notes" });
    // Her level is known: the buttons would show by now.
    await vi.waitFor(() => expect(queryClient.getQueryState(["vaults"])?.status).toBe("success"));
    expect(within(list).getByRole("button", { name: "Restore old" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /permanently/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Empty trash/ })).not.toBeInTheDocument();
  });
});
