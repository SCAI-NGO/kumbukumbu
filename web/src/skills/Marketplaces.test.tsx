// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const commit = "0123456789abcdef0123456789abcdef01234567";
const scriptNotice = "This skill contains scripts. Kumbu² never runs them; a harness you give the skill to may.";
const rateLimited = "GitHub allows 60 requests an hour without a token; try again later, or ask your admin to set a GitHub token.";

const listing = {
  repo: "alice/skills",
  commit,
  skills: [
    { path: "skills/write-well", name: "write-well", description: "Writes clearly.", plugin: null, installable: true, reason: null, hasScripts: true, warnings: [] },
    { path: "skills/too-big", name: "too-big", description: "Huge.", plugin: null, installable: false, reason: "it is larger than the limit", hasScripts: false, warnings: [] },
    { path: "skills/odd", name: "odd", description: "Odd.", plugin: null, installable: true, reason: null, hasScripts: false, warnings: ["its name differs from its folder"] },
  ],
  plugins: [{ name: "elsewhere", source: "bob/plugins", installable: false }],
  note: null,
};

const preview = {
  repo: "alice/skills",
  path: "skills/write-well",
  commit,
  manifest: "# Write well\n\nShort sentences.\n\n<script>alert(1)</script>\n\n![diagram](https://example.org/x.png)",
  files: [
    { path: "SKILL.md", size: 120 },
    { path: "scripts/run.sh", size: 20 },
  ],
  skipped: ["big.bin"],
  hasScripts: true,
};

function api(state: { enabled?: boolean; marketplaces?: string[] } = {}, overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  const repos = state.marketplaces ?? ["alice/skills"];
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Skills", notes: 1, canWrite: true, level: "owner", kind: "skills" }]),
    "GET /api/skills": () => json(200, { skills: [], withoutSkills: [] }),
    "GET /api/skills/marketplaces": () =>
      json(200, { enabled: state.enabled ?? true, marketplaces: repos.map((repo) => ({ repo, addedAt: "2026-11-08T09:00:00Z" })) }),
    "GET /api/skills/marketplaces/alice/skills": () => json(200, listing),
    "GET /api/skills/marketplaces/alice/skills/skill": () => json(200, preview),
    ...overrides,
  });
}

const tab = (name: string) => within(screen.getByRole("tablist", { name: "Skills" })).queryByRole("tab", { name });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
});

