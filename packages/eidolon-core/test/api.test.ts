// SPDX-License-Identifier: AGPL-3.0-or-later
// The attachments API (GET/PUT/DELETE /api/files) and how the client and the engine tell a server that has it.
import { afterEach, describe, expect, test, vi } from "vitest";
import { KumbuApi, type HttpFn, type HttpRequest, type HttpResponse } from "../src/api/client";
import { AuthError, OfflineError, RejectedError, VaultChangedError } from "../src/engine/ports";
import { versionOfBytes } from "../src/engine/version";
import { harness } from "./fakes/harness";

afterEach(() => { vi.useRealTimers(); });

type Reply = { status: number; body?: unknown; bytes?: Uint8Array; headers?: Record<string, string> };
function fakeHttp(...replies: Reply[]): HttpFn & { calls: HttpRequest[] } {
  const calls: HttpRequest[] = [];
  const fn = (async (r: HttpRequest): Promise<HttpResponse> => {
    calls.push(r);
    const reply = replies.length > 1 ? replies.shift()! : replies[0]!;
    const text = reply.body === undefined ? "" : JSON.stringify(reply.body);
    const bytes = reply.bytes ? reply.bytes.slice().buffer : new TextEncoder().encode(text).buffer;
    return Promise.resolve({ status: reply.status, headers: reply.headers ?? {}, text, bytes });
  }) as HttpFn & { calls: HttpRequest[] };
  fn.calls = calls;
  return fn;
}
const api = (http: HttpFn) => new KumbuApi(http, "https://k.example/", "Work", "kumbu_x");
const last = (http: { calls: HttpRequest[] }) => http.calls[http.calls.length - 1]!;
const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x0d, 0x0a]);
const state = (version: string) => ({
  path: "/Work/img/a b.png", mediaType: "image/png", size: 10, width: 1, height: 1, version,
  writtenAt: "2026-10-28T10:00:00Z", writtenBy: "liz",
});

