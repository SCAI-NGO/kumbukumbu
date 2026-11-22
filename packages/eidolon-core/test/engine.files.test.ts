// SPDX-License-Identifier: AGPL-3.0-or-later
// A Kumbu² 0.6.0 server: every file type is kept (kind "file"), SVG is an image, files may be larger than 15 MB.
import { afterEach, describe, expect, test, vi } from "vitest";
import { MAX_ATTACHMENT_BYTES } from "../src/engine/engine";
import { harness } from "./fakes/harness";

afterEach(() => { vi.useRealTimers(); });

const svg = new TextEncoder().encode('<svg xmlns="http://www.w3.org/2000/svg"/>');

describe("files of kinds the plugin doesn't sync", () => {
  test("a file on the server is neither downloaded nor counted as a note", async () => {
    const h = await harness({ start: false, synced: { "a.md": "one" }, remoteFiles: { "report.docx": new Uint8Array([1, 2]), "Makefile": new Uint8Array([3]), "logo.svg": svg } });
    await h.engine.start();
    expect(h.files.binaries.has("report.docx")).toBe(false);
    expect(h.files.binaries.has("Makefile")).toBe(false);
    expect(h.files.binaries.has("logo.svg")).toBe(false);
    expect(h.server.calls.filter((c) => c.method === "readFile" || c.method === "read")).toEqual([]);
  });

  test("a local file of such a kind stays local, the server's copy of it untouched", async () => {
    const h = await harness({ start: false, synced: { "a.md": "one" }, remoteFiles: { "data.csv": new Uint8Array([9]), "logo.svg": svg } });
    h.files.binaries.set("data.csv", new Uint8Array([1]));
    h.files.binaries.set("logo.svg", new Uint8Array([2]));
    h.files.binaries.set("board.canvas", new Uint8Array([3]));
    await h.engine.start();
    expect(h.server.files.get("data.csv")?.bytes).toEqual(new Uint8Array([9]));
    expect(h.server.files.get("logo.svg")?.bytes).toEqual(svg);
    expect(h.server.files.has("board.canvas")).toBe(false);
    expect(h.files.binaries.get("data.csv")).toEqual(new Uint8Array([1]));
    expect(h.engine.status().notSynced.map((n) => n.path).sort()).toEqual(["board.canvas", "data.csv", "logo.svg"]);
  });

  test("a file deleted on the server leaves the local file of that name alone", async () => {
    const h = await harness({ synced: { "a.md": "one" }, remoteFiles: { "report.docx": new Uint8Array([1]) } });
    h.files.binaries.set("report.docx", new Uint8Array([1]));
    await h.engine.check(true);
    h.server.agentRemoveFile("report.docx");
    await h.engine.check(false);
    expect(h.files.binaries.get("report.docx")).toEqual(new Uint8Array([1]));
  });
});

describe("files larger than 15 MB in Kumbu²", () => {
  const bigPdf = () => {
    const big = new Uint8Array(MAX_ATTACHMENT_BYTES + 1);
    big.set(new TextEncoder().encode("%PDF-1.7\n"));
    return big;
  };
  const small = new TextEncoder().encode("%PDF-1.7\nsmall");

  test("a PDF over 15 MB in Kumbu² is not downloaded, and is listed as too large there", async () => {
    const h = await harness({ start: false, synced: { "a.md": "one" }, remoteFiles: { "scan.pdf": bigPdf() } });
    await h.engine.start();
    expect(h.server.calls.filter((c) => c.method === "readFile")).toEqual([]);
    expect(h.files.binaries.has("scan.pdf")).toBe(false);
    expect(h.engine.status().notSynced).toEqual([{ path: "scan.pdf", reason: "too-large-in-kumbu" }]);
  });

  test("a local file of that name is neither replaced nor uploaded over it", async () => {
    const h = await harness({ start: false, synced: { "a.md": "one" }, remoteFiles: { "scan.pdf": bigPdf() } });
    h.files.binaries.set("scan.pdf", small);
    await h.engine.start();
    expect(h.files.binaries.get("scan.pdf")).toEqual(small);
    expect(h.server.files.get("scan.pdf")?.bytes.byteLength).toBe(MAX_ATTACHMENT_BYTES + 1);
    expect(h.server.calls.filter((c) => c.method === "readFile" || c.method === "putFile")).toEqual([]);
  });

  test("once it is smaller in Kumbu², it comes over as any change does", async () => {
    const h = await harness({ start: false, synced: { "a.md": "one" }, remoteFiles: { "scan.pdf": bigPdf() } });
    await h.engine.start();
    await h.server.seedFile("scan.pdf", small);
    await h.engine.check(false);
    expect(h.files.binaries.get("scan.pdf")).toEqual(small);
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("the first sync doesn't ask about it", async () => {
    const h = await harness({ start: false, synced: {}, remoteFiles: { "scan.pdf": bigPdf() } });
    h.files.binaries.set("scan.pdf", small);
    const plans: unknown[] = [];
    const summary = await h.engine.firstSync((plan) => { plans.push(plan); return Promise.resolve("both"); });
    expect(plans).toEqual([]);
    expect(summary?.notSynced).toEqual([{ path: "scan.pdf", reason: "too-large-in-kumbu" }]);
    expect(h.files.binaries.get("scan.pdf")).toEqual(small);
  });

  test("a server that names no sizes is synced as before", async () => {
    const h = await harness({ start: false, synced: { "a.md": "one" }, remoteFiles: { "small.pdf": small } });
    h.server.sizes = false;
    await h.engine.start();
    expect(h.files.binaries.get("small.pdf")).toEqual(small);
  });
});
