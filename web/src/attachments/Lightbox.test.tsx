// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { timeText } from "../components/Sheet";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";
import { openPdf } from "./pdf";

// pdf.js itself doesn't run in jsdom: a PDF opens as the fake below; its links are read as the app reads them.
vi.mock("./pdf", async (actual) => ({ ...(await actual<typeof import("./pdf")>()), openPdf: vi.fn() }));

const { createObjectURL, revokeObjectURL } = URL;

beforeEach(() => {
  URL.createObjectURL = vi.fn(() => "blob:file");
  URL.revokeObjectURL = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  URL.createObjectURL = createObjectURL;
  URL.revokeObjectURL = revokeObjectURL;
});

const png = () => new Response(new Uint8Array([0x89, 0x50, 0x4e, 0x47]), { headers: { "Content-Type": "image/png" } });

const fileInfo = (path: string, extra: Record<string, unknown> = {}) => ({
  path,
  version: "v1",
  lastWriter: "ada",
  lastWriteAt: "2026-10-27T18:42:00Z",
  lastWriterKind: "person",
  kind: path.endsWith(".pdf") ? "pdf" : "image",
  mediaType: path.endsWith(".pdf") ? "application/pdf" : "image/png",
  size: 421_888,
  ...(path.endsWith(".pdf") ? {} : { width: 1440, height: 3120 }),
  ...extra,
});

const entry = (path: string) => ({ name: path.slice(path.lastIndexOf("/") + 1), path, size: 10, kind: path.endsWith(".md") ? "note" : "image", mediaType: "image/png" });

const tree = {
  damaged: 0,
  root: {
    name: "W",
    path: "/W",
    folders: [{ name: "shots", path: "/W/shots", folders: [], notes: ["/W/shots/a.png", "/W/shots/notes.md", "/W/shots/b.png"].map(entry) }],
    notes: [],
  },
};