describe("binary transfer", () => {
  test("the client sends and receives array buffers", async () => {
    const version = await versionOfBytes(png);
    const http = fakeHttp({ status: 201, body: state(version) }, { status: 200, bytes: png, headers: { ETag: `"${version}"` } });
    const client = api(http);

    expect(await client.putFile("img/a b.png", png.slice().buffer, null)).toEqual({ ok: true, version });
    const put = last(http);
    expect(put).toMatchObject({ method: "PUT", url: "https://k.example/api/files/Work/img/a%20b.png",
      headers: { Authorization: "Bearer kumbu_x", "Content-Type": "application/octet-stream" } });
    expect(put.body).toBeInstanceOf(ArrayBuffer);
    expect(new Uint8Array(put.body as ArrayBuffer)).toEqual(png);

    const file = await client.readFile("img/a b.png");
    expect(last(http)).toMatchObject({ method: "GET", url: "https://k.example/api/files/Work/img/a%20b.png" });
    expect(file?.version).toBe(version);
    expect(new Uint8Array(file!.bytes)).toEqual(png);
  });

  test("an overwrite sends the base version; a 409 is a conflict with the version, without bytes", async () => {
    const http = fakeHttp({ status: 409, body: { error: "changed", current: state("dddddddddddd") } });
    expect(await api(http).putFile("img/a b.png", png.slice().buffer, "aaaaaaaaaaaa"))
      .toEqual({ ok: false, conflict: { version: "dddddddddddd" } });
    expect(last(http).url).toBe("https://k.example/api/files/Work/img/a%20b.png?version=aaaaaaaaaaaa");
  });

  test("a put with a base whose file is gone becomes a create", async () => {
    const http = fakeHttp({ status: 404, body: { error: "No file /Work/a.png." } }, { status: 201, body: state("cccccccccccc") });
    expect(await api(http).putFile("a.png", png.slice().buffer, "aaaaaaaaaaaa")).toEqual({ ok: true, version: "cccccccccccc" });
    expect(last(http).url).toBe("https://k.example/api/files/Work/a.png");
  });

  test("a 404 on a create is refused, not a lost key: an old server has no /api/files", async () => {
    const p = api(fakeHttp({ status: 404, body: { error: "Not found." } })).putFile("a.png", png.slice().buffer, null);
    await expect(p).rejects.toBeInstanceOf(RejectedError);
    await expect(p).rejects.not.toBeInstanceOf(AuthError);
  });

  test("413 and 400 carry the server's sentence", async () => {
    const big = api(fakeHttp({ status: 413, body: { error: "A file can have at most 15 MB." } })).putFile("a.pdf", new ArrayBuffer(1), null);
    await expect(big).rejects.toBeInstanceOf(RejectedError);
    await expect(big).rejects.toThrow("A file can have at most 15 MB.");
    await expect(api(fakeHttp({ status: 400, body: { error: "This is not a PNG." } })).putFile("a.png", new ArrayBuffer(1), null))
      .rejects.toThrow("This is not a PNG.");
  });

  test("a read without an ETag takes the version of the bytes; 404 is null", async () => {
    const file = await api(fakeHttp({ status: 200, bytes: png })).readFile("a.png");
    expect(file?.version).toBe(await versionOfBytes(png));
    expect(await api(fakeHttp({ status: 404, body: { error: "gone" } })).readFile("a.png")).toBeNull();
  });

  test("a read whose answer has no bytes is offline, never an empty file", async () => {
    const http: HttpFn = () => Promise.resolve({ status: 200, headers: { ETag: '"aaaaaaaaaaaa"' }, text: "" });
    await expect(api(http).readFile("a.png")).rejects.toBeInstanceOf(OfflineError);
  });

  test("removeFile sends ?version= and maps 204, 404 and 409", async () => {
    const ok = fakeHttp({ status: 204 });
    expect(await api(ok).removeFile("img/a b.png", "aaaaaaaaaaaa")).toEqual({ ok: true });
    expect(last(ok)).toMatchObject({ method: "DELETE", url: "https://k.example/api/files/Work/img/a%20b.png?version=aaaaaaaaaaaa" });
    expect(await api(fakeHttp({ status: 404, body: { error: "gone" } })).removeFile("a.png", "aaaaaaaaaaaa")).toEqual({ ok: false, conflict: null });
    expect(await api(fakeHttp({ status: 409, body: { error: "changed", current: state("dddddddddddd") } })).removeFile("a.png", "aaaaaaaaaaaa"))
      .toEqual({ ok: false, conflict: { version: "dddddddddddd" } });
  });

  test("a 409 on removeFile with the file still at the base is refused", async () => {
    const p = api(fakeHttp({ status: 409, body: { error: "It can't be trashed now.", current: state("aaaaaaaaaaaa") } })).removeFile("a.png", "aaaaaaaaaaaa");
    await expect(p).rejects.toBeInstanceOf(RejectedError);
    await expect(p).rejects.toThrow("It can't be trashed now.");
  });

  test("a file's path is NFC in the url", async () => {
    const http = fakeHttp({ status: 404, body: { error: "gone" } });
    await api(http).readFile("Bespréchung.png");
    expect(last(http).url).toBe(`https://k.example/api/files/Work/${encodeURIComponent("Bespréchung.png".normalize("NFC"))}`);
  });

  test("sends the vault's id with every file request, and a name that is another vault's now is refused", async () => {
    const version = await versionOfBytes(png);
    const http = fakeHttp({ status: 201, body: state(version) }, { status: 200, bytes: png, headers: { ETag: `"${version}"` } }, { status: 204 },
      { status: 409, body: { error: "Work is another vault now.", code: "vault-changed" } });
    const client = new KumbuApi(http, "https://k.example/", "Work", null, "vault-1f3a");
    await client.putFile("a.png", png.slice().buffer, null);
    await client.readFile("a.png");
    await client.removeFile("a.png", version);
    expect(http.calls.map((c) => c.headers["Kumbu-Vault-Id"])).toEqual(["vault-1f3a", "vault-1f3a", "vault-1f3a"]);
    await expect(client.putFile("a.png", png.slice().buffer, version)).rejects.toBeInstanceOf(VaultChangedError);
    expect(http.calls).toHaveLength(4);
  });
});

