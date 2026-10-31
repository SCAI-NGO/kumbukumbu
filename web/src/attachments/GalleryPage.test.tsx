// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";
import { openPdf } from "./pdf";

// pdf.js doesn't run in jsdom: a PDF's tile opens the fake below.
vi.mock("./pdf", async (actual) => ({ ...(await actual<typeof import("./pdf")>()), openPdf: vi.fn() }));

const { createObjectURL, revokeObjectURL } = URL;
const at = (day: number, hour: number) => new Date(2026, 9, day, hour).toISOString();

const file = (path: string, writtenAt: string, size: number) => ({
  name: path.slice(path.lastIndexOf("/") + 1),
  path,
  size,
  kind: path.endsWith(".pdf") ? "pdf" : "image",
  mediaType: path.endsWith(".pdf") ? "application/pdf" : "image/png",
  version: `v-${path.slice(path.lastIndexOf("/") + 1)}`,
  writtenAt,
});

const tree = {
  damaged: 0,
  root: {
    name: "W",
    path: "/W",
    folders: [
      {
        name: "Projects",
        path: "/W/Projects",
        notes: [],
        folders: [
          {
            name: "Teaser",
            path: "/W/Projects/Teaser",
            folders: [],
            notes: [file("/W/Projects/Teaser/banner.png", at(27, 15), 2048), file("/W/Projects/Teaser/chart_q3.pdf", at(27, 9), 4096)],
          },
        ],
      },
      {
        name: "Screenshots",
        path: "/W/Screenshots",
        folders: [],
        notes: [
          file("/W/Screenshots/layout_editor_bad1.png", at(28, 10), 10_240),
          file("/W/Screenshots/layout_editor_bad2.png", at(28, 9), 5120),
          { name: "notes.md", path: "/W/Screenshots/notes.md", size: 12, kind: "note", mediaType: "text/markdown" },
          file("/W/Screenshots/whiteboard.png", at(28, 8), 1024),
        ],
      },
      { name: "Sketches", path: "/W/Sketches", folders: [], notes: [file("/W/Sketches/sketch.png", at(27, 18), 512)] },
    ],
    notes: [{ name: "plan.md", path: "/W/plan.md", size: 40, kind: "note", mediaType: "text/markdown" }],
  },
};

const png = () => new Response(new Uint8Array([0x89, 0x50, 0x4e, 0x47]), { headers: { "Content-Type": "image/png" } });

function api(extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true }),
    "GET /api/vaults/W/tree": () => json(200, tree),
    "GET /api/files/*": png,
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/backlinks/*": () => json(200, []),
    // Whatever the page the gallery opens asks.
    "GET /*": () => json(404, { error: "Not here" }),
    ...extra,
  });
}

/** What the app made the browser download, in order: the links it clicked, and the forms it posted. */
function downloads() {
  const links: string[] = [];
  const forms: { method: string; action: string; target: string; fields: [string, string][] }[] = [];
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    links.push(this.getAttribute("href") ?? "");
  });
  vi.spyOn(HTMLFormElement.prototype, "submit").mockImplementation(function (this: HTMLFormElement) {
    const fields = [...this.querySelectorAll("input")].map((i) => [i.name, i.value] as [string, string]);
    forms.push({ method: this.method, action: this.getAttribute("action") ?? "", target: this.target, fields });
  });
  return { links, forms };
}

const sections = () => screen.getAllByRole("region").filter((r) => r.closest("[data-testid=gallery]"));
const tileNames = (within_: HTMLElement = screen.getByTestId("gallery")) =>
  within(within_)
    .getAllByRole("button")
    .map((b) => b.getAttribute("aria-label"));

async function longPress(name: string) {
  const tile = screen.getByRole("button", { name });
  fireEvent.pointerDown(tile, { pointerId: 1, clientX: 10, clientY: 10, pointerType: "touch", isPrimary: true });
  await act(() => new Promise((resolve) => setTimeout(resolve, 550)));
  fireEvent.pointerUp(tile, { pointerId: 1, clientX: 10, clientY: 10, pointerType: "touch" });
  fireEvent.click(tile);
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date(2026, 9, 28, 12));
  localStorage.clear();
  URL.createObjectURL = vi.fn(() => "blob:file");
  URL.revokeObjectURL = vi.fn();
  vi.mocked(openPdf).mockResolvedValue({
    promise: Promise.resolve({
      getPage: async () => ({
        getViewport: ({ scale }: { scale: number }) => ({ width: 600 * scale, height: 800 * scale }),
        render: () => ({ promise: Promise.resolve(), cancel() {} }),
      }),
    }),
    destroy: async () => {},
  } as never);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  URL.createObjectURL = createObjectURL;
  URL.revokeObjectURL = revokeObjectURL;
});

