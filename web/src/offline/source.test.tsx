// SPDX-License-Identifier: AGPL-3.0-or-later
// Offline, the app's reads answer from the copy (offline mode design §3, work package 5): the same hooks, the same
// shapes, no request to the server.
import "fake-indexeddb/auto";
import { IDBFactory } from "fake-indexeddb";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MirrorCopies, type SealedMirror } from "eidolon-core/store/mirror";
import type { MirrorSlotsPort, StorePorts } from "eidolon-core/store/ports";
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import { fetchFile, useLinks, useNote, useTree } from "../api/notes";
import { openRecordStorage } from "./idbStorage";
import { isNotOnDevice, nameVaults, notOnDevice, OfflineSource, setOfflineSource } from "./source";

const vaults = [{ id: "v1", tag: "t1", canWrite: true, pending: false }];

async function copy(): Promise<SealedMirror> {
  const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
  const slots: MirrorSlotsPort = {
    create: () => Promise.resolve({ id: "s1", token: "tok", key, vaults }),
    contact: () => Promise.resolve({ id: "s1", key, vaults }),
  };
  const ports: StorePorts = {
    storage: await openRecordStorage("test", new IDBFactory()),
    crypto: { subtle: crypto.subtle, randomBytes: (n) => crypto.getRandomValues(new Uint8Array(n)) },
    slots,
    locks: { run: (_name, fn) => fn(), hold: () => ({ acquired: Promise.resolve(), release() {} }) },
    channel: () => ({ post() {}, listen: () => () => {} }),
    clock: { now: () => Date.now(), setTimeout: (fn, ms) => setTimeout(fn, ms), clearTimeout: (h) => clearTimeout(h as number) },
  };
  return new MirrorCopies(ports).create({ userId: "u1", username: "ada", password: "pw", label: "Test", idleMs: 60_000, iterations: 1000 });
}

let mirror: SealedMirror;
const fetched = vi.fn();

beforeEach(async () => {
  mirror = await copy();
  const vault = await mirror.adopt("v1");
  await nameVaults(mirror, [{ id: "v1", name: "Work" }]);
  const files = vault.files();
  await files.writeIf("projects/Plan.md", null, "# Plan\nSee [[Notes]].\n");
  await files.writeIf("projects/Notes.md", null, "Notes");
  await files.writeBinaryIf("shot.png", null, new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]).buffer);
  setOfflineSource(new OfflineSource(mirror));
  fetched.mockReset();
  vi.stubGlobal("fetch", fetched);
});

afterEach(() => {
  setOfflineSource(null);
  vi.unstubAllGlobals();
});

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

test("the tree, a note and its links come from the copy, never from the server", async () => {
  const tree = renderHook(() => useTree("Work"), { wrapper });
  await waitFor(() => expect(tree.result.current.data).toBeDefined());
  expect(tree.result.current.data!.root.folders[0]!.notes.map((n) => n.path)).toEqual(["/Work/projects/Notes.md", "/Work/projects/Plan.md"]);

  const read = renderHook(() => {
    const note = useNote("/Work/projects/Plan.md");
    return { note, links: useLinks("/Work/projects/Plan.md", note.data?.version) };
  }, { wrapper });
  await waitFor(() => expect(read.result.current.links.data).toBeDefined());
  expect(read.result.current.note.data!.content).toBe("# Plan\nSee [[Notes]].\n");
  expect(read.result.current.links.data).toEqual([{ line: 2, raw: "[[Notes]]", target: "Notes", kind: "note", path: "/Work/projects/Notes.md", candidates: [] }]);
  expect(fetched).not.toHaveBeenCalled();
});

test("a vault not kept here and a missing note are a 404, as the server answers", async () => {
  const tree = renderHook(() => useTree("Archive"), { wrapper });
  await waitFor(() => expect(tree.result.current.error).toMatchObject({ status: 404, message: "Archive is not kept on this device." }));
  const note = renderHook(() => useNote("/Work/gone.md"), { wrapper });
  await waitFor(() => expect(note.result.current.error).toMatchObject({ status: 404 }));
});

test("a note listed whose text hasn't come says so, and isn't a 404", async () => {
  const vault = mirror.vault("v1")!;
  await vault.change(["Later.md"], (path) => Promise.resolve({ entry: { path, remote: { version: "abcdef012345", kind: "note" } } }));
  const note = renderHook(() => useNote("/Work/Later.md"), { wrapper });
  await waitFor(() => expect(note.result.current.isError).toBe(true));
  expect(isNotOnDevice(note.result.current.error)).toBe(true);
  expect((note.result.current.error as Error).message).toBe("Not on this device yet. It comes when you're back online.");
});

test("an attachment's bytes come from the copy; one not fetched yet opens when online", async () => {
  const { data, response } = await fetchFile("Work", "shot.png", {}, "blob", new AbortController().signal);
  expect(response.ok).toBe(true);
  expect((data as Blob).type).toBe("image/png");
  expect((data as Blob).size).toBe(8);

  const vault = mirror.vault("v1")!;
  await vault.change(["big.pdf"], (path) => Promise.resolve({ entry: { path, remote: { version: "abcdef012345", kind: "pdf", size: 5_000_000 } } }));
  expect((await fetchFile("Work", "big.pdf", {}, "arrayBuffer", new AbortController().signal)).response.status).toBe(404);
  expect(notOnDevice("/Work/big.pdf")).toBe(true);
  expect(notOnDevice("/Work/shot.png")).toBe(false);
  // An earlier version needs a connection.
  expect((await fetchFile("Work", "shot.png", { version: "0123456789ab" }, "blob", new AbortController().signal)).response.status).toBe(404);
  expect(fetched).not.toHaveBeenCalled();
});

test("vault names follow the vault list", async () => {
  await nameVaults(mirror, [{ id: "v1", name: "Job" }]);
  const tree = renderHook(() => useTree("Job"), { wrapper });
  await waitFor(() => expect(tree.result.current.data?.root.path).toBe("/Job"));
});