describe("server detection", () => {
  test("versions asks for attachments and reads the marker and each entry's kind", async () => {
    const http = fakeHttp({ status: 200, body: { full: true, cursor: "c", state: "s", damaged: 0, attachments: true, deleted: [],
      notes: [{ path: "/Work/a.md", version: "aaaaaaaaaaaa", kind: "note" }, { path: "/Work/b.png", version: "bbbbbbbbbbbb", kind: "image" }] } });
    expect(await api(http).versions(null, null)).toMatchObject({ attachments: true, entries: [
      { path: "a.md", version: "aaaaaaaaaaaa", kind: "note" }, { path: "b.png", version: "bbbbbbbbbbbb", kind: "image" }] });
    expect(last(http).url).toBe("https://k.example/api/vaults/Work/versions?attachments=1");
  });

  test("versions passes each attachment's size on, and only a number", async () => {
    const http = fakeHttp({ status: 200, body: { full: true, cursor: "c", state: "s", damaged: 0, attachments: true, deleted: [],
      notes: [{ path: "/Work/a.md", version: "aaaaaaaaaaaa", kind: "note" }, { path: "/Work/b.pdf", version: "bbbbbbbbbbbb", kind: "pdf", size: 20_000_000 },
        { path: "/Work/c.png", version: "cccccccccccc", kind: "image", size: "big" }] } });
    const { entries } = await api(http).versions(null, null);
    expect(entries.map((e) => e.size)).toEqual([undefined, 20_000_000, undefined]);
  });

  test("an empty vault on a server with attachments still detects them", async () => {
    const http = fakeHttp({ status: 200, body: { full: true, cursor: "c", state: "s", damaged: 0, attachments: true, notes: [], deleted: [] } });
    expect(await api(http).versions(null, null)).toMatchObject({ attachments: true, entries: [] });
  });

  test("a server that ignores the parameter has no attachments", async () => {
    const http = fakeHttp({ status: 200, body: { full: false, cursor: "c", state: "s", damaged: 0,
      notes: [{ path: "/Work/a.md", version: "aaaaaaaaaaaa" }], deleted: [] } });
    const answer = await api(http).versions("c0", null);
    expect(answer.attachments).toBe(false);
    expect(answer.entries).toEqual([{ path: "a.md", version: "aaaaaaaaaaaa" }]);
    expect(last(http).url).toBe("https://k.example/api/vaults/Work/versions?attachments=1&since=c0");
  });

  test("a 304 keeps what the last answer said", async () => {
    const http = fakeHttp(
      { status: 200, body: { full: true, cursor: "c", state: "s", damaged: 0, attachments: true, notes: [], deleted: [] } },
      { status: 304 });
    const client = api(http);
    await client.versions(null, null);
    expect(await client.versions(null, "s")).toMatchObject({ unchanged: true, attachments: true });
  });

  test("a fresh client sends no If-None-Match, so a restart with an unchanged vault still detects attachments", async () => {
    const calls: HttpRequest[] = [];
    // A server that answers 304 to the tag it has: the state the plugin kept from before the restart.
    const http: HttpFn = (r) => {
      calls.push(r);
      if (r.headers["If-None-Match"] === '"s"') return Promise.resolve({ status: 304, headers: {}, text: "" });
      const body = { full: true, cursor: "c", state: "s", damaged: 0, attachments: true, notes: [], deleted: [] };
      return Promise.resolve({ status: 200, headers: {}, text: JSON.stringify(body) });
    };
    const client = api(http);
    expect(await client.versions(null, "s")).toMatchObject({ unchanged: false, attachments: true });
    expect(calls[0]!.headers["If-None-Match"]).toBeUndefined();
    expect(await client.versions(null, "s")).toMatchObject({ unchanged: true, attachments: true });
  });

  test("an old server is detected and attachments stay unsynced", async () => {
    const h = await harness({ synced: { "a.md": "one" }, localOnly: { "img/x.png": "png" }, oldServer: true });
    expect(h.engine.attachmentsSupported).toBe(false);
    expect(h.engine.status().notSynced).toEqual([{ path: "img/x.png", reason: "attachment" }]);
    await h.engine.check(true);
    expect(h.server.calls.map((c) => c.method)).toEqual(["versions"]);
    expect(h.server.files.size).toBe(0);
  });

  test("a server with attachments is detected, and its files sync", async () => {
    const h = await harness({
      localOnly: { "a.md": "one", "img/x.png": "png" }, remoteOnly: { "a.md": "one" }, remoteFiles: { "img/y.png": png }, start: false,
    });
    const choose = vi.fn(() => Promise.resolve("both" as const));
    const summary = await h.engine.firstSync(choose);
    expect(h.engine.attachmentsSupported).toBe(true);
    expect(choose).not.toHaveBeenCalled();
    expect(summary).toMatchObject({ uploaded: 1, downloaded: 1, unchanged: 1 });
    expect(h.files.binaries.get("img/y.png")).toEqual(png);
    expect(h.server.files.has("img/x.png")).toBe(true);
    expect(h.engine.status().notSynced).toEqual([]);
  });
});