describe("Gallery", () => {
  it("orders by date newest first grouped by day", async () => {
    api();
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    expect(screen.getByRole("radio", { name: "Date" })).toBeChecked();
    expect(sections().map((s) => s.getAttribute("aria-label"))).toEqual(["Today", "Yesterday"]);
    expect(tileNames(sections()[0])).toEqual(["layout_editor_bad1.png", "layout_editor_bad2.png", "whiteboard.png"]);
    expect(tileNames(sections()[1])).toEqual(["sketch.png", "banner.png", "chart_q3.pdf"]);
    // Notes are no tiles.
    expect(screen.queryByRole("button", { name: /\.md$/ })).not.toBeInTheDocument();
  });

  it("orders by folder A–Z then name", async () => {
    api();
    renderApp("/gallery/W");
    await userEvent.click(await screen.findByRole("radio", { name: "Folder" }));
    expect(sections().map((s) => s.getAttribute("aria-label"))).toEqual(["/Projects/Teaser", "/Screenshots", "/Sketches"]);
    expect(within(sections()[1]).getByText("3")).toBeInTheDocument();
    expect(tileNames(sections()[0])).toEqual(["banner.png", "chart_q3.pdf"]);
    expect(tileNames(sections()[1])).toEqual(["layout_editor_bad1.png", "layout_editor_bad2.png", "whiteboard.png"]);
  });

  it("orders by name across folders", async () => {
    api();
    const router = renderApp("/gallery/W");
    await userEvent.click(await screen.findByRole("radio", { name: "Name" }));
    const order = ["banner.png", "chart_q3.pdf", "layout_editor_bad1.png", "layout_editor_bad2.png", "sketch.png", "whiteboard.png"];
    expect(tileNames()).toEqual(order);

    // A tile opens the lightbox, which pages through the gallery's order.
    await userEvent.click(screen.getByRole("button", { name: "sketch.png" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/view/W/Sketches/sketch.png"));
    expect((router.state.location.state as { paths: string[] }).paths.map((p) => p.slice(p.lastIndexOf("/") + 1))).toEqual(order);
  });

  it("remembers the order", async () => {
    api();
    renderApp("/gallery/W");
    await userEvent.click(await screen.findByRole("radio", { name: "Name" }));
    expect(localStorage.getItem("kumbu.galleryOrder")).toBe("name");
    cleanup();

    renderApp("/gallery/W");
    expect(await screen.findByRole("radio", { name: "Name" })).toBeChecked();

    // Storage that refuses doesn't stop the gallery.
    cleanup();
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    renderApp("/gallery/W");
    await userEvent.click(await screen.findByRole("radio", { name: "Folder" }));
    expect(screen.getByRole("radio", { name: "Folder" })).toBeChecked();
  });

  it("long press selects and the bar shows count and size", async () => {
    api();
    const router = renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("layout_editor_bad1.png");
    const bar = screen.getByRole("region", { name: "Selection" });
    expect(bar).toHaveTextContent("1 selected · 10.0 KB");
    expect(router.state.location.pathname).toBe("/gallery/W");

    // While selecting, a tap adds a tile instead of opening it.
    await userEvent.click(screen.getByRole("button", { name: "layout_editor_bad2.png" }));
    expect(bar).toHaveTextContent("2 selected · 15.0 KB");
    expect(screen.getByRole("button", { name: "layout_editor_bad2.png" })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(screen.getByRole("button", { name: "layout_editor_bad2.png" }));
    expect(bar).toHaveTextContent("1 selected · 10.0 KB");
    expect(router.state.location.pathname).toBe("/gallery/W");

    // Ctrl-click selects on the desktop; Escape ends it all.
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("region", { name: "Selection" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "sketch.png" }), { ctrlKey: true });
    expect(screen.getByRole("region", { name: "Selection" })).toHaveTextContent("1 selected · 512 B");
  });

  it("downloading several asks for the password by default", async () => {
    const ticket = vi.fn(async (r: Request) => {
      expect(await r.json()).toEqual({ password: "secret" });
      return json(200, { ticket: "t1" });
    });
    api({ "POST /api/export/ticket": ticket });
    const { links, forms } = downloads();
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });

    // One file needs no password: it comes as it is.
    await longPress("whiteboard.png");
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Download" }));
    expect(links).toEqual(["/api/files/W/Screenshots/whiteboard.png?download=1"]);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "sketch.png" }));
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Download" }));
    const dialog = await screen.findByRole("alertdialog", { name: "Download 2 files?" });
    await userEvent.type(within(dialog).getByLabelText("Your password"), "secret");
    await userEvent.click(within(dialog).getByRole("button", { name: "Download" }));

    // A form posted, not a link: a hundred paths wouldn't fit in an address.
    await waitFor(() => expect(forms).toHaveLength(1));
    expect(forms[0].method).toBe("post");
    expect(forms[0].action).toBe("/api/files/W/zip");
    // Into the gallery's hidden frame, not the page: an error lands there, not in place of the app.
    const frame = document.querySelector<HTMLIFrameElement>("iframe[title=Downloads]")!;
    expect(forms[0].target).toBe(frame.name);
    expect(frame).not.toBeVisible();
    expect(forms[0].fields.filter(([name]) => name === "path").map(([, value]) => value)).toEqual(["Screenshots/whiteboard.png", "Sketches/sketch.png"]);
    expect(forms[0].fields.find(([name]) => name === "ticket")?.[1]).toBe("t1");
    expect(forms[0].fields.find(([name]) => name === "tz")?.[1]).toBeTruthy();
    expect(links).toHaveLength(1);
    expect(ticket).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    // The form doesn't stay in the page.
    expect(document.querySelector("form[action$='/zip']")).toBeNull();
  });

  it("with the preference off it downloads without asking", async () => {
    const ticket = vi.fn(() => json(200, { ticket: "t1" }));
    let ask = false;
    const preferences = vi.fn(() => json(200, { askPasswordForDownloads: ask }));
    api({ "GET /api/account/preferences": preferences, "POST /api/export/ticket": ticket });
    const { forms } = downloads();
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("whiteboard.png");
    await userEvent.click(screen.getByRole("button", { name: "banner.png" }));
    const bar = () => within(screen.getByRole("region", { name: "Selection" }));
    await userEvent.click(bar().getByRole("button", { name: "Download" }));

    await waitFor(() => expect(forms).toHaveLength(1));
    expect(forms[0].fields.filter(([name]) => name === "path").map(([, value]) => value)).toEqual(["Screenshots/whiteboard.png", "Projects/Teaser/banner.png"]);
    expect(forms[0].fields.some(([name]) => name === "ticket")).toBe(false);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(ticket).not.toHaveBeenCalled();

    // Turned back on elsewhere: the setting is asked afresh for every download, and the password is asked again.
    ask = true;
    await userEvent.click(bar().getByRole("button", { name: "Download" }));
    expect(await screen.findByRole("alertdialog", { name: "Download 2 files?" })).toBeInTheDocument();
    expect(forms).toHaveLength(1);
    expect(preferences).toHaveBeenCalledTimes(2);
  });

  it("asks for the password for a ZIP when the window is closed, though the setting is off", async () => {
    const ticket = vi.fn(async () => json(200, { ticket: "t1" }));
    api({
      "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: false }),
      "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: false }),
      "POST /api/export/ticket": ticket,
    });
    const { forms } = downloads();
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("whiteboard.png");
    await userEvent.click(screen.getByRole("button", { name: "banner.png" }));
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Download" }));

    const dialog = await screen.findByRole("alertdialog", { name: "Download 2 files?" });
    expect(dialog).toHaveTextContent("They come as one ZIP. It's a while since you last gave your password on this device, so it confirms the download.");
    expect(forms).toHaveLength(0);
    await userEvent.type(within(dialog).getByLabelText("Your password"), "secret");
    await userEvent.click(within(dialog).getByRole("button", { name: "Download" }));

    await waitFor(() => expect(forms).toHaveLength(1));
    expect(ticket).toHaveBeenCalledTimes(1);
    expect(forms[0].fields.find(([name]) => name === "ticket")?.[1]).toBe("t1");
  });

  it("downloads a ZIP without asking when the setting is off and the window open", async () => {
    api({ "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: false }) });
    const { forms } = downloads();
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("whiteboard.png");
    await userEvent.click(screen.getByRole("button", { name: "banner.png" }));
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Download" }));

    await waitFor(() => expect(forms).toHaveLength(1));
    expect(forms[0].fields.some(([name]) => name === "ticket")).toBe(false);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("the download button waits for the setting, so a double tap starts one ZIP", async () => {
    let answer: (r: Response) => void = () => {};
    api({ "GET /api/account/preferences": () => new Promise<Response>((resolve) => (answer = resolve)) });
    const { forms } = downloads();
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("whiteboard.png");
    await userEvent.click(screen.getByRole("button", { name: "banner.png" }));
    const button = within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Download" });
    await userEvent.click(button);
    expect(button).toBeDisabled();
    await userEvent.click(button);

    answer(json(200, { askPasswordForDownloads: false }));
    await waitFor(() => expect(button).toBeEnabled());
    expect(forms).toHaveLength(1);
  });

  it("an error answer in the download frame is shown in the gallery, and the frame emptied", async () => {
    api({ "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: false }) });
    const { forms } = downloads();
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("whiteboard.png");
    await userEvent.click(screen.getByRole("button", { name: "banner.png" }));
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Download" }));
    await waitFor(() => expect(forms).toHaveLength(1));

    const frame = document.querySelector<HTMLIFrameElement>("iframe[title=Downloads]")!;
    // An empty frame (reset, or nothing loaded as the ZIP went to disk) is no error.
    fireEvent.load(frame);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();

    frame.contentDocument!.body.textContent = JSON.stringify({ error: "Arbeit has no file Projects/Teaser/banner.png." });
    fireEvent.load(frame);
    expect(within(screen.getByRole("region", { name: "Selection" })).getByRole("alert")).toHaveTextContent("Arbeit has no file Projects/Teaser/banner.png.");
    expect(frame.getAttribute("src")).toBe("about:blank");

    // Something that isn't the API's answer still says the download didn't start.
    frame.contentDocument!.body.textContent = "<h1>Bad gateway</h1>";
    fireEvent.load(frame);
    expect(screen.getByRole("alert")).toHaveTextContent("The download didn't start. Try again.");
  });

  it("delete moves the selection to the trash after confirming", async () => {
    const deleted: string[] = [];
    let trees = 0;
    api({
      "GET /api/vaults/W/tree": () => (trees++, json(200, tree)),
      "DELETE /api/files/*": (r) => {
        const url = new URL(r.url);
        deleted.push(`${decodeURIComponent(url.pathname)}?${url.searchParams}`);
        return noContent();
      },
    });
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("whiteboard.png");
    await userEvent.click(screen.getByRole("button", { name: "chart_q3.pdf" }));
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Delete" }));

    const dialog = await screen.findByRole("dialog", { name: "Delete 2 files?" });
    expect(dialog).toHaveTextContent("They go to the vault's trash.");
    expect(deleted).toEqual([]);
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete" }));

    await waitFor(() =>
      expect(deleted).toEqual([
        "/api/files/W/Screenshots/whiteboard.png?version=v-whiteboard.png",
        "/api/files/W/Projects/Teaser/chart_q3.pdf?version=v-chart_q3.pdf",
      ]),
    );
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(screen.queryByRole("region", { name: "Selection" })).not.toBeInTheDocument();
    expect(trees).toBeGreaterThan(1);
  });

  it("moves the selection into a folder, each keeping its name", async () => {
    const moves: unknown[] = [];
    api({
      "POST /api/move": async (r) => {
        moves.push(await r.json());
        return json(200, { message: "moved" });
      },
    });
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("whiteboard.png");
    await userEvent.click(screen.getByRole("button", { name: "banner.png" }));
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Move" }));
    const dialog = await screen.findByRole("dialog", { name: "Move 2 files" });
    await userEvent.selectOptions(within(dialog).getByLabelText("Folder"), "/W/Sketches");
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));

    expect(await within(dialog).findByRole("status")).toHaveTextContent("Moved 2 files.");
    expect(moves).toEqual([
      { from: "/W/Screenshots/whiteboard.png", to: "/W/Sketches/whiteboard.png", keepLinks: false },
      { from: "/W/Projects/Teaser/banner.png", to: "/W/Sketches/banner.png", keepLinks: false },
    ]);
    expect(screen.queryByRole("region", { name: "Selection" })).not.toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("orders names and folders as the tree does, without numeric order, case after letters", async () => {
    const odd = {
      damaged: 0,
      root: {
        name: "W",
        path: "/W",
        notes: [file("/W/img2.png", at(28, 1), 1), file("/W/img10.png", at(28, 1), 1), file("/W/Apple.png", at(28, 1), 1), file("/W/apple.png", at(28, 1), 1)],
        folders: [
          { name: "a b", path: "/W/a b", folders: [], notes: [file("/W/a b/x.png", at(28, 1), 1)] },
          {
            name: "a",
            path: "/W/a",
            notes: [file("/W/a/y.png", at(28, 1), 1)],
            folders: [{ name: "c", path: "/W/a/c", folders: [], notes: [file("/W/a/c/z.png", at(28, 1), 1)] }],
          },
          { name: "B", path: "/W/B", folders: [], notes: [file("/W/B/Banner.png", at(28, 1), 1)] },
        ],
      },
    };
    api({ "GET /api/vaults/W/tree": () => json(200, odd) });
    renderApp("/gallery/W");
    await userEvent.click(await screen.findByRole("radio", { name: "Name" }));
    expect(tileNames()).toEqual(["apple.png", "Apple.png", "Banner.png", "img10.png", "img2.png", "x.png", "y.png", "z.png"]);
    await userEvent.click(screen.getByRole("radio", { name: "Folder" }));
    expect(sections().map((s) => s.getAttribute("aria-label"))).toEqual(["/", "/a", "/a/c", "/a b", "/B"]);
    expect(tileNames(sections()[0])).toEqual(["apple.png", "Apple.png", "img10.png", "img2.png"]);
  });

  it("a move refused on the way names the file, keeps the rest selected and moves only those on a second try", async () => {
    const moves: string[] = [];
    let refuse = true;
    api({
      "POST /api/move": async (r) => {
        const { from } = (await r.json()) as { from: string };
        if (from.endsWith("banner.png") && refuse) return json(409, { error: "It changed meanwhile." });
        moves.push(from);
        return json(200, { message: "moved" });
      },
    });
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("whiteboard.png");
    await userEvent.click(screen.getByRole("button", { name: "banner.png" }));
    await userEvent.click(screen.getByRole("button", { name: "layout_editor_bad1.png" }));
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Move" }));
    const dialog = await screen.findByRole("dialog", { name: "Move 3 files" });
    await userEvent.selectOptions(within(dialog).getByLabelText("Folder"), "/W/Sketches");
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));

    expect(await within(dialog).findByText(/banner\.png didn't move: It changed meanwhile\./)).toHaveTextContent("Moved 1 of 3.");
    expect(moves).toEqual(["/W/Screenshots/whiteboard.png"]);
    // Only what moved left the selection.
    expect(screen.getByRole("region", { name: "Selection" })).toHaveTextContent("2 selected");
    expect(within(dialog).getByRole("heading")).toHaveTextContent("Move 2 files");

    refuse = false;
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));
    expect(await within(dialog).findByRole("status")).toHaveTextContent("Moved 3 files.");
    expect(moves).toEqual(["/W/Screenshots/whiteboard.png", "/W/Projects/Teaser/banner.png", "/W/Screenshots/layout_editor_bad1.png"]);
  });

  it("shows a friendly way to upload when the vault has no images or PDFs, and uploads in the gallery", async () => {
    const root = { name: "W", path: "/W", folders: [], notes: [] as ReturnType<typeof file>[] };
    const puts: string[] = [];
    api({
      "GET /api/vaults/W/tree": () => json(200, { damaged: 0, root }),
      "PUT /api/files/*": (r) => {
        const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/files".length));
        puts.push(path);
        root.notes.push(file(path, at(28, 11), 4));
        return json(201, { path, version: "v1", size: 4, kind: "image", mediaType: "image/png", writtenAt: at(28, 11) });
      },
    });
    const router = renderApp("/gallery/W");
    expect(await screen.findByText("No images or PDFs in W yet.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Upload files" }));

    // The sheet opens over the gallery, into the vault's top level.
    const sheet = await screen.findByRole("dialog", { name: "Upload" });
    expect(router.state.location.pathname).toBe("/gallery/W");
    expect(within(sheet).getByText("Into W")).toBeInTheDocument();
    await userEvent.upload(within(sheet).getByLabelText("Choose files"), [new File(["\x89PNG"], "shot.png")]);
    await userEvent.click(await within(sheet).findByRole("button", { name: "Upload" }));
    expect(await within(sheet).findByText("0 notes and 1 file uploaded, 0 replaced, 0 files skipped")).toBeInTheDocument();
    expect(puts).toEqual(["/W/shot.png"]);

    // Done leaves the gallery where it was, now with the new file.
    await userEvent.click(within(sheet).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("dialog", { name: "Upload" })).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/gallery/W");
    expect(await screen.findByRole("button", { name: "shot.png" })).toBeInTheDocument();
  });

  it("the header's Upload opens the sheet over the gallery too", async () => {
    api();
    const router = renderApp("/gallery/W");
    await screen.findByRole("button", { name: "banner.png" });
    await userEvent.click(screen.getByRole("button", { name: "Upload" }));
    const sheet = await screen.findByRole("dialog", { name: "Upload" });
    await userEvent.click(within(sheet).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog", { name: "Upload" })).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/gallery/W");
  });

  it("loads a tile only near the screen and lets go of it when it leaves; a PDF's tile frees its document once drawn", async () => {
    const observed = new Map<Element, (entries: { isIntersecting: boolean }[]) => void>();
    const roots = new Set<Element | Document | null>();
    const margins = new Set<string | undefined>();
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        constructor(
          private readonly callback: (entries: { isIntersecting: boolean }[]) => void,
          options: IntersectionObserverInit,
        ) {
          roots.add(options.root ?? null);
          margins.add(options.rootMargin);
        }
        observe(element: Element) {
          observed.set(element, this.callback);
        }
        disconnect() {}
      },
    );
    const destroy = vi.fn(async () => {});
    const render = vi.fn(() => ({ promise: Promise.resolve(), cancel() {} }));
    vi.mocked(openPdf).mockResolvedValue({
      promise: Promise.resolve({ getPage: async () => ({ getViewport: ({ scale }: { scale: number }) => ({ width: 600 * scale, height: 800 * scale }), render }) }),
      destroy,
    } as never);
    const fetch = api();
    renderApp("/gallery/W");
    const tile = (name: string) => screen.getByRole("button", { name });
    const report = (name: string, isIntersecting: boolean) => act(() => observed.get(tile(name))!([{ isIntersecting }]));
    const fetched = () => fetch.mock.calls.map(([r]) => new URL((r as Request).url).pathname).filter((p) => p.startsWith("/api/files/"));
    await screen.findByRole("button", { name: "whiteboard.png" });
    expect(fetched()).toEqual([]);
    // Near means near the gallery's own scrolling list, about a screen of it ahead.
    expect([...roots]).toEqual([screen.getByTestId("gallery")]);
    expect([...margins]).toEqual(["100% 0px"]);

    report("whiteboard.png", true);
    await waitFor(() => expect(tile("whiteboard.png").querySelector("img")).toHaveAttribute("src", "blob:file"));
    expect(fetched()).toEqual(["/api/files/W/Screenshots/whiteboard.png"]);
    report("whiteboard.png", false);
    expect(tile("whiteboard.png").querySelector("img")).toBeNull();
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:file");

    report("chart_q3.pdf", true);
    await waitFor(() => expect(destroy).toHaveBeenCalled());
    expect(render).toHaveBeenCalledTimes(1);
    const canvas = tile("chart_q3.pdf").querySelector("canvas")!;
    expect(canvas.width).toBeGreaterThan(0);
    report("chart_q3.pdf", false);
    expect([canvas.width, canvas.height]).toEqual([0, 0]);
  });
});
