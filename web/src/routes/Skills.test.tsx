// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const skill = (o: Record<string, unknown>) => ({ description: "Does it.", path: null, source: null, lastEditor: null, byOther: false, warnings: [], canWrite: false, ...o });

function api(withoutSkills: string[] = [], overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Archive", notes: 1, canWrite: true, level: "owner" }, { name: "Skills", notes: 1, canWrite: true, level: "owner" }, { name: "Work", notes: 3, canWrite: true, level: "owner" }]),
    "GET /api/skills": () =>
      json(200, {
        skills: [
          skill({ name: "work-x", location: "Work", path: "/Work/_skills/x", canWrite: true, lastEditor: "Ada" }),
          skill({ name: "mine", location: "Skills", path: "/Skills/mine", canWrite: true, warnings: ["No description"] }),
          skill({ name: "spaced", location: "My Vault", path: "/My Vault/_skills/spaced" }),
          skill({ name: "builtin", location: "system", canWrite: true }),
        ],
        withoutSkills,
      }),
    "POST /api/skills/updates/check": () => json(200, []),
    ...overrides,
  });
}

/** The links the page clicks to download, by their address; nothing is fetched. */
function clicks() {
  const links: string[] = [];
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    links.push(this.getAttribute("href") ?? "");
  });
  return links;
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
});