function api(extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/files/*": png,
    "GET /api/version/*": (r) => json(200, fileInfo(decodeURIComponent(new URL(r.url).pathname.slice("/api/version".length)))),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/vaults/W/tree": () => json(200, tree),
    // Whatever the page that the lightbox goes back to asks.
    "GET /*": () => json(404, { error: "Not here" }),
    ...extra,
  });
}

const order = ["/W/shots/a.png", "/W/shots/b.png", "/W/shots/c.png"];
const opened = (path: string, paths = order) => ["/settings", { pathname: `/view${path}`, state: { paths } }];

describe("Lightbox", () => {
  it("arrow keys move through the given order and Escape closes", async () => {
    api();
    const router = renderApp(opened("/W/shots/b.png"));
    expect(await screen.findByRole("heading", { name: "b.png" })).toBeInTheDocument();
    expect(screen.getByText("File 2 of 3")).toBeInTheDocument();

    fireEvent.keyDown(document.body, { key: "ArrowRight" });
    expect(await screen.findByRole("heading", { name: "c.png" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/view/W/shots/c.png");
    // The last one has no next.
    fireEvent.keyDown(document.body, { key: "ArrowRight" });
    expect(router.state.location.pathname).toBe("/view/W/shots/c.png");

    fireEvent.keyDown(document.body, { key: "ArrowLeft" });
    fireEvent.keyDown(document.body, { key: "ArrowLeft" });
    await waitFor(() => expect(router.state.location.pathname).toBe("/view/W/shots/a.png"));

    // Paging replaced the entry: Escape goes back to where the lightbox was opened.
    fireEvent.keyDown(document.body, { key: "Escape" });
    await waitFor(() => expect(router.state.location.pathname).toBe("/settings"));
  });

  it("pages through the folder's attachments when opened without an order", async () => {
    api();
    const router = renderApp("/view/W/shots/a.png");
    expect(await screen.findByText("File 1 of 2")).toBeInTheDocument();
    fireEvent.keyDown(document.body, { key: "ArrowRight" });
    await waitFor(() => expect(router.state.location.pathname).toBe("/view/W/shots/b.png"));
    // Opened from outside the app, closing goes to the vaults.
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
  });

  it("the info panel shows folder, dimensions, size, date and writer", async () => {
    api();
    renderApp(opened("/W/shots/b.png"));
    await userEvent.click(await screen.findByRole("button", { name: "Info" }));
    const info = screen.getByLabelText("Info", { selector: "dl" });
    await waitFor(() => expect(within(info).getByText("1440 × 3120")).toBeInTheDocument());
    const rows = [...info.querySelectorAll("dt")].map((dt) => [dt.textContent, dt.nextElementSibling?.textContent]);
    expect(rows).toEqual([
      ["Folder", "W / shots"],
      ["Dimensions", "1440 × 3120"],
      ["Size", "412.0 KB"],
      ["Date", timeText("2026-10-27T18:42:00Z")],
      ["Writer", "ada"],
    ]);
  });

  it("copy link puts ![[name.png]] on the clipboard", async () => {
    api();
    const writeText = vi.fn(async () => {});
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    renderApp(opened("/W/shots/b.png"));
    await userEvent.click(await screen.findByRole("button", { name: "Copy link" }));
    expect(writeText).toHaveBeenCalledWith("![[b.png]]");
    expect(await screen.findByRole("status")).toHaveTextContent("Link copied");
  });

  it("copy link for a PDF links it without embedding it", async () => {
    api();
    vi.mocked(openPdf).mockReturnValue(new Promise(() => {}));
    const writeText = vi.fn(async () => {});
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    renderApp(opened("/W/report.pdf", ["/W/report.pdf"]));
    await userEvent.click(await screen.findByRole("button", { name: "Copy link" }));
    expect(writeText).toHaveBeenCalledWith("[[report.pdf]]");
  });

  it("a double tap zooms to 200 % and another back", async () => {
    api();
    renderApp(opened("/W/shots/b.png"));
    await screen.findByRole("img", { name: "b.png" });
    const stage = screen.getByTestId("image-stage");
    const tap = (at: number) => {
      fireEvent.pointerDown(stage, { pointerId: 1, pointerType: "touch", clientX: 100, clientY: 100, timeStamp: at });
      fireEvent.pointerUp(stage, { pointerId: 1, pointerType: "touch", clientX: 100, clientY: 100, timeStamp: at + 50 });
    };
    tap(1000);
    tap(1150);
    expect(await screen.findByText("200 %")).toBeInTheDocument();
    tap(3000);
    tap(3150);
    await waitFor(() => expect(screen.queryByText("200 %")).not.toBeInTheDocument());
  });

  it("deletes to the trash after asking, at the version shown, and shows the next", async () => {
    const fetch = api({ "DELETE /api/files/W/shots/b.png": () => noContent() });
    const router = renderApp(opened("/W/shots/b.png"));
    await userEvent.click(await screen.findByRole("button", { name: "Delete" }));
    const dialog = await screen.findByRole("dialog", { name: "Delete b.png?" });
    expect(within(dialog).getByText("It goes to the vault's trash.")).toBeInTheDocument();
    await waitFor(() => expect(within(dialog).getByText("No note links here.")).toBeInTheDocument());
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/view/W/shots/c.png"));
    const removed = fetch.mock.calls.map(([r]) => r as Request).find((r) => r.method === "DELETE")!;
    expect(new URL(removed.url).search).toBe("?version=v1");
    // The deleted one is out of the order.
    expect(await screen.findByText("File 2 of 2")).toBeInTheDocument();
  });

  it("moves an image keeping its extension, and shows it at its new place", async () => {
    const fetch = api({ "POST /api/move": () => json(200, { message: "moved /W/shots/b.png to /W/shots/bug.png", path: "/W/shots/bug.png", version: "v1" }) });
    const router = renderApp(opened("/W/shots/b.png"));
    await userEvent.click(await screen.findByRole("button", { name: "Rename or move" }));
    const dialog = await screen.findByRole("dialog");
    const name = within(dialog).getByLabelText("Name");
    expect(name).toHaveValue("b");
    expect(within(dialog).getByText(".png")).toBeInTheDocument();
    await userEvent.clear(name);
    await userEvent.type(name, "bug");
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/view/W/shots/bug.png"));
    const move = fetch.mock.calls.map(([r]) => r as Request).find((r) => r.method === "POST")!;
    expect(await move.clone().json()).toEqual({ from: "/W/shots/b.png", to: "/W/shots/bug.png", keepLinks: false });
    expect(await within(dialog).findByRole("heading", { name: "Image moved" })).toBeInTheDocument();
  });

  it("focuses Close as it opens", async () => {
    api();
    renderApp(opened("/W/shots/b.png"));
    await waitFor(() => expect(screen.getByRole("button", { name: "Close" })).toHaveFocus());
  });

  it("offers Delete only once it knows the version shown", async () => {
    let answer: (r: Response) => void = () => {};
    api({ "GET /api/version/*": () => new Promise<Response>((resolve) => (answer = resolve)) });
    renderApp(opened("/W/shots/b.png"));
    const remove = await screen.findByRole("button", { name: "Delete" });
    expect(remove).toBeDisabled();
    answer(json(200, fileInfo("/W/shots/b.png")));
    await waitFor(() => expect(remove).toBeEnabled());
  });

  it("says pdf.js can't be loaded when its chunk doesn't come, not that the PDF is damaged", async () => {
    api();
    vi.mocked(openPdf).mockRejectedValue(new TypeError("Failed to fetch dynamically imported module"));
    renderApp(opened("/W/report.pdf", ["/W/report.pdf"]));
    expect(await screen.findByRole("alert")).toHaveTextContent("can't be loaded");
  });

  it("draws only the pages near the screen, empties those that leave, and redraws only near ones on a zoom", async () => {
    api();
    // The pages report coming near and leaving as the test says.
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
    const renders: { page: number; cancel: ReturnType<typeof vi.fn> }[] = [];
    const page = (number: number) => ({
      getViewport: ({ scale }: { scale: number }) => ({ width: 600 * scale, height: 800 * scale, convertToViewportPoint: () => [0, 0] }),
      getAnnotations: async () => [],
      render: () => {
        const cancel = vi.fn();
        renders.push({ page: number, cancel });
        return { promise: new Promise(() => {}), cancel }; // still drawing
      },
    });
    const doc = { numPages: 3, getPage: async (n: number) => page(n), getDestination: async () => null, getPageIndex: async () => 0 };
    vi.mocked(openPdf).mockResolvedValue({ promise: Promise.resolve(doc), destroy: async () => {} } as never);
    renderApp(opened("/W/report.pdf", ["/W/report.pdf"]));

    await screen.findByText("Page 1 / 3");
    const box = (n: number) => document.querySelector(`[data-page="${n}"]`)!;
    const canvas = (n: number) => box(n).querySelector("canvas")!;
    const report = (n: number, isIntersecting: boolean) => act(() => observed.get(box(n))!([{ isIntersecting }]));
    expect(renders).toEqual([]);

    report(1, true);
    report(2, true);
    await waitFor(() => expect(renders.map((r) => r.page)).toEqual([1, 2]));
    expect(canvas(1).width).toBe(600);

    report(1, false);
    expect(renders[0].cancel).toHaveBeenCalled();
    expect([canvas(1).width, canvas(1).height]).toEqual([0, 0]);

    fireEvent.wheel(screen.getByTestId("pdf-pages"), { ctrlKey: true, deltaY: -100 });
    await waitFor(() => expect(renders.map((r) => r.page)).toEqual([1, 2, 2]));
    expect(renders[1].cancel).toHaveBeenCalled();
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(renders.map((r) => r.page)).toEqual([1, 2, 2]);
  });

  it("a pdf link opens in a new tab with noopener", async () => {
    api();
    // A page 600 × 800 pt; pdf.js's rectangles count from the bottom left.
    const viewport = (scale: number) => ({ width: 600 * scale, height: 800 * scale, convertToViewportPoint: (x: number, y: number) => [x * scale, (800 - y) * scale] });
    const page = {
      getViewport: ({ scale }: { scale: number }) => viewport(scale),
      getAnnotations: async () => [
        { subtype: "Link", url: "https://example.org/paper", rect: [60, 700, 360, 780] },
        { subtype: "Link", url: "javascript:alert(1)", rect: [0, 0, 10, 10] },
        { subtype: "Widget", rect: [0, 0, 600, 800] },
      ],
      render: () => ({ promise: Promise.resolve(), cancel() {} }),
    };
    const doc = { numPages: 2, getPage: async () => page, getDestination: async () => null, getPageIndex: async () => 0 };
    vi.mocked(openPdf).mockResolvedValue({ promise: Promise.resolve(doc), destroy: async () => {} } as never);
    const router = renderApp(opened("/W/report.pdf", ["/W/report.pdf"]));

    expect(await screen.findByText("Page 1 / 2")).toBeInTheDocument();
    const links = await screen.findAllByRole("link", { name: "https://example.org/paper" });
    expect(links).toHaveLength(2); // one per page
    for (const link of links) {
      expect(link).toHaveAttribute("href", "https://example.org/paper");
      expect(link).toHaveAttribute("target", "_blank");
      expect(link.getAttribute("rel")?.split(" ")).toContain("noopener");
    }
    expect(links[0]).toHaveStyle({ left: "10%", top: "2.5%", width: "50%", height: "10%" });
    expect(screen.queryByRole("link", { name: /javascript/ })).not.toBeInTheDocument();

    // Clicking it leaves the app where it is.
    fireEvent.click(links[0]);
    expect(router.state.location.pathname).toBe("/view/W/report.pdf");
  });
});
