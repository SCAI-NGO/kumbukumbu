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
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults/W/tree": () => json(200, tree),
    "GET /api/files/*": png,
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/backlinks/*": () => json(200, []),
    // Whatever the page the gallery opens asks.
    "GET /*": () => json(404, { error: "Not here" }),
    ...extra,
  });
}

/** The hrefs the app made the browser download, in order. */
function downloads() {
  const hrefs: string[] = [];
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    hrefs.push(this.getAttribute("href") ?? "");
  });
  return hrefs;
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
    const hrefs = downloads();
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });

    // One file needs no password: it comes as it is.
    await longPress("whiteboard.png");
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Download" }));
    expect(hrefs).toEqual(["/api/files/W/Screenshots/whiteboard.png?download=1"]);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "sketch.png" }));
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Download" }));
    const dialog = await screen.findByRole("alertdialog", { name: "Download 2 files?" });
    await userEvent.type(within(dialog).getByLabelText("Your password"), "secret");
    await userEvent.click(within(dialog).getByRole("button", { name: "Download" }));

    await waitFor(() => expect(hrefs).toHaveLength(2));
    const zip = new URL(hrefs[1], "http://x");
    expect(zip.pathname).toBe("/api/files/W/zip");
    expect(zip.searchParams.getAll("path")).toEqual(["Screenshots/whiteboard.png", "Sketches/sketch.png"]);
    expect(zip.searchParams.get("ticket")).toBe("t1");
    expect(zip.searchParams.get("tz")).toBeTruthy();
    expect(ticket).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
  });

  it("with the preference off it downloads without asking", async () => {
    const ticket = vi.fn(() => json(200, { ticket: "t1" }));
    api({ "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: false }), "POST /api/export/ticket": ticket });
    const hrefs = downloads();
    renderApp("/gallery/W");
    await screen.findByRole("button", { name: "whiteboard.png" });
    await longPress("whiteboard.png");
    await userEvent.click(screen.getByRole("button", { name: "banner.png" }));
    await userEvent.click(within(screen.getByRole("region", { name: "Selection" })).getByRole("button", { name: "Download" }));

    await waitFor(() => expect(hrefs).toHaveLength(1));
    const zip = new URL(hrefs[0], "http://x");
    expect(zip.searchParams.getAll("path")).toEqual(["Screenshots/whiteboard.png", "Projects/Teaser/banner.png"]);
    expect(zip.searchParams.has("ticket")).toBe(false);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(ticket).not.toHaveBeenCalled();
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

  it("shows a friendly way to upload when the vault has no images or PDFs", async () => {
    api({ "GET /api/vaults/W/tree": () => json(200, { damaged: 0, root: { name: "W", path: "/W", folders: [], notes: [] } }) });
    const router = renderApp("/gallery/W");
    expect(await screen.findByText("No images or PDFs in W yet.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Upload files" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
  });

  it("loads a tile only near the screen and lets go of it when it leaves; a PDF's tile frees its document once drawn", async () => {
    const observed = new Map<Element, (entries: { isIntersecting: boolean }[]) => void>();
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        constructor(private readonly callback: (entries: { isIntersecting: boolean }[]) => void) {}
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