describe("Skills", () => {
  it("groups skills by system, Skills and vault", async () => {
    api();
    renderApp("/skills/installed");
    await screen.findByText("builtin");
    expect(screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toEqual(["System", "Skills", "Work", "My Vault"]);
    for (const section of screen.getAllByRole("region")) expect(section.getAttribute("aria-labelledby")).not.toMatch(/\s/);
    expect(screen.getByText("No description")).toBeInTheDocument();
    expect(screen.getByText(/last changed by Ada/)).toBeInTheDocument();
  });

  it("names connections without Skills and links to Access", async () => {
    api(["Claude", "Desk key"]);
    renderApp("/skills/installed");
    const note = await screen.findByRole("status");
    expect(note).toHaveTextContent("Claude, Desk key can't use your skills yet: give them the vault Skills in Access.");
    expect(within(note).getByRole("link", { name: "Access" })).toHaveAttribute("href", "/access/keys");
  });

  it("copies a skill to Skills by default", async () => {
    let body: unknown;
    api([], { "POST /api/skills/copy": async (r) => ((body = await r.json()), json(200, { path: "/Skills/x" })) });
    renderApp("/skills/installed");
    const card = (await screen.findByText("work-x")).closest("li")!;
    await userEvent.click(within(card).getByRole("button", { name: "Copy to…" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Copy" }));
    await waitFor(() => expect(body).toEqual({ from: "/Work/_skills/x", to: "Skills" }));
  });

  it("shows the dialog's loading state while the vaults load", async () => {
    api([], { "GET /api/vaults": () => new Promise<Response>(() => {}) });
    renderApp("/skills/installed");
    const card = (await screen.findByText("work-x")).closest("li")!;
    await userEvent.click(within(card).getByRole("button", { name: "Copy to…" }));
    expect(within(screen.getByRole("dialog")).getByText("Loading…")).toBeInTheDocument();
    expect(screen.queryByText(/no other vault/i)).not.toBeInTheDocument();
  });

  it("asks for another name when the name is taken", async () => {
    api([], { "POST /api/skills/copy": () => json(409, { error: "taken", type: "name-taken" }) });
    renderApp("/skills/installed");
    const card = (await screen.findByText("work-x")).closest("li")!;
    await userEvent.click(within(card).getByRole("button", { name: "Copy to…" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Copy" }));
    expect(await screen.findByLabelText("Name")).toBeInTheDocument();
  });

  it("offers a system skill as a plain .zip link", async () => {
    api();
    renderApp("/skills/installed");
    await screen.findByText("builtin");
    const link = within(screen.getByText("builtin").closest("li")!).getByRole("link", { name: "Download .zip" });
    expect(link).toHaveAttribute("href", "/api/skills/zip?path=system%3Abuiltin");
    expect(link).toHaveAttribute("download");
  });

  it("asks for the password for a vault skill's .zip by default", async () => {
    const ticket = vi.fn(async (r: Request) => {
      expect(await r.json()).toEqual({ password: "secret" });
      return json(200, { ticket: "t1" });
    });
    api([], {
      "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true }),
      "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
      "POST /api/export/ticket": ticket,
    });
    const links = clicks();
    renderApp("/skills/installed");
    const card = (await screen.findByText("spaced")).closest("li")!;
    await userEvent.click(within(card).getByRole("button", { name: "Download .zip" }));
    const dialog = await screen.findByRole("alertdialog", { name: "Download spaced?" });
    expect(links).toEqual([]);
    await userEvent.type(within(dialog).getByLabelText("Your password"), "secret");
    await userEvent.click(within(dialog).getByRole("button", { name: "Download" }));

    await waitFor(() => expect(links).toEqual(["/api/skills/zip?path=%2FMy%20Vault%2F_skills%2Fspaced&ticket=t1"]));
    expect(ticket).toHaveBeenCalledTimes(1);
  });

  it("asks for the password for a vault skill's .zip when the window is closed, though the setting is off", async () => {
    api([], {
      "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: false }),
      "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: false }),
    });
    const links = clicks();
    renderApp("/skills/installed");
    const card = (await screen.findByText("work-x")).closest("li")!;
    await userEvent.click(within(card).getByRole("button", { name: "Download .zip" }));
    const dialog = await screen.findByRole("alertdialog", { name: "Download work-x?" });
    expect(dialog).toHaveTextContent("It's a while since you last gave your password on this device");
    expect(links).toEqual([]);
  });

  it("downloads a vault skill's .zip without asking when the setting is off and the window open", async () => {
    const ticket = vi.fn(() => json(200, { ticket: "t1" }));
    api([], {
      "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true }),
      "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: false }),
      "POST /api/export/ticket": ticket,
    });
    const links = clicks();
    renderApp("/skills/installed");
    const card = (await screen.findByText("work-x")).closest("li")!;
    await userEvent.click(within(card).getByRole("button", { name: "Download .zip" }));
    await waitFor(() => expect(links).toEqual(["/api/skills/zip?path=%2FWork%2F_skills%2Fx"]));
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(ticket).not.toHaveBeenCalled();
  });

  it("offers no Move for a system skill", async () => {
    api();
    renderApp("/skills/installed");
    const card = (await screen.findByText("builtin")).closest("li")!;
    expect(within(card).getByRole("button", { name: "Copy to…" })).toBeInTheDocument();
    expect(within(card).queryByRole("button", { name: "Move to…" })).not.toBeInTheDocument();
    const mine = screen.getByText("mine").closest("li")!;
    expect(within(mine).getByRole("button", { name: "Move to…" })).toBeInTheDocument();
  });

  it("keeps the tab in the URL", async () => {
    api();
    const router = renderApp("/skills");
    await screen.findByText("builtin");
    expect(router.state.location.pathname).toBe("/skills/installed");
    const tab = (n: string) => within(screen.getByRole("tablist", { name: "Skills" })).getByRole("tab", { name: n });
    expect(tab("Installed")).toHaveAttribute("aria-selected", "true");
    expect(tab("Try")).toHaveAttribute("href", "/skills/try");
    await userEvent.click(tab("Try"));
    await waitFor(() => expect(router.state.location.pathname).toBe("/skills/try"));
  });

  it("checks for updates when the tab opens and on the button", async () => {
    let checks = 0;
    api([], { "POST /api/skills/updates/check": () => (checks++, json(200, [])) });
    renderApp("/skills/installed");
    await screen.findByText("builtin");
    await waitFor(() => expect(checks).toBe(1));
    await waitFor(() => expect(screen.getByRole("button", { name: "Check for updates" })).toBeEnabled());
    await userEvent.click(screen.getByRole("button", { name: "Check for updates" }));
    await waitFor(() => expect(checks).toBe(2));
  });

  it("shows update available, source gone and origin edited", async () => {
    api([], {
      "GET /api/skills": () =>
        json(200, {
          skills: [
            skill({ name: "fresh", location: "Skills", path: "/Skills/fresh", update: "available", canWrite: true }),
            skill({ name: "lost", location: "Skills", path: "/Skills/lost" }),
            skill({ name: "tweaked", location: "Skills", path: "/Skills/tweaked", originEdited: true }),
            skill({ name: "plain", location: "Skills", path: "/Skills/plain" }),
          ],
          withoutSkills: [],
        }),
      "POST /api/skills/updates/check": () => json(200, [{ path: "/Skills/lost", state: "gone" }, { path: "/Skills/plain", state: "current" }]),
    });
    renderApp("/skills/installed");
    const card = async (n: string) => (await screen.findByText(n)).closest("li")!;
    expect(within(await card("fresh")).getByText("Update available")).toBeInTheDocument();
    expect(within(await card("fresh")).getByRole("button", { name: "Review" })).toBeInTheDocument();
    expect(await within(await card("lost")).findByText("Source gone")).toBeInTheDocument();
    expect(within(await card("tweaked")).getByText("Origin edited: no updates")).toBeInTheDocument();
    expect(within(await card("tweaked")).queryByRole("button", { name: "Review" })).not.toBeInTheDocument();
    expect(within(await card("plain")).queryByText(/update|gone/i)).not.toBeInTheDocument();
  });
});
