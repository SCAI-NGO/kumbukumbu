// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { TreeFolder } from "../api/notes";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { NOT_SUPPORTED } from "../upload/planUpload";
import { fakeApi, json, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown" });
const image = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "image", mediaType: "image/jpeg", version: "v1" });

let drafts: TreeFolder;

function api(vault: Record<string, unknown> = { name: "Work", notes: 2, embeddings: "internal" }) {
  drafts = { name: "Drafts", path: "/Work/Drafts", folders: [], notes: [note("/Work/Drafts/plan.md")] };
  const puts: string[] = [];
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [vault]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [drafts], notes: [note("/Work/inbox.md")] } }),
    "GET /api/notes/*": () => json(200, { path: "/Work/inbox.md", content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/version/*": () => json(200, { path: "/Work/inbox.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "PUT /api/files/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname);
      puts.push(path);
      return json(201, { path: path.slice("/api/files".length), mediaType: "image/jpeg", size: 3, version: "v2", lastWriter: "ada", lastWriteAt: "2026-11-14T09:00:00Z" });
    },
  });
  return { fetch, puts };
}

/** A phone or tablet (a touch screen as the main pointer), or a desktop with a mouse. */
function pointer(coarse: boolean) {
  vi.stubGlobal("matchMedia", (q: string) => ({ matches: q === "(pointer: coarse)" && coarse, addEventListener() {}, removeEventListener() {} }));
  // The install hint a phone gets once is not what these tests are about.
  localStorage.setItem("kumbu.installHint", "dismissed");
}

const drawer = () => screen.getByRole("dialog", { name: "File tree" });

async function menuOf(name: string) {
  renderApp("/notes/Work/inbox.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText("Drafts");
  await userEvent.click(within(drawer()).getByRole("button", { name: `Actions for ${name}` }));
  return screen.getAllByRole("menuitem").map((i) => i.textContent);
}

/** Take photo in the Drafts folder's menu, and the camera giving back `file` after `meanwhile`. */
async function takePhoto(file: File, meanwhile: () => void = () => {}) {
  await menuOf("Drafts");
  const camera = document.querySelector<HTMLInputElement>("input[type=file][capture]")!;
  const click = vi.spyOn(camera, "click");
  await userEvent.click(screen.getByRole("menuitem", { name: "Take photo" }));
  expect(click).toHaveBeenCalled();
  expect(camera).toHaveAttribute("accept", "image/*");
  expect(camera).toHaveAttribute("capture", "environment");
  act(meanwhile);
  await userEvent.upload(camera, file);
}

beforeEach(() => {
  window.innerWidth = 390;
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  useDiscreet.setState({ gone: new Set() });
});

describe("Take photo", () => {
  it("is offered on a touch device for a writer, after Upload here", async () => {
    pointer(true);
    api();
    expect(await menuOf("Drafts")).toEqual(["New note here", "New folder…", "Upload here", "Take photo", "Rename or move"]);
  });

  it("is not offered on a desktop", async () => {
    pointer(false);
    api();
    expect(await menuOf("Drafts")).not.toContain("Take photo");
  });

  it("is not offered to a read-only member", async () => {
    pointer(true);
    api({ name: "Work", notes: 2, embeddings: "internal", canWrite: false, level: "read", owner: '"Ada"', shared: true });
    expect(await menuOf("Work")).not.toContain("Take photo");
  });

  it("lands in that folder through the upload sheet as Photo … .jpg, with the upload's confirmation", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(2026, 10, 14, 9, 30, 5));
    pointer(true);
    const { puts } = api();
    await takePhoto(new File(["jpg"], "IMG_0001.jpg", { type: "image/jpeg" }));
    const upload = await screen.findByRole("dialog", { name: "Upload" });
    expect(await within(upload).findByText("0 notes and 1 file uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(puts).toEqual(["/api/files/Work/Drafts/Photo 2026-11-14 093005.jpg"]);
  });

  it("a name taken in the folder gets 2", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(2026, 10, 14, 9, 30, 5));
    pointer(true);
    const { puts } = api();
    drafts.notes.push(image("/Work/Drafts/Photo 2026-11-14 093005.jpg"));
    await takePhoto(new File(["jpg"], "image.jpg", { type: "image/jpeg" }));
    await waitFor(() => expect(puts).toEqual(["/api/files/Work/Drafts/Photo 2026-11-14 093005 2.jpg"]));
  });

  it("a HEIC photo is refused with the upload's sentence", async () => {
    pointer(true);
    const { puts } = api();
    await takePhoto(new File(["heic"], "IMG_0002.HEIC", { type: "image/heic" }));
    const upload = await screen.findByRole("dialog", { name: "Upload" });
    expect(await within(upload).findByText("IMG_0002.HEIC")).toBeInTheDocument();
    expect(within(upload).getByText(NOT_SUPPORTED)).toBeInTheDocument();
    expect(puts).toEqual([]);
  });
  // The camera can take its time: the vault may be hidden, or another one chosen, before the photo comes back.
  it("a photo that comes back after its vault was hidden is dropped", async () => {
    pointer(true);
    const { puts } = api();
    await takePhoto(new File(["jpg"], "IMG_0003.jpg", { type: "image/jpeg" }), () => useDiscreet.setState({ gone: new Set(["Work"]) }));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByRole("dialog", { name: "Upload" })).not.toBeInTheDocument();
    expect(useEditor.getState().upload).toBeNull();
    expect(puts).toEqual([]);
  });

  it("a photo that comes back after another vault was chosen is dropped", async () => {
    pointer(true);
    const { puts } = api();
    await takePhoto(new File(["jpg"], "IMG_0004.jpg", { type: "image/jpeg" }), () => useEditor.setState({ activeVault: "Home" }));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByRole("dialog", { name: "Upload" })).not.toBeInTheDocument();
    expect(useEditor.getState().upload).toBeNull();
    expect(puts).toEqual([]);
  });
});
