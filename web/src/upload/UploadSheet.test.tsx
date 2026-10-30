// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MAX_ATTACHMENT_BYTES } from "./planUpload";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { tryReload } from "../update/apply";
import { useUpdate } from "../update/store";
import { shrinkImage } from "./shrinkImage";
import { summaryText } from "./UploadSheet";

// The browser's canvas isn't in jsdom: shrinking is shrinkImage.test.ts's; here it hands back a small WebP.
vi.mock("./shrinkImage", async (original) => ({
  ...(await original<typeof import("./shrinkImage")>()),
  shrinkType: () => "image/webp",
  shrinkImage: vi.fn(async (file: File) => new File(["RIFF0000WEBP"], file.name.replace(/\.\w+$/, ".webp"), { type: "image/webp" })),
}));

afterEach(() => {
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
    folders: [{ name: "projects", path: "/Work/projects", folders: [], notes: [note("/Work/projects/plan.md")] }],
    notes: [note("/Work/inbox.md")],
  },
};

interface Sent {
  url: string;
  onConflict: string;
  paths: string[];
}

/** A PUT of an image or PDF: the URL's path and `?version=`, and the bytes as text. */
interface Put {
  url: string;
  version: string | null;
  body: string;
}

const fileState = (path: string, version: string) => ({ path, mediaType: "image/png", size: 4, width: 1, height: 1, version, writtenAt: "2026-10-25T09:00:00Z", writtenBy: "ada" });

/**
 * The API, with an upload that behaves as the server's: `ask` writes nothing of a request that meets an existing
 * note (and lists the conflicts), `skip` and `replace` write. `existing` are paths as the request names them.
 */
function uploadApi(
  existing: string[],
  options: { fail?: (n: number) => boolean; gate?: Promise<void>; files?: Record<string, string>; refuse?: Record<string, [number, string]> } = {},
) {
  const sent: Sent[] = [];
  const puts: Put[] = [];
  // Images and PDFs there already, by their path in the vault, with their versions.
  const files = { ...options.files };
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2 }]),
    "GET /api/vaults/Work/tree": () => json(200, workTree),
    "GET /api/notes/*": (r) =>
      json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "POST /api/upload/*": async (r) => {
      const body = (await r.json()) as { files: { path: string }[]; onConflict: string };
      const paths = body.files.map((f) => f.path);
      sent.push({ url: new URL(r.url).pathname, onConflict: body.onConflict, paths });
      if (sent.length === 1) await options.gate;
      if (options.fail?.(sent.length)) return json(409, { error: "Nothing was uploaded: /Work/Vault/more/n000.md changed meanwhile; try again." });
      const found = paths.filter((p) => existing.includes(p));
      if (body.onConflict === "ask" && found.length) return json(200, { written: 0, replaced: 0, skipped: [], conflicts: found.map((path) => ({ path, version: "v7" })) });
      const replaced = body.onConflict === "replace" ? found.length : 0;
      const skipped = body.onConflict === "skip" ? found.map((path) => ({ path, reason: "a note exists at this path" })) : [];
      return json(200, { written: paths.length - found.length, replaced, skipped, conflicts: [] });
    },
    "PUT /api/files/*": async (r) => {
      const url = new URL(r.url);
      const path = decodeURIComponent(url.pathname.slice("/api/files/Work/".length));
      const version = url.searchParams.get("version");
      puts.push({ url: url.pathname, version, body: new TextDecoder().decode(await r.arrayBuffer()) });
      const refusal = options.refuse?.[path];
      if (refusal) return json(refusal[0], { error: refusal[1] });
      const current = files[path];
      if (current !== undefined && current !== version) {
        const error = version === null ? `/Work/${path} exists already; to overwrite it, send its version.` : `/Work/${path} has changed since version ${version}.`;
        return json(409, { error, current: fileState(`/Work/${path}`, current) });
      }
      files[path] = `n-${path}`;
      return json(current === undefined ? 201 : 200, fileState(`/Work/${path}`, files[path]));
    },
  });
  return { fetch, sent, puts, files };
}

