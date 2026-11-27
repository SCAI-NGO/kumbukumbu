// SPDX-License-Identifier: AGPL-3.0-or-later
import "fake-indexeddb/auto";
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MirrorCopies, userKey } from "eidolon-core/store/mirror";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { useDiscreet } from "../discreet/state";
import { useOfflineCopy } from "../offline/copy";
import { lockCopy, openCopy, wipeCopy } from "../offline/mode";
import { turnOff } from "../offline/turnOn";
import { browserPorts } from "../offline/ports";
import { sweepRemovedCopies } from "../offline/unlock";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

afterEach(async () => {
  cleanup();
  vi.unstubAllGlobals();
  await lockCopy();
  useOfflineCopy.getState().clear();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  localStorage.clear();
  await (await browserPorts()).storage.write({ removePrefix: [""] });
});

/** The user's hash, as the copy's header holds it: the key the note is kept under without a copy open. */
const hashOf: Record<string, string> = {};
beforeAll(async () => {
  for (const id of ["u1", "u2"]) hashOf[id] = await userKey(id);
});

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });

const workTree = {
  damaged: 0,
  root: {
    name: "Work",
    path: "/Work",
    folders: [{ name: "projects", path: "/Work/projects", folders: [], notes: [note("/Work/projects/roadmap.md")] }],
    notes: [note("/Work/inbox.md"), note("/Work/readme.md")],
  },
};

const homeTree = { damaged: 0, root: { name: "Home", path: "/Home", folders: [], notes: [note("/Home/diary.md")] } };

type Vault = { name: string; notes: number; embeddings: string; discreet?: boolean };
const home: Vault = { name: "Home", notes: 1, embeddings: "internal" };
const work: Vault = { name: "Work", notes: 3, embeddings: "internal" };

function signedInApi(extra: Parameters<typeof fakeApi>[0] = {}, { vaults = [home, work], revealed = false, who = user }: { vaults?: Vault[]; revealed?: boolean; who?: typeof user } = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user: who, previousSignInAt: null, discreet: { revealed, until: null } }),
    "GET /api/vaults": () => json(200, vaults),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, workTree),
    "GET /api/vaults/Home/tree": () => json(200, homeTree),
    "GET /api/notes/*": (r) =>
      json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    ...extra,
  });
}

const keyOf = (id: string) => `kumbu.lastNote.${hashOf[id]}`;
const kept = () => localStorage.getItem(keyOf("u1"));
const keep = (path: string) => localStorage.setItem(keyOf("u1"), path);
const newClient = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });

