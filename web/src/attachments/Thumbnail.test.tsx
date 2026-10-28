// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fakeApi } from "../test/app";
import { openPdf } from "./pdf";
import Thumbnail, { maxPdfThumbs } from "./Thumbnail";

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