/** Files as a folder picker gives them: each with its path in the picked folder. */
function folderFiles(paths: string[]) {
  return paths.map((path) => {
    const file = new File([`# ${path}`], path.split("/").at(-1)!);
    Object.defineProperty(file, "webkitRelativePath", { value: path });
    return file;
  });
}

const bulk = Array.from({ length: 200 }, (_, i) => `Vault/bulk/n${String(i).padStart(3, "0")}.md`);
// An Obsidian vault: settings, an image, nested folders; two of its notes are in Kumbu² already.
const vault = [
  ...bulk,
  "Vault/.obsidian/app.json",
  "Vault/.obsidian/workspace.json",
  "Vault/img/pic.heic",
  "Vault/a.md",
  "Vault/notes/b.md",
  "Vault/notes/deep/c.md",
  "Vault/notes/deep/d.markdown",
];
const existing = ["Vault/notes/b.md", "Vault/notes/deep/c.md"];

async function openFromDock() {
  await userEvent.click(await screen.findByRole("button", { name: "New" }));
  await userEvent.click(screen.getByRole("menuitem", { name: "Upload" }));
  return screen.findByRole("dialog", { name: "Upload" });
}

async function pickVault(sheet: HTMLElement, paths = vault) {
  await userEvent.upload(within(sheet).getByLabelText("Choose a folder"), folderFiles(paths));
  await userEvent.click(await within(sheet).findByRole("button", { name: "Upload" }));
}

