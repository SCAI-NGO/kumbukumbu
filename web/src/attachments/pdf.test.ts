// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from "vitest";

const pdfjs = vi.hoisted(() => ({
  GlobalWorkerOptions: { workerSrc: "" },
  getDocument: vi.fn(() => ({ promise: new Promise(() => {}), destroy: async () => {} })),
}));
vi.mock("pdfjs-dist/legacy/build/pdf.mjs", () => pdfjs);

const { openPdf, pageLinks } = await import("./pdf");

describe("pdf.js", () => {
  // pdfjs-dist 6.x no longer reads `isEvalSupported`: it compiles nothing from a PDF into JavaScript. The option is
  // asserted because it is set (and would count in a 5.x build); what holds whatever pdf.js does is the CSP, whose
  // script-src has no 'unsafe-eval' (Api/Security.cs).
  it("pdf.js is loaded with isEvalSupported false", async () => {
    const data = new ArrayBuffer(8);
    await openPdf(data);

    expect(pdfjs.getDocument).toHaveBeenCalledWith(expect.objectContaining({ data, isEvalSupported: false, useWasm: false, enableXfa: false, maxImageSize: 50_000_000 }));
    // The worker and the image decoders come from the app's own origin.
    const worker = new URL(pdfjs.GlobalWorkerOptions.workerSrc, window.location.href);
    expect(worker.origin).toBe(window.location.origin);
    expect(worker.pathname).toMatch(/pdf\.worker\.min\.mjs$/);
    const { wasmUrl } = (pdfjs.getDocument.mock.calls[0] as unknown as [{ wasmUrl: string }])[0];
    expect(wasmUrl).toMatch(new RegExp(`^${window.location.origin}/assets/pdfjs-[0-9a-f]{10}/$`));
  });

  it("keeps a page's links to addresses and places in the PDF, nothing else", async () => {
    const viewport = { width: 300, height: 400, convertToViewportPoint: (x: number, y: number) => [x / 2, (800 - y) / 2] };
    const page = {
      getAnnotations: async () => [
        { subtype: "Link", url: "https://example.org", rect: [0, 400, 600, 800] },
        { subtype: "Link", url: "mailto:ada@example.org", rect: [0, 0, 60, 80] },
        { subtype: "Link", dest: "chapter-2", rect: [0, 0, 60, 80] },
        { subtype: "Link", url: "javascript:alert(1)", rect: [0, 0, 60, 80] },
        { subtype: "Link", url: "file:///etc/passwd", rect: [0, 0, 60, 80] },
        { subtype: "Link", rect: [0, 0, 60, 80] },
        { subtype: "Widget", rect: [0, 0, 60, 80] },
      ],
    };
    const links = await pageLinks(page as never, viewport as never);
    expect(links).toEqual([
      { url: "https://example.org", dest: undefined, left: 0, top: 0, width: 1, height: 0.5 },
      { url: "mailto:ada@example.org", dest: undefined, left: 0, top: 0.9, width: 0.1, height: 0.1 },
      { url: undefined, dest: "chapter-2", left: 0, top: 0.9, width: 0.1, height: 0.1 },
    ]);
  });
});
