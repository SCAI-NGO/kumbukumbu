// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api, call } from "../api/client";
import { followRename } from "../api/sharing";
import { useDiscreet } from "../discreet/state";
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

const internalProvider = { name: "internal", label: "On this server", description: null, external: false, available: true, reason: null, default: true };
// The admin wrote the description as a sentence: the dialog doesn't add a second full stop.
const tensorx = { name: "tensorx", label: "TensorX", description: "EU, zero data retention.", external: true, available: true, reason: null, default: false };

function signedInApi(extra: Parameters<typeof fakeApi>[0] = {}, who = user) {
  return fakeApi({
    "GET /api/session": () => json(200, { user: who, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Home", notes: 1, embeddings: "internal" }, { name: "Work", notes: 6, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, [internalProvider]),
    "GET /api/vaults/Work/tree": () => json(200, workTree),
    "GET /api/vaults/Home/tree": () => json(200, homeTree),
    // Every note opens, with a line of text and no links.
    "GET /api/notes/*": (r) =>
      json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
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

  it("opens the drawer on a swipe only over the note, closes it only while it is on top, and drops a cancelled or two-finger touch", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    const shell = await screen.findByTestId("shell");
    const swipe = (from: [number, number], to: [number, number], fingers = 1) => {
      fireEvent.touchStart(shell, { touches: Array.from({ length: fingers }, () => ({ clientX: from[0], clientY: from[1] })) });
      fireEvent.touchEnd(shell, { changedTouches: [{ clientX: to[0], clientY: to[1] }] });
    };

    // Two fingers: a pinch, not a swipe. A touch the browser took over: no swipe either.
    swipe([10, 400], [100, 400], 2);
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
    fireEvent.touchStart(shell, { touches: [{ clientX: 10, clientY: 400 }] });
    fireEvent.touchCancel(shell);
    fireEvent.touchEnd(shell, { changedTouches: [{ clientX: 100, clientY: 400 }] });
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();

    // Not over the search sheet.
    await userEvent.click(screen.getByRole("button", { name: /^Search/ }));
    await screen.findByRole("dialog", { name: "Search" });
    swipe([10, 400], [100, 400]);
    expect(screen.getByRole("dialog", { name: "Search" })).toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
    await userEvent.keyboard("{Escape}");

    // Open, then a dialog over it: a swipe left leaves both.
    swipe([10, 400], [100, 400]);
    await userEvent.click(within(drawer()).getByRole("button", { name: "Switch vault: Work" }));
    await userEvent.click(within(screen.getByRole("dialog", { name: "Vaults" })).getByRole("button", { name: "New vault" }));
    swipe([300, 400], [200, 400]);
    expect(screen.getByRole("dialog", { name: "New vault" })).toBeInTheDocument();
    expect(drawer()).toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    swipe([300, 400], [200, 400]);
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
  });

  it("lists folders first, then notes, alphabetically, with each folder's note count", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await waitFor(() => expect(rows()).toEqual(["projects3 items", "zeta1 item", "inbox", "readme"]));

    await userEvent.click(within(drawer()).getByRole("button", { name: /^projects/ }));
    expect(rows()).toEqual(["projects3 items", "deep1 item", "roadmap", "Untitled", "zeta1 item", "inbox", "readme"]);
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
        return json(201, { path: "/Work/projects/Untitled 2.md", content: "# ", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T10:00:00Z" });
      },
    });
    const router = renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for projects" }));
    const trees = () => fetch.mock.calls.filter(([r]) => r.url.endsWith("/api/vaults/Work/tree")).length;
    const before = trees();
    await userEvent.click(screen.getByRole("menuitem", { name: "New note here" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/Untitled%202.md"));
    expect(body).toEqual({ content: "# " });
    expect(useEditor.getState().mode).toBe("write");
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
    // The tree is asked again, so the new note shows up.
    await waitFor(() => expect(trees()).toBe(before + 1));
  });

  it("offers no choice of search by meaning while the server has no external provider", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for Work" }));
    expect(screen.getByRole("menuitem", { name: "Upload here" })).toBeInTheDocument();
    expect(screen.queryByRole("menuitem", { name: "Search by meaning…" })).not.toBeInTheDocument();
  });

  it("lets the owner send a vault to an external provider only after saying what that sends", async () => {
    let body: unknown;
    let vaults = [{ name: "Work", notes: 6, embeddings: "internal" }];
    const fetch = signedInApi({
      "GET /api/vaults": () => json(200, vaults),
      "GET /api/embeddings": () => json(200, [internalProvider, tensorx]),
      "PUT /api/vaults/Work/embeddings": async (r) => {
        body = await r.json();
        vaults = [{ name: "Work", notes: 6, embeddings: "tensorx" }];
        return json(200, vaults[0]);
      },
    });
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for Work" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Search by meaning…" }));

    const dialog = await screen.findByRole("dialog", { name: "Search by meaning in Work" });
    expect(within(dialog).getByRole("radio", { name: /On this server/ })).toBeChecked();
    expect(within(dialog).queryByRole("note")).not.toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Use this server" })).toBeDisabled();

    await userEvent.click(within(dialog).getByRole("radio", { name: /TensorX/ }));
    const warning = within(dialog).getByRole("note");
    expect(warning).toHaveTextContent("The text of every section in Work is sent to TensorX");
    expect(warning).toHaveTextContent("So is the text of every search that includes Work: also a search across all your vaults, whether you make it or an agent with a key for Work does.");
    expect(warning).toHaveTextContent("The notes of your other vaults are not sent; only the searches that include Work are.");
    expect(warning).toHaveTextContent("What TensorX promises: EU, zero data retention.");
    expect(warning.textContent).not.toContain("..");
    expect(warning).toHaveTextContent("Kumbu² can't check or enforce what happens to your text there.");
    expect(fetch.mock.calls.some(([r]) => r.method === "PUT")).toBe(false);
    // A screen reader hears the warning with the button that acts on it.
    expect(within(dialog).getByRole("button", { name: "Send to TensorX" })).toHaveAccessibleDescription(
      expect.stringContaining("The text of every section in Work is sent to TensorX"),
    );

    await userEvent.click(within(dialog).getByRole("button", { name: "Send to TensorX" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Search by meaning in Work" })).not.toBeInTheDocument());
    expect(body).toEqual({ provider: "tensorx" });

    // Opened again, it shows the choice, and going back says what that means.
    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for Work" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Search by meaning…" }));
    const again = await screen.findByRole("dialog", { name: "Search by meaning in Work" });
    await waitFor(() => expect(within(again).getByRole("radio", { name: /TensorX/ })).toBeChecked());
    await userEvent.click(within(again).getByRole("radio", { name: /On this server/ }));
    expect(within(again).getByRole("note")).toHaveTextContent("nothing more is sent to TensorX");
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "Search by meaning in Work" })).not.toBeInTheDocument();
    expect(drawer()).toBeInTheDocument();
  });

  it("marks the provider that is the server's default", async () => {
    signedInApi({
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 6, embeddings: "tensorx" }]),
      "GET /api/embeddings": () => json(200, [{ ...internalProvider, default: false }, { ...tensorx, default: true }]),
    });
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for Work" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Search by meaning…" }));
    const dialog = await screen.findByRole("dialog", { name: "Search by meaning in Work" });

    await waitFor(() => expect(within(dialog).getByRole("radio", { name: /TensorX/ })).toBeChecked());
    expect(within(dialog).getByRole("radio", { name: /default on this server/ })).toHaveAttribute("value", "tensorx");
    expect(dialog).toHaveTextContent("TensorX (now, default on this server)");
  });

  it("tells the owner of a shared vault that its members' notes go too, and that they will be told", async () => {
    signedInApi({
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 6, embeddings: "internal", level: "owner", shared: true, members: 2 }]),
      "GET /api/embeddings": () => json(200, [internalProvider, tensorx]),
    });
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for Work" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Search by meaning…" }));
    const dialog = await screen.findByRole("dialog", { name: "Search by meaning in Work" });
    expect(dialog).not.toHaveTextContent("shared with");

    await userEvent.click(within(dialog).getByRole("radio", { name: /TensorX/ }));
    expect(within(dialog).getByRole("note")).toHaveTextContent("Work is shared with 2 people. Their notes, too, go to TensorX from now on. They will be told.");
    expect(within(dialog).getByRole("button", { name: "Send to TensorX" })).toHaveAccessibleDescription(expect.stringContaining("Work is shared with 2 people."));
  });

  it("says the same when a shared vault goes back to this server", async () => {
    signedInApi({
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 6, embeddings: "tensorx", level: "owner", shared: true, members: 1 }]),
      "GET /api/embeddings": () => json(200, [internalProvider, tensorx]),
    });
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for Work" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Search by meaning…" }));
    const dialog = await screen.findByRole("dialog", { name: "Search by meaning in Work" });
    await waitFor(() => expect(within(dialog).getByRole("radio", { name: /TensorX/ })).toBeChecked());
    await userEvent.click(within(dialog).getByRole("radio", { name: /On this server/ }));
    expect(within(dialog).getByRole("note")).toHaveTextContent("Work is shared with 1 person. Their notes, too, are embedded on this server again. They will be told.");
  });

  it("says nothing of members for a vault shared with nobody", async () => {
    signedInApi({
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 6, embeddings: "internal", level: "owner", shared: false, members: 0 }]),
      "GET /api/embeddings": () => json(200, [internalProvider, tensorx]),
    });
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for Work" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Search by meaning…" }));
    const dialog = await screen.findByRole("dialog", { name: "Search by meaning in Work" });
    await userEvent.click(within(dialog).getByRole("radio", { name: /TensorX/ }));
    expect(within(dialog).getByRole("note")).toHaveTextContent("The text of every section in Work is sent to TensorX");
    expect(within(dialog).getByRole("note")).not.toHaveTextContent("shared with");
  });

  it("shows why a choice failed and keeps the dialog open", async () => {
    signedInApi({
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 6, embeddings: "internal" }]),
      "GET /api/embeddings": () => json(200, [internalProvider, { ...tensorx, available: false, reason: "the embedding provider TensorX answered 503 Service Unavailable" }]),
      "PUT /api/vaults/Work/embeddings": () => json(400, { error: "This server has no embedding provider 'tensorx'." }),
    });
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(await within(drawer()).findByRole("button", { name: "Actions for Work" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Search by meaning…" }));
    const dialog = await screen.findByRole("dialog", { name: "Search by meaning in Work" });

    expect(dialog).toHaveTextContent("Not available right now: the embedding provider TensorX answered 503 Service Unavailable.");
    await userEvent.click(within(dialog).getByRole("radio", { name: /TensorX/ }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Send to TensorX" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("This server has no embedding provider 'tensorx'.");
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
    const switcher = within(drawer()).getByRole("button", { name: "Switch vault: Work" });
    const account = within(drawer()).getByRole("navigation", { name: "Account" });
    expect(tree.compareDocumentPosition(switcher) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(switcher.compareDocumentPosition(account) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    // The popout opens inside the drawer, which stays open behind it and after a vault is chosen.
    await userEvent.click(switcher);
    const popout = screen.getByRole("dialog", { name: "Vaults" });
    expect(within(popout).getByRole("button", { name: "Work" })).toHaveAttribute("aria-current", "true");
    await userEvent.click(within(popout).getByRole("button", { name: "Home" }));
    await waitFor(() => expect(within(drawer()).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["garden"]));
    expect(within(drawer()).getByRole("button", { name: "Switch vault: Home" })).toHaveFocus();
  });

  it("leads from the popout to the overview of all vaults, even with a last note", async () => {
    signedInApi();
    const router = renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(within(drawer()).getByRole("button", { name: "Switch vault: Work" }));
    await userEvent.click(within(screen.getByRole("dialog", { name: "Vaults" })).getByRole("button", { name: "All vaults" }));
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
    expect(useEditor.getState().recentPaths).toContain("/Work/inbox.md");

    // A vault chosen there opens its tree, as from the overview at the start.
    await userEvent.click(within(screen.getByRole("list", { name: "Your vaults" })).getByRole("button", { name: /^Home/ }));
    expect(await screen.findByRole("dialog", { name: "File tree" })).toBeInTheDocument();
    expect(useEditor.getState().activeVault).toBe("Home");
  });

  it("links to access, settings and the console, and to admin only for admins", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    const account = within(await openDrawer()).getByRole("navigation", { name: "Account" });
    expect(within(account).getByRole("link", { name: "Skills" })).toHaveAttribute("href", "/skills/installed");
    expect(within(account).getByRole("link", { name: "Access" })).toHaveAttribute("href", "/access/keys");
    expect(within(account).getByRole("link", { name: "Settings" })).toHaveAttribute("href", "/settings");
    expect(within(account).getByRole("link", { name: "Console" })).toHaveAttribute("href", "/console");
    expect(within(account).queryByRole("link", { name: "Admin" })).not.toBeInTheDocument();
  });

  it.each([
    ["Access", "Access"],
    ["Settings", "Settings"],
    ["Console", "Console"],
    ["Admin", "Users"],
  ])("closes the drawer when %s is followed, so the way back finds it shut", async (link, title) => {
    signedInApi({ "GET /api/admin/*": () => json(200, []) }, { ...user, role: "admin" });
    renderApp("/notes/Work/inbox.md");
    const account = within(await openDrawer()).getByRole("navigation", { name: "Account" });
    await userEvent.click(within(account).getByRole("link", { name: link }));
    // The page's first lazy import: slow on a busy machine.
    expect(await screen.findByRole("heading", { name: title }, { timeout: 5_000 })).toBeInTheDocument();
    expect(useEditor.getState().overlay).toBe("none");

    await userEvent.click(screen.getByRole("link", { name: "Back to the notes" }));
    await screen.findByRole("button", { name: "Open file tree" });
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
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

  it("stays signed in, and says so, when signing out fails", async () => {
    signedInApi({ "DELETE /api/session": () => json(500, { error: "Something went wrong." }) });
    const router = renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
    expect(await within(drawer()).findByRole("alert")).toHaveTextContent("Couldn't sign out. Try again.");
    expect(router.state.location.pathname).toBe("/notes/Work/inbox.md");
    expect(useEditor.getState().recentPaths).toEqual(["/Work/inbox.md"]);
    expect(useSession.getState().user).not.toBeNull();
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
    await waitFor(() => expect(rows()).toEqual(["projects3 items", "zeta1 item", "inbox", "readme"]));
  });

  it("shows a vault being re-encrypted as such, and its tree as busy for a moment", async () => {
    signedInApi({
      "GET /api/vaults": () =>
        json(200, [
          { name: "Home", notes: 0, embeddings: "internal", pending: true },
          { name: "Work", notes: 6, embeddings: "internal", pending: false },
        ]),
      "GET /api/vaults/Home/tree": () => json(503, { error: "This vault is being re-encrypted; try again in a moment." }),
    });
    renderApp("/");
    expect(await screen.findByRole("button", { name: /^Home/ })).toHaveTextContent("Being re-encrypted");
    expect(screen.getByRole("button", { name: /^Work/ })).toHaveTextContent("6 notes");
    await userEvent.click(screen.getByRole("button", { name: /^Home/ }));
    expect(await within(await screen.findByRole("dialog", { name: "File tree" })).findByText("This vault is being re-encrypted; try again in a moment.")).toBeInTheDocument();
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
    expect(within(drawer()).getByRole("button", { name: "Switch vault: Ideas" })).toBeInTheDocument();
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
    ["/access/keys", "Access"],
    ["/settings", "Settings"],
    ["/admin/users", "Users"],
    ["/admin/invitations", "Invitations"],
  ])("has a page at %s, with the way back to the notes", async (path, title) => {
    signedInApi({ "GET /api/admin/*": () => json(200, []) }, { ...user, role: "admin" });
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

    // The resize handle is the last stop in the drawer, after the account row.
    await userEvent.tab({ shift: true });
    expect(within(drawer()).getByRole("separator", { name: "Resize the sidebar" })).toHaveFocus();
    await userEvent.tab({ shift: true });
    expect(within(drawer()).getByRole("button", { name: "Sign out" })).toHaveFocus();
    await userEvent.tab();
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
    const switcher = within(drawer()).getByRole("button", { name: "Switch vault: Work" });
    await userEvent.click(switcher);
    await userEvent.click(within(screen.getByRole("dialog", { name: "Vaults" })).getByRole("button", { name: "New vault" }));
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "New vault" })).not.toBeInTheDocument();
    expect(drawer()).toBeInTheDocument();
    // The popout closed when New vault was chosen: the focus goes back to the button that opened it.
    expect(switcher).toHaveFocus();

    // Escape in the popout closes the popout only.
    await userEvent.click(switcher);
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "Vaults" })).not.toBeInTheDocument();
    expect(drawer()).toBeInTheDocument();
    expect(switcher).toHaveFocus();
  });

  it("copies the note's file path, its link and its Markdown link from the note's menu, and says so", async () => {
    signedInApi();
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    renderApp("/notes/Home/garden.md");
    await screen.findByRole("heading", { name: "garden" });
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    const items = screen.getAllByRole("menuitem").map((i) => i.textContent);
    expect(items.slice(items.indexOf("History") + 1, items.indexOf("History") + 4)).toEqual(["Copy file path", "Copy link", "Copy markdown link"]);

    await userEvent.click(screen.getByRole("menuitem", { name: "Copy link" }));
    expect(writeText).toHaveBeenCalledWith("[[garden]]");
    expect(await screen.findByText("Copied")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Copy markdown link" }));
    expect(writeText).toHaveBeenLastCalledWith("[garden](/garden.md)");
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Copy file path" }));
    expect(writeText).toHaveBeenLastCalledWith("/Home/garden.md");
  });

  it("says so when a copy fails", async () => {
    signedInApi();
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockRejectedValue(new Error("denied")) } });
    renderApp("/notes/Home/garden.md");
    await screen.findByRole("heading", { name: "garden" });
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Copy file path" }));
    expect(await screen.findByText("Couldn't copy. Copy it by hand.")).toBeInTheDocument();
  });

  it("stays in Write mode when the open note's vault is renamed in another tab", async () => {
    // The vault list read again after the rename hangs: until it is in, the new name isn't in the list.
    let reads = 0;
    signedInApi({
      "GET /api/vaults": () =>
        ++reads === 1 ? json(200, [{ name: "Home", notes: 1, embeddings: "internal" }, { name: "Work", notes: 6, embeddings: "internal" }]) : new Promise<Response>(() => {}),
      "GET /api/vaults/Büro/tree": () => json(200, { damaged: 0, root: { ...workTree.root, name: "Büro", path: "/Büro" } }),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/notes/Work/inbox.md", queryClient);
    const write = await screen.findByRole("button", { name: "Write" });
    await waitFor(() => expect(write).toBeEnabled());
    await userEvent.click(write);
    await screen.findByRole("textbox", { name: "Markdown source" });

    act(() => followRename(queryClient, router.navigate, "Work", "Büro"));

    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/B%C3%BCro/inbox.md"));
    expect(screen.getByRole("button", { name: "Write" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("textbox", { name: "Markdown source" })).toBeInTheDocument();
  });
});

describe("the first vault", () => {
  const skills = { name: "Skills", notes: 1, embeddings: "internal", kind: "skills" };
  const welcome = { name: "Welcome", notes: 1, embeddings: "internal" };
  const tree = (name: string) => () => json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: [] } });
  const vaultsApi = (vaults: object[]) =>
    signedInApi({ "GET /api/vaults": () => json(200, vaults), "GET /api/vaults/Welcome/tree": tree("Welcome"), "GET /api/vaults/Skills/tree": tree("Skills") });

  it("is the first vault that isn't the Skills vault, though Skills sorts first", async () => {
    vaultsApi([skills, welcome]);
    renderApp("/");
    await waitFor(() => expect(useEditor.getState().activeVault).toBe("Welcome"));
  });

  it("is the Skills vault when there is no other", async () => {
    vaultsApi([skills]);
    renderApp("/");
    await waitFor(() => expect(useEditor.getState().activeVault).toBe("Skills"));
  });

  it("leaves a vault the user chose", async () => {
    vaultsApi([skills, welcome]);
    useEditor.getState().setActiveVault("Skills");
    renderApp("/");
    await screen.findByRole("button", { name: /^Welcome/ });
    expect(useEditor.getState().activeVault).toBe("Skills");
  });
});