describe("the upload", () => {
  it("writes no existing note before the user decides, lists what isn't Markdown, and shows each batch", async () => {
    let open!: () => void;
    const { sent } = uploadApi(existing, { gate: new Promise<void>((ok) => (open = ok)) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    renderApp("/notes/Work/inbox.md", queryClient);

    const sheet = await openFromDock();
    expect(within(sheet).getByText("Into Work")).toBeInTheDocument();
    await userEvent.upload(within(sheet).getByLabelText("Choose a folder"), folderFiles(vault));
    expect(await within(sheet).findByText("204 Markdown files to upload, 3 files skipped")).toBeInTheDocument();
    await userEvent.click(within(sheet).getByRole("button", { name: "Upload" }));

    expect(await within(sheet).findByText("Batch 1 of 2")).toBeInTheDocument();
    expect(within(sheet).getByRole("progressbar")).toHaveAttribute("aria-valuenow", "0");
    open();

    // Both batches asked; the second met existing notes and wrote nothing; nothing was replaced.
    const list = await within(sheet).findByRole("group", { name: "2 notes exist already" });
    expect(sent.map((s) => [s.url, s.onConflict, s.paths.length])).toEqual([
      ["/api/upload/Work", "ask", 200],
      ["/api/upload/Work", "ask", 4],
    ]);
    expect(sent[0].paths).toEqual(bulk);
    expect(within(list).getAllByRole("checkbox").map((c) => c.getAttribute("aria-label"))).toEqual(["Replace Vault/notes/b.md", "Replace Vault/notes/deep/c.md"]);

    expect(within(list).getByText("2 new notes wait for this choice")).toBeInTheDocument();
    // A choice is required: Escape doesn't close the sheet and drop the held notes, "Skip all" has the focus.
    expect(within(list).getByRole("button", { name: "Skip all" })).toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(screen.getByRole("dialog", { name: "Upload" })).toBeInTheDocument();
    expect(within(sheet).queryByRole("button", { name: /Cancel|Close/ })).not.toBeInTheDocument();

    await userEvent.click(within(list).getByRole("checkbox", { name: "Replace Vault/notes/b.md" }));
    await userEvent.click(within(sheet).getByRole("button", { name: "Replace 1 selected" }));

    expect(await within(sheet).findByText("202 notes uploaded, 1 replaced, 4 files skipped")).toBeInTheDocument();
    // The new notes of the held batch go with "skip" (an existing note is never replaced unasked), the chosen one with "replace".
    expect(sent.slice(2)).toEqual([
      { url: "/api/upload/Work", onConflict: "skip", paths: ["Vault/a.md", "Vault/notes/deep/d.markdown"] },
      { url: "/api/upload/Work", onConflict: "replace", paths: ["Vault/notes/b.md"] },
    ]);
    await userEvent.click(within(sheet).getByText("4 files skipped"));
    const skipped = within(sheet).getByRole("list", { name: "Skipped files" });
    expect(within(skipped).getAllByRole("listitem").map((li) => li.textContent)).toEqual([
      "Vault/.obsidian/app.jsonObsidian's settings or trash, not a note",
      "Vault/.obsidian/workspace.jsonObsidian's settings or trash, not a note",
      "Vault/img/pic.heicnot a format Kumbu² keeps: notes (.md, .markdown), PDFs (.pdf) and images (.png, .jpg, .jpeg, .gif, .webp)",
      "Vault/notes/deep/c.mda note exists at this path",
    ]);
    for (const key of [["tree", "Work"], ["vaults"], ["search"], ["note"]]) expect(invalidate).toHaveBeenCalledWith({ queryKey: key });

    await userEvent.click(within(sheet).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("dialog", { name: "Upload" })).not.toBeInTheDocument();
  });

  it("holds an update's reload while the upload runs", async () => {
    let open!: () => void;
    uploadApi([], { gate: new Promise<void>((ok) => (open = ok)) });
    renderApp("/notes/Work/inbox.md");
    act(() => useUpdate.setState({ available: { semver: "0.2.0", sha: "a6362ec" } }));
    const reload = vi.fn();
    try {
      const sheet = await openFromDock();
      await pickVault(sheet, bulk.concat("Vault/a.md"));
      expect(await within(sheet).findByText("Batch 1 of 2")).toBeInTheDocument();

      expect(await act(() => tryReload("auto", reload))).toBe(false);
      open();
      await userEvent.click(await within(sheet).findByRole("button", { name: "Done" }));
      expect(await act(() => tryReload("auto", reload))).toBe(true);
    } finally {
      act(() => useUpdate.setState({ available: null }));
      sessionStorage.clear();
    }
  });

  it("replaces all, or skips all, as the user says", async () => {
    const { sent } = uploadApi(existing);
    renderApp("/notes/Work/inbox.md");
    await pickVault(await openFromDock(), vault.slice(200));
    await userEvent.click(await screen.findByRole("button", { name: "Replace all" }));
    expect(await screen.findByText("2 notes uploaded, 2 replaced, 3 files skipped")).toBeInTheDocument();
    expect(sent.slice(1).map((s) => [s.onConflict, s.paths])).toEqual([
      ["skip", ["Vault/a.md", "Vault/notes/deep/d.markdown"]],
      ["replace", ["Vault/notes/b.md", "Vault/notes/deep/c.md"]],
    ]);

    cleanup();
    useEditor.getState().reset();
    const second = uploadApi(existing);
    renderApp("/notes/Work/inbox.md");
    await pickVault(await openFromDock(), vault.slice(200));
    await userEvent.click(await screen.findByRole("button", { name: "Skip all" }));
    expect(await screen.findByText("2 notes uploaded, 0 replaced, 5 files skipped")).toBeInTheDocument();
    expect(second.sent.slice(1).map((s) => [s.onConflict, s.paths])).toEqual([["skip", ["Vault/a.md", "Vault/notes/deep/d.markdown"]]]);
  });

  it("stops at a failed batch, says what was written so far, and retries the rest", async () => {
    let failing = true;
    const { sent } = uploadApi([], { fail: (n) => n === 2 && failing });
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await pickVault(sheet, [...bulk, ...bulk.map((p) => p.replace("bulk", "more")), "Vault/last.md"]);

    expect(await within(sheet).findByRole("alert")).toHaveTextContent("Nothing was uploaded: /Work/Vault/more/n000.md changed meanwhile; try again.");
    expect(within(sheet).getByText("Batch 2 of 3 failed. So far: 200 notes uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(sent).toHaveLength(2);

    failing = false;
    await userEvent.click(within(sheet).getByRole("button", { name: "Retry" }));
    expect(await within(sheet).findByText("401 notes uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(sent.map((s) => s.paths[0])).toEqual(["Vault/bulk/n000.md", "Vault/more/n000.md", "Vault/more/n000.md", "Vault/last.md"]);
  });

  it('uploads into a folder from the drawer\'s "Upload here"', async () => {
    const { sent } = uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Actions for projects" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Upload here" }));

    const sheet = await screen.findByRole("dialog", { name: "Upload" });
    expect(within(sheet).getByText("Into Work / projects")).toBeInTheDocument();
    await userEvent.upload(within(sheet).getByLabelText("Choose files"), [new File(["x"], "one.md"), new File(["y"], "two.MD")]);
    await userEvent.click(await within(sheet).findByRole("button", { name: "Upload" }));
    expect(await within(sheet).findByText("2 notes uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(sent).toEqual([{ url: "/api/upload/Work/projects", onConflict: "ask", paths: ["one.md", "two.MD"] }]);
  });

  it("takes files dropped on the note into the note's folder, and asks before sending them", async () => {
    const { sent } = uploadApi([]);
    renderApp("/notes/Work/projects/plan.md");
    const area = (await screen.findByRole("heading", { name: "plan" })).parentElement!;
    const file = new File(["x"], "dropped.md");
    const items = [{ kind: "file", webkitGetAsEntry: () => ({ isFile: true, isDirectory: false, fullPath: "/dropped.md", file: (ok: (f: File) => void) => ok(file) }) }];
    fireEvent.dragOver(area, { dataTransfer: { types: ["Files"], items } });
    fireEvent.drop(area, { dataTransfer: { types: ["Files"], items } });

    const sheet = await screen.findByRole("dialog", { name: "Upload" });
    expect(within(sheet).getByText("Into Work / projects")).toBeInTheDocument();
    expect(await within(sheet).findByText("1 Markdown file to upload, 0 files skipped")).toBeInTheDocument();
    expect(sent).toEqual([]);
    await userEvent.click(within(sheet).getByRole("button", { name: "Upload" }));
    await waitFor(() => expect(sent).toEqual([{ url: "/api/upload/Work/projects", onConflict: "ask", paths: ["dropped.md"] }]));
  });
});

/** A file whose size is `size` without holding that many bytes. */
function sized(name: string, size: number, content = "x") {
  const file = new File([content], name);
  Object.defineProperty(file, "size", { value: size });
  return file;
}

async function pickFiles(sheet: HTMLElement, files: File[]) {
  await userEvent.upload(within(sheet).getByLabelText("Choose files"), files);
}

describe("images and PDFs", () => {
  it("go with the notes of one pick, one PUT each, and are reported in one summary", async () => {
    const { sent, puts } = uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Actions for projects" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Upload here" }));
    const sheet = await screen.findByRole("dialog", { name: "Upload" });

    // A phone names the file in NFD; it goes up in NFC, as the server stores it.
    await pickFiles(sheet, [new File(["# A"], "a.md"), new File(["\x89PNG"], "Bespre\u0301chung.png"), new File(["%PDF-1.7"], "scan.pdf")]);
    expect(await within(sheet).findByText("1 Markdown file and 2 other files to upload, 0 files skipped")).toBeInTheDocument();
    await userEvent.click(within(sheet).getByRole("button", { name: "Upload" }));

    expect(await within(sheet).findByText("1 note and 2 files uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(sent).toEqual([{ url: "/api/upload/Work/projects", onConflict: "ask", paths: ["a.md"] }]);
    expect(puts).toEqual([
      { url: `/api/files/Work/projects/${encodeURIComponent("Bespr\u00e9chung.png")}`, version: null, body: "\x89PNG" },
      { url: "/api/files/Work/projects/scan.pdf", version: null, body: "%PDF-1.7" },
    ]);
  });

  it("an existing file is asked about with the notes, and replaced at the version the server reported", async () => {
    const { puts } = uploadApi(["b.md"], { files: { "shot.png": "v9", "keep.png": "v3" } });
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await pickFiles(sheet, [new File(["B"], "b.md"), new File(["new"], "shot.png"), new File(["also"], "keep.png"), new File(["fresh"], "fresh.png")]);
    await userEvent.click(await within(sheet).findByRole("button", { name: "Upload" }));

    const list = await within(sheet).findByRole("group", { name: "3 files exist already" });
    expect(within(list).getAllByRole("checkbox").map((c) => c.getAttribute("aria-label"))).toEqual(["Replace b.md", "Replace shot.png", "Replace keep.png"]);
    expect(within(list).getByText(/A replaced file keeps its old version/)).toBeInTheDocument();
    await userEvent.click(within(list).getByRole("checkbox", { name: "Replace b.md" }));
    await userEvent.click(within(list).getByRole("checkbox", { name: "Replace shot.png" }));
    await userEvent.click(within(sheet).getByRole("button", { name: "Replace 2 selected" }));

    expect(await within(sheet).findByText("0 notes and 1 file uploaded, 2 replaced, 1 file skipped")).toBeInTheDocument();
    expect(puts.map((p) => [p.url, p.version, p.body])).toEqual([
      ["/api/files/Work/shot.png", null, "new"],
      ["/api/files/Work/keep.png", null, "also"],
      ["/api/files/Work/fresh.png", null, "fresh"],
      ["/api/files/Work/shot.png", "v9", "new"],
    ]);
    await userEvent.click(within(sheet).getByText("1 file skipped"));
    expect(within(within(sheet).getByRole("list", { name: "Skipped files" })).getByRole("listitem")).toHaveTextContent("keep.pnga file exists at this path");
  });

  it("a file that changed after the question isn't replaced", async () => {
    const { puts, files } = uploadApi([], { files: { "shot.png": "v9" } });
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await pickFiles(sheet, [new File(["new"], "shot.png")]);
    await userEvent.click(await within(sheet).findByRole("button", { name: "Upload" }));
    await within(sheet).findByRole("group", { name: "1 file exists already" });
    files["shot.png"] = "v10";
    await userEvent.click(within(sheet).getByRole("button", { name: "Replace all" }));

    expect(await within(sheet).findByText("0 notes and 0 files uploaded, 0 replaced, 1 file skipped")).toBeInTheDocument();
    expect(puts.map((p) => p.version)).toEqual([null, "v9"]);
    await userEvent.click(within(sheet).getByText("1 file skipped"));
    expect(within(sheet).getByRole("list", { name: "Skipped files" })).toHaveTextContent("shot.pngchanged after you chose to replace it; kept as it is");
  });

  it("a file the server refuses is listed with its sentence, and the rest goes on", async () => {
    const { puts } = uploadApi([], {
      refuse: {
        "fake.png": [400, "fake.png isn't a PNG image (its content doesn't match its name)"],
        // A 409 without a file state: the path is a folder already.
        "shots.png": [409, "/Work/shots.png is a folder."],
      },
    });
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await pickFiles(sheet, [new File(["MZ"], "fake.png"), new File(["x"], "shots.png"), new File(["%PDF-"], "ok.pdf")]);
    await userEvent.click(await within(sheet).findByRole("button", { name: "Upload" }));

    expect(await within(sheet).findByText("0 notes and 1 file uploaded, 0 replaced, 2 files skipped")).toBeInTheDocument();
    expect(puts.map((p) => p.url)).toEqual(["/api/files/Work/fake.png", "/api/files/Work/shots.png", "/api/files/Work/ok.pdf"]);
    await userEvent.click(within(sheet).getByText("2 files skipped"));
    expect(within(within(sheet).getByRole("list", { name: "Skipped files" })).getAllByRole("listitem").map((li) => li.textContent)).toEqual([
      "fake.pngfake.png isn't a PNG image (its content doesn't match its name)",
      "shots.png/Work/shots.png is a folder.",
    ]);
  });

  it("the file input lists concrete types, not image/*", async () => {
    uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    const accept = within(sheet).getByLabelText("Choose files").getAttribute("accept")!;
    expect(accept).toContain(".pdf,.png,.jpg,.jpeg,.gif,.webp,application/pdf,image/png,image/jpeg,image/gif,image/webp");
    expect(accept).toContain(".md");
    expect(accept).not.toContain("*");
  });

  it("a heic file is refused with the formats named", async () => {
    const { puts } = uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    // The file picker's list keeps it out (user-event applies `accept` as browsers do); a folder or a drop doesn't.
    await userEvent.upload(within(sheet).getByLabelText("Choose a folder"), folderFiles(["Photos/IMG_0001.HEIC"]));
    expect(await within(sheet).findByText("Nothing to upload, 1 file skipped")).toBeInTheDocument();
    expect(within(sheet).getByRole("button", { name: "Upload" })).toBeDisabled();
    await userEvent.click(within(sheet).getByText("1 file skipped"));
    expect(within(sheet).getByRole("list", { name: "Skipped files" })).toHaveTextContent(
      "Photos/IMG_0001.HEICnot a format Kumbu² keeps: notes (.md, .markdown), PDFs (.pdf) and images (.png, .jpg, .jpeg, .gif, .webp)",
    );
    expect(puts).toEqual([]);
  });

  it("offers to shrink an image over 15 MB and says when the extension changes", async () => {
    const { puts } = uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await pickFiles(sheet, [sized("huge.png", MAX_ATTACHMENT_BYTES + 1), sized("photo.webp", MAX_ATTACHMENT_BYTES + 1), sized("scan.pdf", MAX_ATTACHMENT_BYTES + 1)]);

    expect(await within(sheet).findByText("Too large for Kumbu² (15 MB). Shrink it so it fits?")).toBeInTheDocument();
    const images = within(sheet).getByRole("list", { name: "Images too large" });
    expect(within(images).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["huge.pngIt is uploaded as huge.webp.", "photo.webp"]);
    await userEvent.click(within(sheet).getByRole("button", { name: "Shrink" }));

    expect(await within(sheet).findByText("2 files to upload, 1 file skipped")).toBeInTheDocument();
    expect(shrinkImage).toHaveBeenCalledTimes(2);
    expect(vi.mocked(shrinkImage).mock.calls.map(([file, max]) => [file.name, max])).toEqual([
      ["huge.png", MAX_ATTACHMENT_BYTES],
      ["photo.webp", MAX_ATTACHMENT_BYTES],
    ]);
    await userEvent.click(within(sheet).getByText("1 file skipped"));
    expect(within(sheet).getByRole("list", { name: "Skipped files" })).toHaveTextContent("scan.pdflarger than 15 MB, the limit for a file");
    await userEvent.click(within(sheet).getByRole("button", { name: "Upload" }));
    expect(await within(sheet).findByText("0 notes and 2 files uploaded, 0 replaced, 1 file skipped")).toBeInTheDocument();
    expect(puts.map((p) => p.url)).toEqual(["/api/files/Work/huge.webp", "/api/files/Work/photo.webp"]);
    vi.mocked(shrinkImage).mockClear();
  });

  it("warns that a GIF loses its animation, and skips the image when told not to shrink", async () => {
    const { puts } = uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await pickFiles(sheet, [sized("party.gif", MAX_ATTACHMENT_BYTES + 1)]);

    const images = await within(sheet).findByRole("list", { name: "Images too large" });
    expect(images).toHaveTextContent("party.gifIt is uploaded as party.webp.An animated GIF loses its animation.");
    await userEvent.click(within(sheet).getByRole("button", { name: "Skip it" }));
    expect(await within(sheet).findByText("Nothing to upload, 1 file skipped")).toBeInTheDocument();
    expect(shrinkImage).not.toHaveBeenCalled();
    expect(puts).toEqual([]);
  });

  it("a pasted image is named after the moment, and the name can be changed before it goes", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(2026, 9, 27, 22, 15, 42));
    try {
      const { puts } = uploadApi([]);
      renderApp("/notes/Work/inbox.md");
      const sheet = await openFromDock();
      fireEvent.paste(sheet, { clipboardData: { files: [new File(["\x89PNG"], "image.png", { type: "image/png" })] } });

      const name = await within(sheet).findByRole("textbox", { name: "Name of the pasted image" });
      expect(name).toHaveValue("pasted 2026-10-27 22-15.png");
      await userEvent.clear(name);
      await userEvent.type(name, "Whiteboard");
      await userEvent.click(within(sheet).getByRole("button", { name: "Upload" }));

      expect(await within(sheet).findByText("0 notes and 1 file uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
      expect(puts).toEqual([{ url: "/api/files/Work/Whiteboard.png", version: null, body: "\x89PNG" }]);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("the open sheet", () => {
  it("keeps a drop anywhere on the page from opening the file in place of the app", async () => {
    uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    await openFromDock();
    const drop = new Event("drop", { bubbles: true, cancelable: true });
    const over = new Event("dragover", { bubbles: true, cancelable: true });
    document.body.dispatchEvent(over);
    document.body.dispatchEvent(drop);
    expect(over.defaultPrevented).toBe(true);
    expect(drop.defaultPrevented).toBe(true);

    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    const later = new Event("drop", { bubbles: true, cancelable: true });
    document.body.dispatchEvent(later);
    expect(later.defaultPrevented).toBe(false);
  });
});

describe("summaryText", () => {
  it("counts in the singular and the plural", () => {
    expect(summaryText({ written: 34, replaced: 3, skipped: 12 })).toBe("34 notes uploaded, 3 replaced, 12 files skipped");
    expect(summaryText({ written: 1, replaced: 1, skipped: 1 })).toBe("1 note uploaded, 1 replaced, 1 file skipped");
    expect(summaryText({ written: 0, replaced: 0, skipped: 0 })).toBe("0 notes uploaded, 0 replaced, 0 files skipped");
  });
});

describe("empty folders", () => {
  it("uploading a folder with an empty subfolder puts its marker", async () => {
    const { puts, sent } = uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await userEvent.upload(within(sheet).getByLabelText("Choose a folder"), folderFiles(["Vault/a.md", "Vault/Drafts/.vaultkeep"]));
    expect(await within(sheet).findByText("1 Markdown file to upload, 0 files skipped")).toBeInTheDocument();
    await userEvent.click(within(sheet).getByRole("button", { name: "Upload" }));

    expect(await within(sheet).findByText("1 note uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(sent).toHaveLength(1);
    expect(puts).toEqual([{ url: "/api/files/Work/Vault/Drafts/.vaultkeep", version: null, body: "" }]);
  });

  it("a refused marker is listed as its folder", async () => {
    uploadApi([], { refuse: { "Vault/Drafts/.vaultkeep": [409, "/Work/Vault/Drafts is a note."] } });
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await userEvent.upload(within(sheet).getByLabelText("Choose a folder"), folderFiles(["Vault/a.md", "Vault/Drafts/.vaultkeep"]));
    await userEvent.click(await within(sheet).findByRole("button", { name: "Upload" }));

    expect(await within(sheet).findByText("1 note uploaded, 0 replaced, 1 file skipped")).toBeInTheDocument();
    await userEvent.click(within(sheet).getByText("1 file skipped"));
    const list = within(sheet).getByRole("list", { name: "Skipped files" });
    expect(list).toHaveTextContent("Vault/Drafts//Work/Vault/Drafts is a note.");
    expect(list).not.toHaveTextContent(".vaultkeep");
  });

  it("a pick of nothing but markers reviews as empty folders", async () => {
    uploadApi([]);
    renderApp("/notes/Work/inbox.md");
    const sheet = await openFromDock();
    await userEvent.upload(within(sheet).getByLabelText("Choose a folder"), folderFiles(["Vault/Drafts/.vaultkeep"]));
    expect(await within(sheet).findByText("1 empty folder to upload, 0 files skipped")).toBeInTheDocument();
    expect(within(sheet).getByRole("button", { name: "Upload" })).toBeEnabled();
  });
});
