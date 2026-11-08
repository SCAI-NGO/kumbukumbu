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
    ...overrides,
  });
}

afterEach(() => {
  cleanup();
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

  it("offers each skill as a .zip", async () => {
    api();
    renderApp("/skills/installed");
    await screen.findByText("builtin");
    const zipOf = (name: string) => within(screen.getByText(name).closest("li")!).getByRole("link", { name: "Download .zip" });
    expect(zipOf("work-x")).toHaveAttribute("href", "/api/skills/zip?path=%2FWork%2F_skills%2Fx");
    expect(zipOf("spaced")).toHaveAttribute("href", "/api/skills/zip?path=%2FMy%20Vault%2F_skills%2Fspaced");
    expect(zipOf("builtin")).toHaveAttribute("href", "/api/skills/zip?path=system%3Abuiltin");
    expect(zipOf("mine")).toHaveAttribute("download");
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
});
