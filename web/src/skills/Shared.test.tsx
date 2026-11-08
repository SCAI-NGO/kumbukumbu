// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";
import { useWithdrawn } from "./withdrawn";

const link = "https://kumbu.example.org/k#ABCD-EFGH-JKMN-PQRS-TVWX-YZ23-4567-89AB";

const skill = (o: Record<string, unknown>) => ({ description: "Does it.", source: null, lastEditor: null, byOther: false, warnings: [], canWrite: true, shared: false, ...o });

/** Alice's /Skills/mine, shared or not as `state.shared` says, and Bob's /Team/_skills/theirs in a vault shared with her. */
function api(state: { shared: boolean; changed?: boolean }, publishEnabled = true, overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, publishEnabled }),
    "GET /api/vaults": () =>
      json(200, [
        { name: "Skills", notes: 1, canWrite: true, level: "owner", kind: "skills" },
        { name: "Team", notes: 1, canWrite: true, level: "write", owner: "bob" },
      ]),
    "GET /api/skills": () =>
      json(200, {
        skills: [
          skill({ name: "mine", location: "Skills", path: "/Skills/mine", shared: state.shared }),
          skill({ name: "theirs", location: "Team", path: "/Team/_skills/theirs" }),
          skill({ name: "builtin", location: "system", path: null }),
        ],
        withoutSkills: [],
      }),
    "GET /api/skills/shared": () => json(200, state.shared ? [{ id: "p1", path: "/Skills/mine", snapshotAt: "2026-11-08T09:00:00Z", changed: state.changed ?? false }] : []),
    ...overrides,
  });
}

const card = async (name: string) => (await screen.findByText(name)).closest("li")!;

const tab = (name: string) => within(screen.getByRole("tablist", { name: "Skills" })).queryByRole("tab", { name });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useWithdrawn.getState().reset();
});

describe("Shared", () => {
  it("shows the link once and never again", async () => {
    const state = { shared: false };
    let body: unknown;
    api(state, true, {
      "POST /api/skills/shared": async (r) => {
        body = await r.json();
        state.shared = true;
        return json(201, { link, snapshotAt: "2026-11-08T09:00:00Z" });
      },
    });
    const router = renderApp("/skills/installed");

    // Share is for her own skills alone: not a member's, not a system skill.
    expect(within(await card("theirs")).queryByRole("button", { name: "Share" })).not.toBeInTheDocument();
    expect(within(await card("builtin")).queryByRole("button", { name: "Share" })).not.toBeInTheDocument();
    await userEvent.click(within(await card("mine")).getByRole("button", { name: "Share" }));

    expect(await screen.findByText(link)).toBeInTheDocument();
    expect(body).toEqual({ path: "/Skills/mine" });
    expect(screen.getByText(/shown only now/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Leave without it" }));

    await waitFor(() => expect(screen.queryByText(link)).not.toBeInTheDocument());
    await waitFor(() => expect(within(screen.getByText("mine").closest("li")!).queryByRole("button", { name: "Share" })).not.toBeInTheDocument());
    await userEvent.click(tab("Shared")!);
    await waitFor(() => expect(router.state.location.pathname).toBe("/skills/shared"));
    expect(await screen.findByText("/Skills/mine")).toBeInTheDocument();
    expect(screen.queryByText(/#/)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /copy link/i })).not.toBeInTheDocument();
  });

  it("offers no second link while one exists", async () => {
    api({ shared: true, changed: true });
    renderApp("/skills/installed");

    const mine = await card("mine");
    expect(within(mine).queryByRole("button", { name: "Share" })).not.toBeInTheDocument();
    expect(within(mine).getByText(/shared by link/)).toBeInTheDocument();

    await userEvent.click(tab("Shared")!);
    const item = (await screen.findByText("/Skills/mine")).closest("li")!;
    expect(within(item).getByText(/changed since/i)).toBeInTheDocument();
    expect(within(item).getByRole("button", { name: "Update" })).toBeInTheDocument();
    expect(within(item).getByRole("button", { name: "Withdraw" })).toBeInTheDocument();
    expect(within(item).queryByRole("button", { name: /copy link|share/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/#/)).not.toBeInTheDocument();
  });

  it("updates a changed skill's copy", async () => {
    const state = { shared: true, changed: true };
    let updated = false;
    api(state, true, {
      "POST /api/skills/shared/p1/update": () => ((updated = true), (state.changed = false), json(200, { snapshotAt: "2026-11-08T10:00:00Z" })),
    });
    renderApp("/skills/shared");

    await userEvent.click(within((await screen.findByText("/Skills/mine")).closest("li")!).getByRole("button", { name: "Update" }));

    await waitFor(() => expect(updated).toBe(true));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Update" })).not.toBeInTheDocument());
  });

  it("warns before sharing anew", async () => {
    const state = { shared: true };
    let withdrawn = false;
    let shared = false;
    api(state, true, {
      "DELETE /api/skills/shared/p1": () => ((withdrawn = true), (state.shared = false), noContent()),
      "POST /api/skills/shared": () => ((shared = true), (state.shared = true), json(201, { link, snapshotAt: "2026-11-08T09:00:00Z" })),
    });
    renderApp("/skills/shared");

    await userEvent.click(within((await screen.findByText("/Skills/mine")).closest("li")!).getByRole("button", { name: "Withdraw" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Withdraw" }));
    await waitFor(() => expect(withdrawn).toBe(true));
    expect(await screen.findByText("No skill is shared by link.")).toBeInTheDocument();

    await userEvent.click(tab("Installed")!);
    await userEvent.click(within(await card("mine")).getByRole("button", { name: "Share" }));
    const warning = screen.getByRole("alertdialog");
    expect(within(warning).getByText("Copies made from the old link will be told their source is gone and get no more updates.")).toBeInTheDocument();
    expect(shared).toBe(false);
    await userEvent.click(within(warning).getByRole("button", { name: "Share anew" }));

    await waitFor(() => expect(shared).toBe(true));
    expect(await screen.findByText(link)).toBeInTheDocument();
  });

  it("hides the tab when publishing is off", async () => {
    api({ shared: false }, false);
    const router = renderApp("/skills/shared");

    await waitFor(() => expect(router.state.location.pathname).toBe("/skills/installed"));
    await card("mine");
    expect(tab("Shared")).not.toBeInTheDocument();
    expect(tab("Try")).toBeInTheDocument();
    expect(within(await card("mine")).queryByRole("button", { name: "Share" })).not.toBeInTheDocument();
  });
});
