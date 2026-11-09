// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

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
const host = window.location.host;
const url = `https://${host}/dav/`;
const clients = ["KDE Dolphin", "GNOME Files", "macOS Finder", "Windows Explorer", "rclone", "Obsidian Remotely Save", "Nextcloud External storage"];

function api(listed: unknown[] = []) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }]),
    "GET /api/dav-shares": () => json(200, { enabled: true, shares: listed }),
    "POST /api/dav-shares": () => json(201, { password, share }),
  });
}

async function createShare(queryClient?: QueryClient) {
  api([share]);
  const router = renderApp("/access/dav", queryClient);
  await userEvent.click(await screen.findByRole("button", { name: "New share" }));
  const dialog = screen.getByRole("dialog", { name: "New WebDAV share" });
  await userEvent.type(within(dialog).getByLabelText("Label"), "Work PC");
  await userEvent.click(within(await within(dialog).findByRole("group", { name: "Work" })).getByLabelText("Read and write"));
  await userEvent.click(within(dialog).getByRole("button", { name: "Create share" }));
  await screen.findByText(password);
  return router;
}

/** Each client's instructions, by the name on its summary. */
const instructions = () =>
  Object.fromEntries(
    within(screen.getByRole("region", { name: "How to connect" }))
      .getAllByRole("group")
      .map((d) => [d.querySelector("summary")!.textContent, d]),
  );

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
});

describe("DavConnect", () => {
  it("shows URL, username and password once with copy buttons", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = await createShare(queryClient);

    // The connect page has the page to itself, as a new key does.
    expect(screen.queryByRole("tablist")).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Connect “Work PC”" })).toBeInTheDocument();
    expect(screen.getByLabelText("URL")).toHaveTextContent(url);
    expect(screen.getByLabelText("Username")).toHaveTextContent("ada.k7qm");
    expect(screen.getByLabelText("Password")).toHaveTextContent(password);
    expect(screen.getByText(/password manager/)).toBeInTheDocument();
    expect(screen.getByText(/shown only now/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Copy URL" }));
    expect(writeText).toHaveBeenLastCalledWith(url);
    await userEvent.click(screen.getByRole("button", { name: "Copy username" }));
    expect(writeText).toHaveBeenLastCalledWith("ada.k7qm");
    await userEvent.click(screen.getByRole("button", { name: "Copy password" }));
    expect(writeText).toHaveBeenLastCalledWith(password);
    // The password is nowhere in the instructions: it has its one place above.
    expect(screen.getByRole("region", { name: "How to connect" })).not.toHaveTextContent(password);

    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByText(password)).not.toBeInTheDocument();
    expect(await screen.findByRole("tablist", { name: "Access" })).toBeInTheDocument();
    // Neither the URL nor storage nor the client-wide caches keep it.
    expect(router.state.location.pathname + router.state.location.search + router.state.location.hash).toBe("/access/dav");
    expect(JSON.stringify({ ...localStorage })).not.toContain(password);
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(password);
    await waitFor(() => expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.data))).not.toContain(password));
    expect(JSON.stringify(queryClient.getQueryCache().getAll().map((q) => q.state.data))).not.toContain(password);
  });

  it("asks once before leaving with the password unsaved", async () => {
    const router = await createShare();
    await act(() => router.navigate("/settings"));
    expect(screen.getByRole("alertdialog")).toHaveTextContent("You haven't saved the password");
    await userEvent.click(screen.getByRole("button", { name: "Stay" }));
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    await userEvent.click(screen.getByRole("button", { name: "Leave without it" }));
    expect(screen.queryByText(password)).not.toBeInTheDocument();
  });

  it("fills the username into every client's instructions", async () => {
    await createShare();
    const each = instructions();
    expect(Object.keys(each)).toEqual(clients);
    for (const name of clients) expect(each[name]).toHaveTextContent("ada.k7qm");

    expect(within(each["KDE Dolphin"]).getByRole("link", { name: `webdavs://ada.k7qm@${host}/dav/` })).toHaveAttribute(
      "href",
      `webdavs://ada.k7qm@${host}/dav/`,
    );
    expect(each["GNOME Files"]).toHaveTextContent("Other Locations");
    expect(each["GNOME Files"]).toHaveTextContent(`davs://ada.k7qm@${host}/dav/`);
    expect(each["macOS Finder"]).toHaveTextContent("Connect to Server (⌘K)");
    expect(each["macOS Finder"]).toHaveTextContent(url);
    expect(each["Windows Explorer"]).toHaveTextContent("Map network drive");
    expect(each["Windows Explorer"]).toHaveTextContent(`net use K: ${url} /user:ada.k7qm *`);
    const rclone = each["rclone"].querySelector("pre")!.textContent;
    expect(rclone).toContain("type = webdav");
    expect(rclone).toContain(`url = ${url}`);
    expect(rclone).toContain("vendor = other");
    expect(rclone).toContain("user = ada.k7qm");
    expect(rclone).toMatch(/^pass = /m);
    expect(each["rclone"]).toHaveTextContent("rclone obscure");
    expect(each["Obsidian Remotely Save"]).toHaveTextContent("WebDAV");
    expect(each["Obsidian Remotely Save"]).toHaveTextContent(url);
    expect(each["Nextcloud External storage"]).toHaveTextContent(host);
    expect(each["Nextcloud External storage"]).toHaveTextContent("/dav/");
    expect(each["Nextcloud External storage"]).toHaveTextContent(/one share for each Nextcloud user/);
    expect(each["Nextcloud External storage"]).toHaveTextContent(/never one admin mount/i);
  });

  it("without the password from the row", async () => {
    // Even right after creating it: the row's page never has the password.
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    await createShare();
    await userEvent.click(screen.getByRole("button", { name: "Copy password" }));
    await userEvent.click(screen.getByRole("button", { name: "Done" }));

    await userEvent.click(await screen.findByRole("button", { name: "How to connect Work PC" }));
    expect(screen.getByRole("heading", { name: "Connect “Work PC”" })).toBeInTheDocument();
    expect(screen.getByLabelText("URL")).toHaveTextContent(url);
    expect(screen.getByLabelText("Username")).toHaveTextContent("ada.k7qm");
    expect(screen.queryByLabelText("Password")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Copy password" })).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(password);
    expect(instructions()["rclone"]).toHaveTextContent("ada.k7qm");

    await userEvent.click(screen.getByRole("button", { name: "Back to the shares" }));
    expect(await screen.findByRole("button", { name: "New share" })).toBeInTheDocument();
  });
});
