// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../../state/editor";
import { useSession } from "../../state/session";
import { applyScheme } from "../../theme/apply";
import { fakeApi, json, renderApp, user } from "../../test/app";

function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }, { name: "Diary", notes: 1 }]),
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/vault-events": () => json(200, []),
    "GET /api/account/storage": () => json(200, { used: 4096, limit: null }),
    "GET /api/account/calendar": () => json(200, { zone: "Europe/Vienna", serverZone: "Europe/Vienna" }),
    ...overrides,
  });
}

function mockSystem(dark: boolean) {
  vi.stubGlobal("matchMedia", (q: string) => ({ matches: dark && q.includes("dark"), addEventListener() {}, removeEventListener() {} }));
}

beforeEach(() => {
  localStorage.clear();
  delete document.documentElement.dataset.scheme;
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("Settings: Appearance", () => {
  it("explains installing on Android, iPhone and the desktop", async () => {
    api();
    renderApp("/settings/appearance");
    const install = await screen.findByRole("region", { name: "Install as an app" });
    expect(install).toHaveAttribute("id", "install");
    const lists = within(install).getAllByRole("list");
    expect(lists).toHaveLength(3);
    expect(install).toHaveTextContent("Android");
    expect(lists[0]).toHaveTextContent("⋮");
    expect(lists[0]).toHaveTextContent("Install app");
    expect(lists[0]).toHaveTextContent("Add to Home screen");
    expect(install).toHaveTextContent("iPhone");
    expect(lists[1]).toHaveTextContent("Share");
    expect(lists[1]).toHaveTextContent("Add to Home Screen");
    expect(install).toHaveTextContent("desktop");
    expect(lists[2]).toHaveTextContent("Install Kumbu²");
  });

  it("says so when it already runs as an installed app", async () => {
    vi.stubGlobal("matchMedia", (q: string) => ({ matches: q === "(display-mode: standalone)", addEventListener() {}, removeEventListener() {} }));
    api();
    renderApp("/settings/appearance");
    const install = await screen.findByRole("region", { name: "Install as an app" });
    expect(install).toHaveTextContent("Kumbu² is installed as an app on this device.");
    expect(within(install).queryAllByRole("list")).toHaveLength(0);
  });

  it("follows the system by default and remembers only the scheme's name", async () => {
    mockSystem(true);
    api();
    renderApp("/settings/appearance");
    const schemes = await screen.findByRole("radiogroup", { name: "Colour scheme" });
    expect(within(schemes).getByLabelText("Follow the system")).toBeChecked();

    await userEvent.click(within(schemes).getByLabelText("Catppuccin Latte"));
    expect(document.documentElement.dataset.scheme).toBe("catppuccin-latte");
    expect({ ...localStorage }).toEqual({ "kumbu.scheme": "catppuccin-latte" });

    await userEvent.click(within(schemes).getByLabelText("Follow the system"));
    expect(document.documentElement.dataset.scheme).toBe("kumbu");
    expect(localStorage.length).toBe(0);
  });

  it("refreshes Read mode by itself only once chosen, kept on this device", async () => {
    mockSystem(true);
    api();
    renderApp("/settings/appearance");
    const box = await within(await screen.findByRole("region", { name: "Changes made elsewhere" })).findByRole("checkbox", { name: "Refresh Read mode automatically" });
    expect(box).not.toBeChecked();

    await userEvent.click(box);
    expect(box).toBeChecked();
    expect({ ...localStorage }).toEqual({ "kumbu.autoRefresh": "on" });

    await userEvent.click(box);
    expect(localStorage.length).toBe(0);
  });

  it("groups the schemes as dark and light, each with a swatch", async () => {
    mockSystem(true);
    api();
    renderApp("/settings/appearance");
    const dark = await screen.findByRole("group", { name: "Dark" });
    const light = screen.getByRole("group", { name: "Light" });
    expect(within(dark).getByLabelText("Tokyo Night")).toBeInTheDocument();
    expect(within(dark).queryByLabelText("Tokyo Night Day")).not.toBeInTheDocument();
    expect(within(light).getByLabelText("Tokyo Night Day")).toBeInTheDocument();
    expect(within(light).getByLabelText("Rosé Pine Dawn")).toBeInTheDocument();
    expect(screen.getByTestId("swatch-nord")).toBeInTheDocument();
  });

  it("shows a remembered choice as chosen", async () => {
    mockSystem(false);
    localStorage.setItem("kumbu.scheme", "kumbu");
    api();
    renderApp("/settings/appearance");
    expect(await screen.findByLabelText("Kumbu")).toBeChecked();
  });

  it("follows a scheme chosen elsewhere while it is open: the palette's", async () => {
    mockSystem(false);
    api();
    renderApp("/settings/appearance");
    expect(await screen.findByLabelText("Follow the system")).toBeChecked();
    act(() => applyScheme("kumbu"));
    expect(screen.getByLabelText("Kumbu")).toBeChecked();
    act(() => applyScheme(null));
    expect(screen.getByLabelText("Follow the system")).toBeChecked();
  });

  it("asks for the password before downloads of several files go without it", async () => {
    const bodies: unknown[] = [];
    api({
      "PUT /api/account/preferences": async (r) => {
        const body = (await r.json()) as { askPasswordForDownloads: boolean; password?: string };
        bodies.push(body);
        if (body.password === "wrong") return json(400, { error: "The password is wrong." });
        return json(200, { askPasswordForDownloads: body.askPasswordForDownloads });
      },
    });
    renderApp("/settings/appearance");
    const downloads = await screen.findByRole("region", { name: "Downloads" });
    const box = within(downloads).getByRole("checkbox", { name: "Ask for my password when downloading several files" });
    await waitFor(() => expect(box).toBeChecked());

    await userEvent.click(box);
    let dialog = await screen.findByRole("alertdialog", { name: "Download several files without the password?" });
    // Nothing changes before the password is given.
    expect(box).toBeChecked();
    expect(bodies).toEqual([]);
    await userEvent.type(within(dialog).getByLabelText("Your password"), "wrong");
    await userEvent.click(within(dialog).getByRole("button", { name: "Turn off" }));
    expect(await within(dialog).findByText("The password is wrong.")).toBeInTheDocument();
    expect(box).toBeChecked();

    // Cancelled: it stays on.
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(box).toBeChecked();

    await userEvent.click(box);
    dialog = await screen.findByRole("alertdialog", { name: "Download several files without the password?" });
    expect(within(dialog).getByLabelText("Your password")).toHaveValue("");
    await userEvent.type(within(dialog).getByLabelText("Your password"), "secret");
    await userEvent.click(within(dialog).getByRole("button", { name: "Turn off" }));
    await waitFor(() => expect(box).not.toBeChecked());
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(bodies.at(-1)).toEqual({ askPasswordForDownloads: false, password: "secret" });

    // Back on needs no password.
    await userEvent.click(box);
    await waitFor(() => expect(box).toBeChecked());
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(bodies.at(-1)).toEqual({ askPasswordForDownloads: true });
  });
});