describe("the last note", () => {
  it("keeps the note under a key that names no user", async () => {
    signedInApi();
    const router = renderApp("/notes/Work/inbox.md");
    expect(await screen.findByRole("heading", { name: "inbox" })).toBeInTheDocument();
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
    // And the next one, after the first read.
    await act(() => router.navigate("/notes/Work/readme.md"));
    await waitFor(() => expect(kept()).toBe("/Work/readme.md"));
    expect(Object.keys(localStorage).filter((k) => k.startsWith("kumbu.lastNote."))).toEqual([keyOf("u1")]);
    for (const k of Object.keys(localStorage)) {
      expect(k).not.toContain(user.id);
      expect(localStorage.getItem(k)).not.toContain(user.id);
    }
  });

  it("an entry kept under the user's id moves to its new place on its first read, and opens there", async () => {
    localStorage.setItem(`kumbu.lastNote.${user.id}`, "/Work/projects/roadmap.md");
    signedInApi();
    const router = renderApp("/");
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/roadmap.md"));
    expect(localStorage.getItem(`kumbu.lastNote.${user.id}`)).toBeNull();
    expect(kept()).toBe("/Work/projects/roadmap.md");
  });

  it("opens it on start with no note in the address, the tree at it", async () => {
    keep("/Work/projects/roadmap.md");
    signedInApi();
    const router = renderApp("/");
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/roadmap.md"));
    expect(await screen.findByRole("heading", { name: "roadmap" })).toBeInTheDocument();
    // The vault list is never shown on the way.
    expect(screen.queryByRole("heading", { name: "Your vaults" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Open file tree" }));
    const tree = screen.getByRole("dialog", { name: "File tree" });
    expect(await within(tree).findByRole("button", { name: "roadmap" })).toHaveAttribute("aria-current", "page");
  });

  it("shows no vault list while the kept note's tree is on its way", async () => {
    keep("/Work/inbox.md");
    let answer: (r: Response) => void = () => {};
    signedInApi({ "GET /api/vaults/Work/tree": () => new Promise<Response>((resolve) => (answer = resolve)) });
    const router = renderApp("/");
    await waitFor(() => expect(useEditor.getState().activeVault).toBe("Work"));
    expect(screen.queryByRole("heading", { name: "Your vaults" })).not.toBeInTheDocument();
    await act(async () => answer(json(200, workTree)));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/inbox.md"));
  });

  it("keeps a note only once it has loaded: one that isn't there leaves the last one kept", async () => {
    signedInApi({ "GET /api/notes/Work/nope.md": () => json(404, { error: "There is no note at /Work/nope.md." }) });
    const router = renderApp("/notes/Work/inbox.md");
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
    await act(() => router.navigate("/notes/Work/nope.md"));
    expect(await screen.findByText("There is no note at /Work/nope.md.")).toBeInTheDocument();
    expect(kept()).toBe("/Work/inbox.md");
  });

  it("a vault chosen before the shell shows (Share's Open) wins over it", async () => {
    keep("/Work/inbox.md");
    signedInApi();
    useEditor.getState().setActiveVault("Home");
    const router = renderApp("/");
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");
    expect(useEditor.getState().activeVault).toBe("Home");
    expect(kept()).toBe("/Work/inbox.md");
  });

  it("opens only on the app's start: a later / in the same page load stays the vaults", async () => {
    signedInApi();
    renderApp("/", newClient());
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    // Another tab keeps a note meanwhile; the shell shows again, from nothing chosen.
    keep("/Work/inbox.md");
    cleanup();
    useEditor.getState().reset();
    const router = renderApp("/", newClient());
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");
  });

  it("a note that is gone opens its vault's root, and is forgotten", async () => {
    keep("/Work/gone.md");
    signedInApi();
    const router = renderApp("/");
    await waitFor(() => expect(kept()).toBeNull());
    expect(router.state.location.pathname).toBe("/");
    expect(useEditor.getState().activeVault).toBe("Work");
  });

  it("a vault that is gone starts as today, and is forgotten", async () => {
    keep("/Gone/x.md");
    signedInApi();
    const router = renderApp("/");
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    await waitFor(() => expect(kept()).toBeNull());
    expect(router.state.location.pathname).toBe("/");
    expect(useEditor.getState().activeVault).toBe("Home");
  });

  it("a note in a discreet vault is never kept", async () => {
    signedInApi({}, { vaults: [{ ...home, discreet: true }, work], revealed: true });
    const router = renderApp("/notes/Work/inbox.md");
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
    await waitFor(() => expect(useDiscreet.getState().names).toEqual(new Set(["Home"])));
    await act(() => router.navigate("/notes/Home/diary.md"));
    expect(await screen.findByRole("heading", { name: "diary" })).toBeInTheDocument();
    expect(kept()).toBe("/Work/inbox.md");
    for (let i = 0; i < localStorage.length; i++) expect(localStorage.getItem(localStorage.key(i)!)).not.toContain("Home");
  });

  it.each([
    ["marked discreet", [home, { ...work, discreet: true }], true],
    ["left out of the list", [home], false],
  ])("the kept note goes once its vault is %s, here or elsewhere", async (_, vaults, revealed) => {
    keep("/Work/inbox.md");
    signedInApi({}, { vaults, revealed });
    const router = renderApp("/");
    await waitFor(() => expect(kept()).toBeNull());
    expect(router.state.location.pathname).toBe("/");
  });

  it("signing out forgets it", async () => {
    signedInApi({ "DELETE /api/session": () => noContent() });
    const router = renderApp("/notes/Work/inbox.md", newClient());
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(kept()).toBeNull();
  });

  it("a session that ends keeps it", async () => {
    signedInApi();
    const router = renderApp("/notes/Work/inbox.md", newClient());
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
    act(() => useSession.getState().end());
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(kept()).toBe("/Work/inbox.md");
  });

  it("another user never sees it", async () => {
    signedInApi({}, { who: { ...user, id: "u2" } });
    renderApp("/notes/Work/inbox.md", newClient());
    await waitFor(() => expect(localStorage.getItem(keyOf("u2"))).toBe("/Work/inbox.md"));
    cleanup();
    vi.unstubAllGlobals();
    useSession.getState().reset();
    useEditor.getState().reset();

    signedInApi();
    const router = renderApp("/", newClient());
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");
  });

  describe("with the user's offline copy on the device", () => {
    const slotKey = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
    // jsdom has no Web Locks: the copy's lock is taken at once, as in a single tab.
    beforeEach(() => Object.defineProperty(navigator, "locks", { value: { request: (_: string, fn: () => unknown) => fn() }, configurable: true }));
    afterEach(() => void Reflect.deleteProperty(navigator, "locks"));

    /** The app's server, the slot's calls (fetch with a URL) included. */
    function withSlot(answers: Parameters<typeof fakeApi>[0] = {}) {
      const answer = signedInApi({ "POST /api/account/mirror-slots/contact": () => json(200, { id: "s1", key: slotKey, vaults: [] }), ...answers });
      vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) =>
        answer(input instanceof Request ? input : new Request(new URL(String(input), "http://localhost"), init)));
    }

    /** Ada's copy, as turning offline on leaves it, locked. */
    async function adasCopy() {
      withSlot({ "POST /api/account/mirror-slots": () => json(201, { id: "s1", token: "tok-ada", key: slotKey, vaults: [] }) });
      const copies = new MirrorCopies(await browserPorts());
      const mirror = await copies.create({ userId: user.id, username: "ada", password: "pw", label: "Test", idleMs: 60_000 });
      mirror.lock();
      return { copies, header: mirror.header };
    }

    it("with the user's copy open, the note is kept in the copy and nothing of it in local storage", async () => {
      const { copies, header } = await adasCopy();
      withSlot();
      const router = renderApp("/notes/Work/inbox.md", newClient());
      expect(await screen.findByRole("heading", { name: "inbox" })).toBeInTheDocument();
      await waitFor(async () => expect(await useOfflineCopy.getState().mirror?.lastNote()).toBe("/Work/inbox.md"));
      // And the next one, after the first read.
      await act(() => router.navigate("/notes/Work/readme.md"));
      await waitFor(async () => expect(await useOfflineCopy.getState().mirror?.lastNote()).toBe("/Work/readme.md"));
      for (const k of Object.keys(localStorage)) expect(`${k}=${localStorage.getItem(k)}`).not.toMatch(/inbox|readme/);
      const again = (await copies.unlockOffline(header, "pw"))!;
      expect(await again.lastNote()).toBe("/Work/readme.md");
      again.lock();
    });

    it("opens the note kept in the copy on start", async () => {
      const { copies, header } = await adasCopy();
      const opened = (await copies.unlockOffline(header, "pw"))!;
      await opened.keepLastNote("/Work/projects/roadmap.md");
      opened.lock();
      withSlot();
      const router = renderApp("/", newClient());
      await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/roadmap.md"));
      expect(screen.queryByRole("heading", { name: "Your vaults" })).not.toBeInTheDocument();
    });

    it("a note kept in local storage moves into the copy once it opens, and opens there", async () => {
      await adasCopy();
      keep("/Work/projects/roadmap.md");
      withSlot();
      const router = renderApp("/", newClient());
      await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/roadmap.md"));
      expect(await useOfflineCopy.getState().mirror?.lastNote()).toBe("/Work/projects/roadmap.md");
      expect(kept()).toBeNull();
    });

    it("with the copy's opening failing, the vaults show", async () => {
      await adasCopy();
      withSlot({ "POST /api/account/mirror-slots/contact": () => json(503, { error: "Down." }) });
      const router = renderApp("/", newClient());
      expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
      expect(router.state.location.pathname).toBe("/");
    });

    it("signing out forgets the note kept in the copy", async () => {
      const { copies, header } = await adasCopy();
      withSlot({ "DELETE /api/session": () => noContent() });
      const router = renderApp("/notes/Work/inbox.md", newClient());
      await waitFor(async () => expect(await useOfflineCopy.getState().mirror?.lastNote()).toBe("/Work/inbox.md"));
      await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
      await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
      await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
      const again = (await copies.unlockOffline(header, "pw"))!;
      expect(await again.lastNote()).toBeNull();
      again.lock();
    });

    it("removing the copy forgets it", async () => {
      const { header } = await adasCopy();
      keep("/Work/inbox.md");
      useOfflineCopy.getState().set(header, null);
      await wipeCopy(header.slot.token);
      expect(kept()).toBeNull();
    });

    it("turning offline off forgets it", async () => {
      const { header } = await adasCopy();
      keep("/Work/inbox.md");
      withSlot({ "DELETE /api/account/mirror-slots/s1": () => noContent() });
      await turnOff(header);
      expect(kept()).toBeNull();
    });

    it("a copy found gone as it opens forgets it", async () => {
      await adasCopy();
      keep("/Work/inbox.md");
      withSlot({ "POST /api/account/mirror-slots/contact": () => json(404, { error: "Gone.", code: "mirror-wipe" }) });
      useSession.getState().signedIn({ user, previousSignInAt: null });
      await openCopy(user.id);
      expect(kept()).toBeNull();
    });

    it("the sign-in page's sweep forgets the note of a copy it removes, and leaves an entry it can't match", async () => {
      await adasCopy();
      keep("/Work/inbox.md");
      localStorage.setItem(keyOf("u2"), "/Home/diary.md");
      vi.stubGlobal("fetch", () => Promise.resolve(json(401, { error: "Gone.", code: "mirror-wipe" })));
      await sweepRemovedCopies();
      expect(kept()).toBeNull();
      expect(localStorage.getItem(keyOf("u2"))).toBe("/Home/diary.md");
    });
  });
});
