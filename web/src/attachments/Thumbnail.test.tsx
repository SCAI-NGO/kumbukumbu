// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fakeApi } from "../test/app";
import { openPdf } from "./pdf";
import Thumbnail, { maxImageThumbs, maxPdfThumbs } from "./Thumbnail";

// pdf.js itself doesn't run in jsdom: each PDF opens as a fake whose drawing ends when the test says so.
vi.mock("./pdf", async (actual) => ({ ...(await actual<typeof import("./pdf")>()), openPdf: vi.fn() }));

/** One per PDF opened, in order: ends its drawing. */
let draws: (() => void)[] = [];

beforeEach(() => {
  draws = [];
  vi.mocked(openPdf).mockImplementation(async () => {
    let done!: () => void;
    const drawn = new Promise<void>((resolve) => (done = resolve));
    draws.push(done);
    return {
      promise: Promise.resolve({
        getPage: async () => ({
          getViewport: ({ scale }: { scale: number }) => ({ width: 600 * scale, height: 800 * scale }),
          render: () => ({ promise: drawn, cancel() {} }),
        }),
      }),
      destroy: async () => {},
    } as never;
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const pdf = () => new Response(new Uint8Array([0x25, 0x50, 0x44, 0x46, 0x2d]), { headers: { "Content-Type": "application/pdf" } });

describe("PDF thumbnails", () => {
  it("fetch and draw only a few at a time, the rest in turn", async () => {
    const asked: string[] = [];
    fakeApi({
      "GET /api/files/W/report.pdf": (r) => {
        asked.push(new URL(r.url).searchParams.get("version") ?? "");
        return pdf();
      },
    });
    const versions = ["v1", "v2", "v3", "v4", "v5"];
    const { unmount } = render(
      <div>
        {versions.map((v) => (
          <span key={v} className="relative">
            <Thumbnail vault="W" path="/W/report.pdf" at={{ version: v }} />
          </span>
        ))}
      </div>,
    );

    expect(maxPdfThumbs).toBe(2);
    await waitFor(() => expect(draws).toHaveLength(2));
    await new Promise((r) => setTimeout(r, 30));
    expect(asked).toEqual(["v1", "v2"]);

    // One drawn: the next one's turn.
    draws[0]();
    await waitFor(() => expect(asked).toEqual(["v1", "v2", "v3"]));
    await waitFor(() => expect(draws).toHaveLength(3));
    draws[1]();
    draws[2]();
    await waitFor(() => expect(asked).toEqual(versions));
    unmount();
  });

  it("lets a thumbnail that goes while waiting leave the queue, and one that goes while drawing free its turn", async () => {
    const asked: string[] = [];
    fakeApi({
      "GET /api/files/W/report.pdf": (r) => {
        asked.push(new URL(r.url).searchParams.get("version") ?? "");
        return pdf();
      },
    });
    const shown = (versions: string[]) => (
      <div>
        {versions.map((v) => (
          <span key={v} className="relative">
            <Thumbnail vault="W" path="/W/report.pdf" at={{ version: v }} />
          </span>
        ))}
      </div>
    );
    const { rerender } = render(shown(["a", "b", "c", "d"]));
    await waitFor(() => expect(draws).toHaveLength(2));

    // c (waiting) and a (drawing) go: b keeps drawing, d gets a's turn, c is never fetched.
    rerender(shown(["b", "d"]));
    await waitFor(() => expect(asked).toEqual(["a", "b", "d"]));
    await new Promise((r) => setTimeout(r, 30));
    expect(asked).not.toContain("c");
  });
});

describe("Image thumbnails", () => {
  it("fetch only a few at a time, the next once one's bytes are in", async () => {
    const { createObjectURL, revokeObjectURL } = URL;
    URL.createObjectURL = () => "blob:shot";
    URL.revokeObjectURL = () => {};
    const asked: string[] = [];
    const answers: (() => void)[] = [];
    fakeApi({
      "GET /api/files/W/shot.png": (r) => {
        asked.push(new URL(r.url).searchParams.get("version") ?? "");
        return new Promise<Response>((resolve) =>
          answers.push(() => resolve(new Response(new Uint8Array([0x89, 0x50]), { headers: { "Content-Type": "image/png" } }))),
        );
      },
    });
    const versions = ["v1", "v2", "v3", "v4", "v5", "v6", "v7", "v8"];
    try {
      const { unmount } = render(
        <div>
          {versions.map((v) => (
            <span key={v} className="relative">
              <Thumbnail vault="W" path="/W/shot.png" at={{ version: v }} />
            </span>
          ))}
        </div>,
      );

      expect(maxImageThumbs).toBe(6);
      await waitFor(() => expect(asked).toHaveLength(6));
      await new Promise((r) => setTimeout(r, 30));
      expect(asked).toEqual(versions.slice(0, 6));

      answers[0]();
      await waitFor(() => expect(asked).toEqual(versions.slice(0, 7)));
      answers[1]();
      await waitFor(() => expect(asked).toEqual(versions));
      unmount();
    } finally {
      URL.createObjectURL = createObjectURL;
      URL.revokeObjectURL = revokeObjectURL;
    }
  });
});

describe("SVG thumbnails", () => {
  it("show an SVG through an <img> of its object URL, never its markup in the page", async () => {
    const { createObjectURL, revokeObjectURL } = URL;
    const blobs: Blob[] = [];
    URL.createObjectURL = (blob: Blob) => (blobs.push(blob), "blob:diagram");
    URL.revokeObjectURL = () => {};
    const svg = '<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script><rect id="drawn" width="4" height="4"/></svg>';
    fakeApi({ "GET /api/files/W/diagram.svg": () => new Response(svg, { headers: { "Content-Type": "image/svg+xml" } }) });
    try {
      const { container } = render(
        <span className="relative">
          <Thumbnail vault="W" path="/W/diagram.svg" alt="diagram.svg" />
        </span>,
      );

      await waitFor(() => expect(container.querySelector("img")).toHaveAttribute("src", "blob:diagram"));
      expect(blobs[0]!.type).toBe("image/svg+xml");
      expect(container.querySelector("svg, script, #drawn, object, iframe")).toBeNull();
    } finally {
      URL.createObjectURL = createObjectURL;
      URL.revokeObjectURL = revokeObjectURL;
    }
  });
});

describe("Thumbnails of files larger than 15 MB", () => {
  it("fetch nothing and show the kind's icon, a PDF's and an image's", async () => {
    const fetch = fakeApi({ "GET /api/files/W/big.pdf": pdf, "GET /api/files/W/big.png": () => new Response(new Uint8Array([0x89])) });
    render(
      <div>
        <span className="relative">
          <Thumbnail vault="W" path="/W/big.pdf" size={15_728_641} alt="big.pdf" />
        </span>
        <span className="relative">
          <Thumbnail vault="W" path="/W/big.png" size={20_000_000} alt="big.png" />
        </span>
      </div>,
    );

    await new Promise((r) => setTimeout(r, 30));
    expect(fetch).not.toHaveBeenCalled();
    expect(document.querySelector("canvas")).toBeNull();
    expect(document.querySelector("img")).toBeNull();
    expect(document.querySelectorAll("svg[role=img]")).toHaveLength(2);
  });

  it("at exactly 15 MB still fetch and draw", async () => {
    const asked: string[] = [];
    fakeApi({ "GET /api/files/W/report.pdf": (r) => (asked.push(new URL(r.url).pathname), pdf()) });
    render(
      <span className="relative">
        <Thumbnail vault="W" path="/W/report.pdf" size={15_728_640} />
      </span>,
    );

    await waitFor(() => expect(asked).toEqual(["/api/files/W/report.pdf"]));
  });
});