describe("Marketplaces", () => {
  it("adds and removes a marketplace", async () => {
    const repos = ["alice/skills"];
    let added: unknown;
    let removed = false;
    api({ marketplaces: repos }, {
      "GET /api/skills/marketplaces": () => json(200, { enabled: true, marketplaces: repos.map((repo) => ({ repo, addedAt: "2026-11-08T09:00:00Z" })) }),
      "POST /api/skills/marketplaces": async (r) => {
        added = await r.json();
        repos.push("bob/tools");
        return json(201, { repo: "bob/tools", addedAt: "2026-11-08T10:00:00Z" });
      },
      "DELETE /api/skills/marketplaces/alice/skills": () => ((removed = true), repos.shift(), noContent()),
    });
    renderApp("/skills/marketplaces");

    expect(await screen.findByText("alice/skills")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Repository"), "bob/tools");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(await screen.findByText("bob/tools")).toBeInTheDocument();
    expect(added).toEqual({ repo: "bob/tools" });

    await userEvent.click(within((await screen.findByText("alice/skills")).closest("li")!).getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(removed).toBe(true));
    await waitFor(() => expect(screen.queryByText("alice/skills")).not.toBeInTheDocument());
  });

  it("browses and shows what isn't installable and why", async () => {
    api();
    renderApp("/skills/marketplaces");

    await userEvent.click(await screen.findByRole("button", { name: "Browse alice/skills" }));

    const good = (await screen.findByText("write-well")).closest("li")!;
    expect(within(good).getByText("Writes clearly.")).toBeInTheDocument();
    expect(within(good).getByRole("button", { name: "Install…" })).toBeInTheDocument();
    const bad = screen.getByText("too-big").closest("li")!;
    expect(within(bad).getByText(/it is larger than the limit/)).toBeInTheDocument();
    expect(within(bad).queryByRole("button", { name: "Install…" })).not.toBeInTheDocument();
    const odd = screen.getByText("odd").closest("li")!;
    expect(within(odd).queryByText(/Not installable/)).not.toBeInTheDocument();
    expect(within(odd).getByText(/its name differs from its folder/)).toBeInTheDocument();
    const plugin = screen.getByText("elsewhere").closest("li")!;
    expect(within(plugin).getByText(/in another repository, not installable here/)).toBeInTheDocument();
    expect(within(plugin).getByText(/bob\/plugins/)).toBeInTheDocument();
    expect(within(plugin).queryByRole("button", { name: "Install…" })).not.toBeInTheDocument();
  });

  it("installs after showing repository, path, commit, files and the script notice", async () => {
    let body: unknown;
    let previewUrl = "";
    api({}, {
      "GET /api/skills/marketplaces/alice/skills/skill": (r) => ((previewUrl = r.url), json(200, preview)),
      "POST /api/skills/install": async (r) => ((body = await r.json()), json(201, { path: "/Skills/write-well" })),
    });
    renderApp("/skills/marketplaces");

    await userEvent.click(await screen.findByRole("button", { name: "Browse alice/skills" }));
    await userEvent.click(within((await screen.findByText("write-well")).closest("li")!).getByRole("button", { name: "Install…" }));

    const dialog = await screen.findByRole("dialog", { name: "Install write-well" });
    expect(new URL(previewUrl).searchParams.get("commit")).toBe(commit);
    expect(new URL(previewUrl).searchParams.get("path")).toBe("skills/write-well");
    expect(within(dialog).getByText("alice/skills")).toBeInTheDocument();
    expect(within(dialog).getByText("skills/write-well")).toBeInTheDocument();
    expect(within(dialog).getByText(commit.slice(0, 12))).toBeInTheDocument();
    expect(within(dialog).queryByText(commit)).not.toBeInTheDocument();
    expect(within(dialog).getByText("scripts/run.sh")).toBeInTheDocument();
    expect(within(dialog).getByText(/big\.bin/)).toBeInTheDocument();
    expect(within(dialog).getByText(scriptNotice)).toBeInTheDocument();
    // The manifest is text: no raw HTML is made, no image fetched.
    expect(within(dialog).getByText("Short sentences.")).toBeInTheDocument();
    expect(dialog.querySelector("script")).toBeNull();
    expect(within(dialog).queryByText(/alert\(1\)/)).not.toBeInTheDocument();
    expect(dialog.querySelector("img")).toBeNull();
    expect(within(dialog).getByText("diagram")).toBeInTheDocument();

    await userEvent.click(within(dialog).getByRole("button", { name: "Install" }));
    expect(await screen.findByRole("radio", { name: "Skills" })).toBeChecked();
    await userEvent.click(screen.getAllByRole("button", { name: "Install" }).at(-1)!);

    await waitFor(() => expect(body).toEqual({ repo: "alice/skills", commit, path: "skills/write-well", vault: "Skills" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("says when GitHub's limit is reached", async () => {
    api({}, { "GET /api/skills/marketplaces/alice/skills": () => json(429, { error: rateLimited, type: "github-rate-limited" }) });
    renderApp("/skills/marketplaces");

    await userEvent.click(await screen.findByRole("button", { name: "Browse alice/skills" }));

    expect(await screen.findByText(rateLimited)).toBeInTheDocument();
  });

  it("hides the tab when switched off", async () => {
    const fetch = api({ enabled: false });
    renderApp("/skills/installed");

    await screen.findByRole("tablist", { name: "Skills" });
    await waitFor(() => expect(fetch.mock.calls.some(([r]) => new URL((r as Request).url).pathname === "/api/skills/marketplaces")).toBe(true));
    await waitFor(() => expect(tab("Shared")).toBeNull());
    expect(tab("Marketplaces")).toBeNull();
  });

  it("shows the tab when switched on", async () => {
    api();
    renderApp("/skills/installed");

    expect(await screen.findByRole("tab", { name: "Marketplaces" })).toBeInTheDocument();
  });
});
