// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const own = (name: string) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false });
const share = {
  id: "k7qm",
  username: "ada.k7qm",
  label: "Work PC",
  vaults: { Work: "write" },
  createdAt: "2026-10-01T09:00:00Z",
  lastUsedAt: null,
  revokedAt: null,
  expiresAt: null,
  lostVaults: [],
};
const password = "K7QM-ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789-ABCD-EFGH-JKMN-PQRS";

function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    // Publishing is on, so a "May publish" that showed up would be the dialog's doing, not the server's.
    "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true, publishEnabled: true }),
    "GET /api/vaults": () => json(200, [own("Work"), own("Personal")]),
    "GET /api/dav-shares": () => json(200, { enabled: true, shares: [] }),
    ...overrides,
  });
}

async function openNewShare() {
  renderApp("/access/dav");
  await userEvent.click(await screen.findByRole("button", { name: "New share" }));
  return screen.getByRole("dialog", { name: "New WebDAV share" });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
});

describe("NewShareDialog", () => {
  it("has no May publish checkbox", async () => {
    api();
    const dialog = await openNewShare();
    await userEvent.click(within(await within(dialog).findByRole("group", { name: "Work" })).getByLabelText("Read and write"));
    expect(within(dialog).queryByRole("checkbox")).not.toBeInTheDocument();
    expect(within(dialog).queryByText("May publish")).not.toBeInTheDocument();
  });

  it("needs a label and a vault", async () => {
    api();
    const dialog = await openNewShare();
    const create = within(dialog).getByRole("button", { name: "Create share" });
    expect(create).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Label"), "Work PC");
    expect(create).toBeDisabled();
    await userEvent.click(within(await within(dialog).findByRole("group", { name: "Work" })).getByLabelText("Read"));
    expect(create).toBeEnabled();
  });

  it("posts label, vaults and expiry", async () => {
    const bodies: unknown[] = [];
    api({ "POST /api/dav-shares": async (r) => (bodies.push(await r.json()), json(201, { password, share })) });
    const dialog = await openNewShare();
    await userEvent.type(within(dialog).getByLabelText("Label"), "Work PC");
    await userEvent.click(within(await within(dialog).findByRole("group", { name: "Work" })).getByLabelText("Read and write"));
    await userEvent.click(within(within(dialog).getByRole("group", { name: "Personal" })).getByLabelText("Maintain"));
    // An API key's choices, 90 days unless chosen otherwise.
    const expiry = within(dialog).getByRole("group", { name: "Expires after" });
    expect(within(expiry).getAllByRole("radio").map((r) => r.parentElement!.textContent)).toEqual(["30 days", "90 days", "1 year", "Never"]);
    expect(within(expiry).getByLabelText("90 days")).toBeChecked();
    await userEvent.click(within(dialog).getByRole("button", { name: "Create share" }));
    await screen.findByText(password);
    expect(bodies).toEqual([{ label: "Work PC", vaults: { Work: "write", Personal: "maintain" }, expiresInDays: 90 }]);
  });

  it("leaves the expiry out for a share that never expires", async () => {
    const bodies: unknown[] = [];
    api({ "POST /api/dav-shares": async (r) => (bodies.push(await r.json()), json(201, { password, share })) });
    const dialog = await openNewShare();
    await userEvent.type(within(dialog).getByLabelText("Label"), "Work PC");
    await userEvent.click(within(await within(dialog).findByRole("group", { name: "Work" })).getByLabelText("Read and write"));
    await userEvent.click(within(dialog).getByLabelText("Never"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Create share" }));
    await screen.findByText(password);
    expect(bodies).toEqual([{ label: "Work PC", vaults: { Work: "write" } }]);
  });

  it("asks for the password", async () => {
    let posts = 0;
    api({
      // The first try finds the password window closed; the repeat after the password goes through.
      "POST /api/dav-shares": () =>
        posts++ === 0 ? json(401, { error: "Give your password again.", type: "password-required" }) : json(201, { password, share }),
      "POST /api/session/confirm": () => noContent(),
    });
    const dialog = await openNewShare();
    await userEvent.type(within(dialog).getByLabelText("Label"), "Work PC");
    await userEvent.click(within(await within(dialog).findByRole("group", { name: "Work" })).getByLabelText("Read and write"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Create share" }));

    const prompt = await screen.findByRole("alertdialog", { name: "Confirm your password" });
    await userEvent.type(within(prompt).getByLabelText("Your password"), "correct horse");
    await userEvent.click(within(prompt).getByRole("button", { name: "Confirm" }));
    expect(await screen.findByText(password)).toBeInTheDocument();
    await waitFor(() => expect(posts).toBe(2));
  });

  it("shows the server's sentence on a refusal", async () => {
    api({ "POST /api/dav-shares": () => json(409, { error: "You have a share with this label already." }) });
    const dialog = await openNewShare();
    await userEvent.type(within(dialog).getByLabelText("Label"), "Work PC");
    await userEvent.click(within(await within(dialog).findByRole("group", { name: "Work" })).getByLabelText("Read and write"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Create share" }));
    expect(await within(dialog).findByText("You have a share with this label already.")).toBeInTheDocument();
  });
});
