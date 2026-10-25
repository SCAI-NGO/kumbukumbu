// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

afterEach(() => {
  // Unmounted first: the stores reset below would otherwise update a mounted shell outside act().
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });

const workTree = {
  damaged: 0,
  root: {
    name: "Work",
    path: "/Work",
    folders: [
      {
        name: "projects",
        path: "/Work/projects",
        folders: [{ name: "deep", path: "/Work/projects/deep", folders: [], notes: [note("/Work/projects/deep/plan.md")] }],
        notes: [note("/Work/projects/roadmap.md"), note("/Work/projects/Untitled.md")],
      },
      { name: "zeta", path: "/Work/zeta", folders: [], notes: [note("/Work/zeta/a.md")] },
    ],
    notes: [note("/Work/inbox.md"), note("/Work/readme.md")],
  },
};

const homeTree = { damaged: 0, root: { name: "Home", path: "/Home", folders: [], notes: [note("/Home/garden.md")] } };

function signedInApi(extra: Parameters<typeof fakeApi>[0] = {}, who = user) {
  return fakeApi({
    "GET /api/session": () => json(200, { user: who, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Home", notes: 1 }, { name: "Work", notes: 6 }]),
    "GET /api/vaults/Work/tree": () => json(200, workTree),
    "GET /api/vaults/Home/tree": () => json(200, homeTree),
    ...extra,
  });
}

const drawer = () => screen.getByRole("dialog", { name: "File tree" });
const rows = () => within(drawer()).getAllByRole("listitem").map((li) => li.textContent);

async function openDrawer() {
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  return drawer();
}

describe("the shell", () => {
  it("shows the note's place and title, and switches between Read and Write", async () => {
    signedInApi();
    renderApp("/notes/Work/projects/deep/plan.md");
    expect(await screen.findByRole("heading", { name: "plan" })).toBeInTheDocument();
    expect(screen.getByText("Work / projects / deep")).toBeInTheDocument();

    const write = screen.getByRole("button", { name: "Write" });
    expect(screen.getByRole("button", { name: "Read" })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(write);
    expect(write).toHaveAttribute("aria-pressed", "true");
    expect(useEditor.getState().mode).toBe("write");
  });

  it("reads the note's path from its encoded URL segments", async () => {
    signedInApi();
    renderApp("/notes/My%20Vault/a%20b/c%2525%3F.md");
    expect(await screen.findByRole("heading", { name: "c%25?" })).toBeInTheDocument();
    expect(useEditor.getState().currentPath).toBe("/My Vault/a b/c%25?.md");
  });

  it("opens the drawer on an edge swipe, closes it on a swipe left, and ignores vertical scrolling", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    const shell = await screen.findByTestId("shell");
    const touch = (type: "touchStart" | "touchEnd", x: number, y: number) =>
      fireEvent[type](shell, { [type === "touchStart" ? "touches" : "changedTouches"]: [{ clientX: x, clientY: y }] });

    touch("touchStart", 10, 400);
    touch("touchEnd", 20, 700);
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();

    touch("touchStart", 10, 400);
    touch("touchEnd", 100, 410);
    expect(drawer()).toBeInTheDocument();

    touch("touchStart", 300, 400);
    touch("touchEnd", 200, 400);
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
  });

  it("lists folders first, then notes, alphabetically, with each folder's note count", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await waitFor(() => expect(rows()).toEqual(["projects3 notes", "zeta1 note", "inbox", "readme"]));

    await userEvent.click(within(drawer()).getByRole("button", { name: /^projects/ }));
    expect(rows()).toEqual(["projects3 notes", "deep1 note", "roadmap", "Untitled", "zeta1 note", "inbox", "readme"]);
  });

  it("expands the folder chain of the open note and marks it", async () => {
    signedInApi();
    renderApp("/notes/Work/projects/deep/plan.md");
    await openDrawer();
    const current = await within(drawer()).findByRole("button", { name: "plan" });
    expect(current).toHaveAttribute("aria-current", "page");
    expect(within(drawer()).getByRole("button", { name: /^projects/ })).toHaveAttribute("aria-expanded", "true");
    expect(within(drawer()).getByRole("button", { name: /^deep/ })).toHaveAttribute("aria-expanded", "true");
    expect(within(drawer()).getByRole("button", { name: /^zeta/ })).toHaveAttribute("aria-expanded", "false");
  });

  it("opens a note from the drawer and closes it", async () => {
    signedInApi();
    const router = renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "readme" }));
    expect(router.state.location.pathname).toBe("/notes/Work/readme.md");
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "readme" })).toBeInTheDocument();
  });

  it('creates "Untitled 2" with "New note here" when "Untitled" exists, and opens it in Write mode', async () => {
    let body: unknown;
    const fetch = signedInApi({
      "PUT /api/notes/Work/projects/Untitled%202.md": async (request) => {
        body = await request.json();
        return json(201, { path: "/Work/projects/Untitled 2.md", content: "", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T10:00:00Z" });
      },
    });
    const router = renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for projects" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "New note here" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/Untitled%202.md"));
    expect(body).toEqual({ content: "" });
    expect(useEditor.getState().mode).toBe("write");
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
    // The tree is asked again, so the new note shows up.
    await waitFor(() => expect(fetch.mock.calls.filter(([r]) => r.url.endsWith("/api/vaults/Work/tree"))).toHaveLength(2));
  });

  it('creates "Untitled" from the dock in the open note\'s folder', async () => {
    signedInApi({
      "PUT /api/notes/Work/zeta/Untitled.md": () =>
        json(201, { path: "/Work/zeta/Untitled.md", content: "", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T10:00:00Z" }),
    });
    const router = renderApp("/notes/Work/zeta/a.md");
    await screen.findByRole("heading", { name: "a" });
    await userEvent.click(screen.getByRole("button", { name: "New" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "New note" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/zeta/Untitled.md"));
  });

  it("puts the vault switcher and the account row at the bottom of the drawer, and switches vaults", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    const tree = await within(drawer()).findByRole("list");
    const switcher = within(drawer()).getByRole("group", { name: "Switch vault" });
    const account = within(drawer()).getByRole("navigation", { name: "Account" });
    expect(tree.compareDocumentPosition(switcher) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(switcher.compareDocumentPosition(account) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    expect(within(switcher).getByRole("button", { name: "Work" })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(within(switcher).getByRole("button", { name: "Home" }));
    await waitFor(() => expect(rows()).toEqual(["garden"]));
    expect(within(switcher).getByRole("button", { name: "Home" })).toHaveAttribute("aria-pressed", "true");
  });

  it("links to keys and settings, and to admin only for admins", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    const account = within(await openDrawer()).getByRole("navigation", { name: "Account" });
    expect(within(account).getByRole("link", { name: "Keys" })).toHaveAttribute("href", "/keys");
    expect(within(account).getByRole("link", { name: "Settings" })).toHaveAttribute("href", "/settings");
    expect(within(account).queryByRole("link", { name: "Admin" })).not.toBeInTheDocument();
  });

  it.each(["admin", "primary_admin"])("shows the admin link to a %s", async (role) => {
    signedInApi({}, { ...user, role });
    renderApp("/notes/Work/inbox.md");
    const account = within(await openDrawer()).getByRole("navigation", { name: "Account" });
    expect(within(account).getByRole("link", { name: "Admin" })).toHaveAttribute("href", "/admin/users");
  });

  it("signs out and forgets the last note", async () => {
    const fetch = signedInApi({ "DELETE /api/session": () => noContent() });
    const router = renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(fetch.mock.calls.some(([r]) => r.method === "DELETE")).toBe(true);
    expect(useEditor.getState().recentPaths).toEqual([]);
    expect(useSession.getState().user).toBeNull();
  });

  it("opens the last note at /", async () => {
    signedInApi();
    useEditor.getState().openNote("/Work/projects/roadmap.md");
    const router = renderApp("/");
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/roadmap.md"));
  });

  it("shows the vaults at / when no note was opened yet, and opens one in the drawer", async () => {
    signedInApi();
    renderApp("/");
    await userEvent.click(await screen.findByRole("button", { name: /^Work/ }));
    expect(useEditor.getState().activeVault).toBe("Work");
    await waitFor(() => expect(rows()).toEqual(["projects3 notes", "zeta1 note", "inbox", "readme"]));
  });

  it('asks an empty account to "Create your first vault", and creates it', async () => {
    let vaults: { name: string; notes: number }[] = [];
    let body: unknown;
    signedInApi({
      "GET /api/vaults": () => json(200, vaults),
      "POST /api/vaults": async (request) => {
        body = await request.json();
        vaults = [{ name: "Ideas", notes: 0 }];
        return json(201, { name: "Ideas", notes: 0 });
      },
      "GET /api/vaults/Ideas/tree": () => json(200, { damaged: 0, root: { name: "Ideas", path: "/Ideas", folders: [], notes: [] } }),
    });
    renderApp("/");
    await userEvent.click(await screen.findByRole("button", { name: "Create your first vault" }));
    const dialog = screen.getByRole("dialog", { name: "New vault" });
    await userEvent.type(within(dialog).getByLabelText("Name"), "Ideas");
    await userEvent.click(within(dialog).getByRole("button", { name: "Create vault" }));

    expect(await screen.findByRole("dialog", { name: "File tree" })).toBeInTheDocument();
    expect(body).toEqual({ name: "Ideas" });
    expect(useEditor.getState().activeVault).toBe("Ideas");
    expect(within(drawer()).getByRole("button", { name: "Ideas" })).toHaveAttribute("aria-pressed", "true");
  });

  it("shows the server's sentence when a vault can't be created", async () => {
    signedInApi({
      "GET /api/vaults": () => json(200, []),
      "POST /api/vaults": () => json(409, { error: "A vault named Ideas exists already." }),
    });
    renderApp("/");
    await userEvent.click(await screen.findByRole("button", { name: "Create your first vault" }));
    const dialog = screen.getByRole("dialog", { name: "New vault" });
    await userEvent.type(within(dialog).getByLabelText("Name"), "Ideas");
    await userEvent.click(within(dialog).getByRole("button", { name: "Create vault" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("A vault named Ideas exists already.");
  });

  it.each([
    ["/keys", "API keys"],
    ["/settings", "Settings"],
    ["/admin/users", "Users"],
    ["/admin/invitations", "Invitations"],
  ])("has a page at %s, with the way back to the notes", async (path, title) => {
    signedInApi();
    renderApp(path);
    expect(await screen.findByRole("heading", { name: title })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Back to the notes" })).toHaveAttribute("href", "/");
  });

  it("moves focus into the drawer, keeps it there, and gives it back on Escape", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    const opener = await screen.findByRole("button", { name: "Open file tree" });
    await userEvent.click(opener);
    expect(within(drawer()).getByRole("heading", { name: "Work" })).toHaveFocus();
    await within(drawer()).findByRole("button", { name: "inbox" });

    await userEvent.tab({ shift: true });
    expect(within(drawer()).getByRole("button", { name: "Sign out" })).toHaveFocus();
    await userEvent.tab();
    expect(within(drawer()).getByRole("button", { name: "Actions for Work" })).toHaveFocus();

    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
    expect(opener).toHaveFocus();
  });

  it("closes only a folder menu on Escape, not the drawer around it", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for projects" }));
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("menu")).not.toBeInTheDocument();
    expect(drawer()).toBeInTheDocument();
  });

  it("moves focus to the vault name, and closes the dialog on Escape with focus back on its opener", async () => {
    signedInApi({ "GET /api/vaults": () => json(200, []) });
    renderApp("/");
    const opener = await screen.findByRole("button", { name: "Create your first vault" });
    await userEvent.click(opener);
    expect(within(screen.getByRole("dialog", { name: "New vault" })).getByLabelText("Name")).toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "New vault" })).not.toBeInTheDocument();
    expect(opener).toHaveFocus();
  });

  it("closes only the topmost: Escape in the New vault dialog leaves the drawer open", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    const chip = within(drawer()).getByRole("button", { name: "New vault" });
    await userEvent.click(chip);
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "New vault" })).not.toBeInTheDocument();
    expect(drawer()).toBeInTheDocument();
    expect(chip).toHaveFocus();
  });
});