describe("vaults shared with the user", () => {
  const vault = (name: string, extra: Record<string, unknown> = {}) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false, ...extra });
  const sharedVaults = [
    vault("Home"),
    // Her own vault, shared with others: still hers.
    vault("Work", { shared: true, members: 2 }),
    vault("Rezepte", { owner: '"Carol"', level: "read", canWrite: false, shared: true, membership: "m3" }),
    vault("garten", { owner: '"Alice"', level: "write", shared: true, membership: "m1" }),
    vault("Beete", { owner: '"Alice"', level: "maintain", shared: true, membership: "m2" }),
  ];
  const tree = (name: string, notes: string[], folders: { name: string; notes: string[] }[] = []) => ({
    damaged: 0,
    root: {
      name,
      path: `/${name}`,
      folders: folders.map((f) => ({ name: f.name, path: `/${name}/${f.name}`, folders: [], notes: f.notes.map((n) => note(`/${name}/${f.name}/${n}`)) })),
      notes: notes.map((n) => note(`/${name}/${n}`)),
    },
  });
  const sharedApi = () =>
    signedInApi({
      "GET /api/vaults": () => json(200, sharedVaults),
      "GET /api/vaults/Rezepte/tree": () => json(200, tree("Rezepte", ["soup.md"], [{ name: "Sweets", notes: ["cake.md"] }])),
      "GET /api/vaults/garten/tree": () => json(200, tree("garten", ["plan.md"], [{ name: "Board", notes: ["Board.md"] }])),
      "GET /api/vaults/Beete/tree": () => json(200, tree("Beete", ["plan.md"], [{ name: "Board", notes: ["Board.md"] }])),
    });
  const names = (list: HTMLElement) => within(list).getAllByRole("button").map((b) => b.textContent?.replace(/\d+ notes?$|Read only$/, ""));
  const menuItems = async (label: string, at = 0) => {
    await userEvent.click(within(drawer()).getAllByRole("button", { name: label })[at]);
    const items = screen.getAllByRole("menuitem").map((i) => i.textContent);
    await userEvent.keyboard("{Escape}");
    return items;
  };

  it("groups shared vaults under 'Shared with me' by owner", async () => {
    sharedApi();
    renderApp("/");
    expect(names(await screen.findByRole("list", { name: "Your vaults" }))).toEqual(["Home", "Work"]);
    expect(screen.getByRole("heading", { name: "Shared with me" })).toBeInTheDocument();
    expect(names(screen.getByRole("list", { name: 'Shared by "Alice"' }))).toEqual(["garten", "Beete"]);
    expect(names(screen.getByRole("list", { name: 'Shared by "Carol"' }))).toEqual(["Rezepte"]);
    expect(within(screen.getByRole("list", { name: 'Shared by "Carol"' })).getByRole("button")).toHaveTextContent("Read only");

    // The drawer's switcher: her own first, then each owner's.
    await userEvent.click(screen.getByRole("button", { name: /^garten/ }));
    await userEvent.click(within(drawer()).getByRole("button", { name: "Switch vault: garten" }));
    const switcher = screen.getByRole("dialog", { name: "Vaults" });
    expect(within(switcher).getAllByRole("button").map((b) => b.textContent)).toEqual(["Home", "Work", "garten", "Beete", "Rezepte", "Your name for this vault…", "New vault", "All vaults", "More vaults…"]);
    expect(within(switcher).getByRole("list", { name: 'Shared by "Alice"' })).toHaveTextContent("garten");
    await userEvent.keyboard("{Escape}");
    expect(within(drawer()).getByText('Shared by "Alice"', { selector: "p" })).toBeInTheDocument();
  });

  it("keeps two owners of one display name apart by their usernames", async () => {
    signedInApi({
      "GET /api/vaults": () =>
        json(200, [
          vault("Home"),
          vault("garten", { owner: '"Alice"', ownerUsername: "alice", level: "write", shared: true, membership: "m1" }),
          vault("Beete", { owner: '"Alice"', ownerUsername: "mallory", level: "write", shared: true, membership: "m2" }),
        ]),
      "GET /api/vaults/garten/tree": () => json(200, tree("garten", ["plan.md"])),
    });
    renderApp("/");
    expect(names(await screen.findByRole("list", { name: 'Shared by "Alice" (alice)' }))).toEqual(["garten"]);
    expect(names(screen.getByRole("list", { name: 'Shared by "Alice" (mallory)' }))).toEqual(["Beete"]);

    await userEvent.click(screen.getByRole("button", { name: /^garten/ }));
    await userEvent.click(within(drawer()).getByRole("button", { name: "Switch vault: garten" }));
    const switcher = screen.getByRole("dialog", { name: "Vaults" });
    expect(within(switcher).getByRole("list", { name: 'Shared by "Alice" (alice)' })).toHaveTextContent("garten");
    expect(within(switcher).getByRole("list", { name: 'Shared by "Alice" (mallory)' })).toHaveTextContent("Beete");
    await userEvent.keyboard("{Escape}");
    expect(within(drawer()).getByText('Shared by "Alice" (alice)', { selector: "p" })).toBeInTheDocument();
  });

  it("opens a read-only shared vault without write actions", async () => {
    sharedApi();
    renderApp("/notes/Rezepte/soup.md");
    expect(await screen.findByRole("heading", { name: "soup" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeDisabled());
    expect(screen.getByText("You can only read Rezepte.")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    // Copying writes nothing: the three copy items stay.
    expect(screen.getAllByRole("menuitem").map((i) => i.textContent)).toEqual(["Find in note", "History", "Copy file path", "Copy link", "Copy markdown link"]);
    await userEvent.keyboard("{Escape}");
    await userEvent.click(screen.getByRole("button", { name: "New" }));
    for (const item of screen.getAllByRole("menuitem")) expect(item).toBeDisabled();
    await userEvent.keyboard("{Escape}");

    await openDrawer();
    await within(drawer()).findByText("Sweets");
    // Reading, the trash, activity, checkpoints, the gallery, pairing her own device, her side of the sharing, the export and leaving stay; nothing that writes, nor the owner's choices.
    expect(await menuItems("Actions for Rezepte")).toEqual(["Trash", "Activity", "Checkpoints", "Gallery", "Connect Obsidian…", "Shared with you…", "Export as ZIP…", "Leave this vault…"]);
    expect(within(drawer()).queryByRole("button", { name: "Actions for Sweets" })).not.toBeInTheDocument();
    expect(await menuItems("Actions for soup")).toEqual(["Copy file path", "Copy link", "Copy markdown link"]);
  });

  it("shows Board.md read-only below maintain", async () => {
    sharedApi();
    renderApp("/notes/garten/Board/Board.md");
    expect(await screen.findByRole("heading", { name: "Board" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeDisabled());
    // Refused with the server's sentence (collab design §9.3), on the button and above the note.
    const sentence = "Only those who may maintain garten can change its board's columns.";
    expect(screen.getByRole("button", { name: "Write" })).toHaveAttribute("title", sentence);
    expect(screen.getByText(sentence)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    expect(screen.getAllByRole("menuitem").map((i) => i.textContent)).toEqual(["Find in note", "History", "Copy file path", "Copy link", "Copy markdown link"]);
    await userEvent.keyboard("{Escape}");

    await openDrawer();
    await within(drawer()).findByRole("button", { name: "Board" });
    // A note beside it can be changed; the board's definition and its folder can't, and the definition is only copied.
    expect(await menuItems("Actions for plan")).toEqual(["Copy file path", "Copy link", "Copy markdown link", "Rename or move", "Delete note"]);
    expect(within(drawer()).getAllByRole("button", { name: "Actions for Board" })).toHaveLength(2);
    expect(await menuItems("Actions for Board")).toEqual(["New note here", "New folder…", "Upload here"]);
    expect(await menuItems("Actions for Board", 1)).toEqual(["Copy file path", "Copy link", "Copy markdown link"]);
    expect(await menuItems("Actions for garten")).toEqual(
      expect.arrayContaining(["New note here", "New folder…", "Upload here", "Trash", "Board", "Export as ZIP…"]),
    );
  });

  it("refuses Write mode on Board.md for a write member with the structural sentence, by the refused save's code", async () => {
    // The list still says maintain: her role was lowered to write since it was read, and the list read again hangs.
    let reads = 0;
    signedInApi({
      "GET /api/vaults": () => (++reads === 1 ? json(200, sharedVaults) : new Promise<Response>(() => {})),
      "GET /api/vaults/Beete/tree": () => json(200, tree("Beete", ["plan.md"], [{ name: "Board", notes: ["Board.md"] }])),
      "GET /api/version/*": () => json(200, { path: "/Beete/Board/Board.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
      // Whatever the text says, the code is what counts.
      "PUT /api/notes/Beete/Board/Board.md": () => json(403, { error: "Refused.", code: "structural" }),
    });
    renderApp("/notes/Beete/Board/Board.md");
    const write = await screen.findByRole("button", { name: "Write" });
    await waitFor(() => expect(write).toBeEnabled());
    await userEvent.click(write);
    await userEvent.type(await screen.findByRole("textbox", { name: "Markdown source" }), " more");
    fireEvent.blur(screen.getByRole("textbox", { name: "Markdown source" }));

    const sentence = "Only those who may maintain Beete can change its board's columns.";
    await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeDisabled());
    expect(screen.getByRole("button", { name: "Read" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Write" })).toHaveAttribute("title", sentence);
    expect(screen.getByText(sentence)).toBeInTheDocument();
  });

  it("reads the vault list again when the server refuses a write for the level", async () => {
    const fetch = signedInApi({
      "GET /api/vaults": () => json(200, sharedVaults),
      "GET /api/vaults/garten/tree": () => json(200, tree("garten", ["plan.md"])),
      // Her role was lowered meanwhile.
      "PUT /api/notes/garten/plan.md": () => json(403, { error: "you can only read vault garten", code: "read-only" }),
    });
    renderApp("/notes/garten/plan.md");
    await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeEnabled());
    const vaultReads = () => fetch.mock.calls.filter(([r]) => (r as Request).method === "GET" && new URL((r as Request).url).pathname === "/api/vaults").length;
    const before = vaultReads();
    // Any refused write, wherever it came from.
    await expect(call(api.PUT("/api/notes/{vault}/{path}", { params: { path: { vault: "garten", path: "plan.md" } }, body: { content: "x" } }))).rejects.toMatchObject({
      status: 403,
      code: "read-only",
    });
    await waitFor(() => expect(vaultReads()).toBeGreaterThan(before));
  });

  it("offers the owner Share…, which lists who has access", async () => {
    signedInApi({
      "GET /api/vaults": () => json(200, sharedVaults),
      "GET /api/vaults/Work/access": () =>
        json(200, { apiKeys: [], connections: [], members: [{ id: "m9", label: "Bob", role: "write", status: "active", confirm: false, acceptedAt: null, redeemedAt: null, expiresAt: null, credentials: [] }] }),
    });
    renderApp("/notes/Work/inbox.md");
    await openDrawer();
    await within(drawer()).findByText("zeta");
    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for Work" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Share…" }));
    const dialog = await screen.findByRole("dialog", { name: "Who has access to Work" });
    expect(await within(dialog).findByRole("listitem", { name: "Bob" })).toBeInTheDocument();
  });

  it("lets a maintainer change Board.md, and keeps the owner's choices for the owner", async () => {
    signedInApi({
      "GET /api/vaults": () => json(200, sharedVaults),
      "GET /api/vaults/Beete/tree": () => json(200, tree("Beete", ["plan.md"], [{ name: "Board", notes: ["Board.md"] }])),
      "GET /api/embeddings": () => json(200, [internalProvider, tensorx]),
    });
    renderApp("/notes/Beete/Board/Board.md");
    expect(await screen.findByRole("heading", { name: "Board" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeEnabled());
    await openDrawer();
    await within(drawer()).findByRole("button", { name: "Board" });
    // The folder's menu, then the note's.
    expect(await menuItems("Actions for Board")).toContain("Rename or move");
    expect(await menuItems("Actions for Board", 1)).toEqual(["Copy file path", "Copy link", "Copy markdown link", "Rename or move", "Delete note"]);
    const items = await menuItems("Actions for Beete");
    expect(items).toContain("Board");
    // A member may pair her own device; sharing and the embedding provider stay the owner's.
    expect(items).toContain("Connect Obsidian…");
    expect(items).not.toContain("Share…");
    expect(items).not.toContain("Search by meaning…");
  });

  describe("publishing", () => {
    const publishApi = (publishEnabled: boolean, published: string[] = []) =>
      signedInApi({
        "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true, publishEnabled }),
        "GET /api/vaults": () => json(200, sharedVaults),
        "GET /api/vaults/garten/tree": () => json(200, tree("garten", ["plan.md"])),
        // Publish… waits for her marks: one could cover the note.
        "GET /api/vaults/Work/publications": () => json(200, { publications: [], unpublishable: [] }),
        "GET /api/publications/*": (r) => {
          const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/publications".length));
          return published.includes(path)
            ? json(200, { path, snapshotAt: "2026-11-05T14:32:00Z", older: false, changedAt: null, publishedBy: "you", link: "https://kumbu.example.org/s#x" })
            : json(404, { error: `${path.slice(1)} is not published.` });
        },
      });
    const noteMenu = async () => {
      await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
      const items = screen.getAllByRole("menuitem").map((i) => i.textContent);
      await userEvent.keyboard("{Escape}");
      return items;
    };

    it("offers Publish… for her own unpublished note, and opens the dialog", async () => {
      publishApi(true);
      renderApp("/notes/Work/inbox.md");
      expect(await screen.findByRole("heading", { name: "inbox" })).toBeInTheDocument();
      await waitFor(async () => expect(await noteMenu()).toContain("Publish…"));
      await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
      await userEvent.click(screen.getByRole("menuitem", { name: "Publish…" }));
      expect(await screen.findByRole("dialog", { name: "Publish this note" })).toBeInTheDocument();
    });

    it("offers no Publish… on a vault shared with her, even with maintain", async () => {
      publishApi(true);
      renderApp("/notes/Beete/plan.md");
      expect(await screen.findByRole("heading", { name: "plan" })).toBeInTheDocument();
      await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeEnabled());
      expect(await noteMenu()).not.toContain("Publish…");
    });

    it("offers no Publish… for a published note, which has its bar, nor while publishing is off", async () => {
      publishApi(true, ["/Work/inbox.md"]);
      renderApp("/notes/Work/inbox.md");
      expect(await screen.findByText("Published, up to date")).toBeInTheDocument();
      expect(await noteMenu()).not.toContain("Publish…");
      cleanup();

      publishApi(false);
      renderApp("/notes/Work/readme.md");
      expect(await screen.findByRole("heading", { name: "readme" })).toBeInTheDocument();
      await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeEnabled());
      expect(await noteMenu()).not.toContain("Publish…");
    });
  });
});

const frameOf = (box: HTMLElement) => box.querySelector(":scope > [data-discreet-frame]");

describe("discreet mode on a phone", () => {
  afterEach(() => useDiscreet.getState().reset());
  const shownApi = (revealed: boolean) =>
    signedInApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed, until: revealed ? new Date(Date.now() + 9 * 60_000 + 30_000).toISOString() : null } }),
      "POST /api/ws-ticket": () => json(503, { error: "later" }),
    });

  it("frames the shell and shows the pill under the top bar, and in the drawer", async () => {
    shownApi(true);
    renderApp("/notes/Work/inbox.md");
    const shell = await screen.findByTestId("shell");
    const pill = await within(shell).findByRole("group", { name: "Discreet vaults shown" });
    expect(pill).toHaveTextContent("Discreet · 10 min");
    expect(frameOf(shell)).toBeInTheDocument();
    const tree = await openDrawer();
    expect(frameOf(tree)).toBeInTheDocument();
    expect(within(tree).getByRole("group", { name: "Discreet vaults shown" })).toBeInTheDocument();
  });

  it("shows neither while they are hidden", async () => {
    shownApi(false);
    renderApp("/notes/Work/inbox.md");
    const shell = await screen.findByTestId("shell");
    await screen.findByRole("heading", { name: "inbox" });
    expect(screen.queryByRole("group", { name: "Discreet vaults shown" })).not.toBeInTheDocument();
    expect(frameOf(shell)).toBeNull();
    expect(frameOf(await openDrawer())).toBeNull();
  });
});

describe("More vaults… on a phone", () => {
  it("leads to where the PIN is set, leaving neither the dialog nor the drawer open", async () => {
    signedInApi({
      "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
      "GET /api/vault-events": () => json(200, []),
      "GET /api/account/storage": () => json(200, { used: 4096, limit: null }),
    });
    const router = renderApp("/notes/Work/inbox.md");
    const tree = await openDrawer();
    await userEvent.click(await within(tree).findByRole("button", { name: "Switch vault: Work" }));
    await userEvent.click(within(screen.getByRole("dialog", { name: "Vaults" })).getByRole("button", { name: "More vaults…" }));
    const pin = await screen.findByRole("dialog", { name: "More vaults" });
    await userEvent.click(within(pin).getByRole("link", { name: "Settings → Discreet vaults" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/settings"));
    expect(router.state.location.hash).toBe("#discreet-vaults");
    expect(screen.queryByRole("dialog", { name: "More vaults" })).not.toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
    // Settings is outside the shell, which unmounts: the drawer is closed in the store, not only out of sight.
    expect(useEditor.getState().overlay).toBe("none");
  });
});
