// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { asset, type Kept, keptPage, page as appPage, sha256 } from "./kept";
import { appPolicy, kdfWorkerPolicy } from "./policy";

const script = "export const app = 1;";
const page = "<!doctype html><script type=module src=/assets/main.js></script>";

/** A cache as any page of the origin can write it, with what was asked of it. */
async function cacheOf(entries: Record<string, string>): Promise<Kept & { entries: Map<string, Response> }> {
  const map = new Map(Object.entries(entries).map(([path, body]) => [path, new Response(body, { status: 200 })]));
  return {
    entries: map,
    match: async (path) => map.get(path)?.clone(),
    put: async (path, response) => void map.set(path, response),
    delete: async (path) => map.delete(path),
  };
}

const network = (body: string) => async () => new Response(body, { status: 200 });
const offline = async (): Promise<Response> => {
  throw new TypeError("Failed to fetch");
};

/** A file as the build lists it: its SHA-256 and its content type. */
const listed = async (body: string, type = "text/javascript") => ({ sha256: await sha256(new TextEncoder().encode(body)), type });

/** An answer as the network or the cache gives it: with the URL it came from, which its clones keep. */
function answered(url: string, body: string, headers: Record<string, string>): Response {
  const response = new Response(body, { status: 200, headers });
  Object.defineProperty(response, "url", { value: url });
  Object.defineProperty(response, "clone", { value: () => answered(url, body, headers) });
  return response;
}

describe("a kept file", () => {
  it("is served from the cache while its bytes are the build's, without asking the network", async () => {
    const cache = await cacheOf({ "/assets/main.js": script });
    const answer = await asset(cache, "/assets/main.js", await listed(script), offline);
    expect(await answer.text()).toBe(script);
  });

  it("changed in the cache is deleted and fetched from the server, and the server's is kept again", async () => {
    const cache = await cacheOf({ "/assets/main.js": "the attacker's script" });
    const answer = await asset(cache, "/assets/main.js", await listed(script), network(script));
    expect(await answer.text()).toBe(script);
    expect(await (await cache.match("/assets/main.js"))!.text()).toBe(script);
  });

  it("changed in the cache is never served offline: the request fails instead", async () => {
    const cache = await cacheOf({ "/assets/main.js": "the attacker's script" });
    expect((await asset(cache, "/assets/main.js", await listed(script), offline)).type).toBe("error");
    expect(cache.entries.has("/assets/main.js")).toBe(false);
  });

  it("from the network that isn't the build's is passed on, but not kept", async () => {
    const cache = await cacheOf({});
    const answer = await asset(cache, "/assets/main.js", await listed(script), network("another build's file"));
    expect(await answer.text()).toBe("another build's file");
    expect(cache.entries.has("/assets/main.js")).toBe(false);
  });

  it("kept with another status than 200 counts as changed", async () => {
    const cache = await cacheOf({});
    cache.entries.set("/assets/main.js", new Response(script, { status: 203 }));
    expect((await asset(cache, "/assets/main.js", await listed(script), offline)).type).toBe("error");
    expect(cache.entries.has("/assets/main.js")).toBe(false);
  });
});

describe("the kept page, for a start without a network", () => {
  it("is served while its bytes are the build's", async () => {
    const cache = await cacheOf({ "/index.html": page });
    expect(await (await keptPage(cache, await listed(page, "text/html")))!.text()).toBe(page);
  });

  it("changed in the cache is deleted and not served", async () => {
    const cache = await cacheOf({ "/index.html": "<script>steal()</script>" });
    expect(await keptPage(cache, await listed(page, "text/html"))).toBeUndefined();
    expect(cache.entries.has("/index.html")).toBe(false);
  });
});

describe("offline, a file it can't fetch", () => {
  it("answers an error response, with no uncaught rejection", async () => {
    const answer = await asset(await cacheOf({}), "/assets/main.js", await listed(script), offline);
    expect([answer.type, answer.status]).toEqual(["error", 0]);
  });

  it("answers an error response for a page it neither fetches nor keeps", async () => {
    const answer = await appPage(await cacheOf({}), await listed(page, "text/html"), offline);
    expect([answer.type, answer.status]).toEqual(["error", 0]);
  });

  it("answers a page from the network first, and the kept one only without a network", async () => {
    const cache = await cacheOf({ "/index.html": page });
    expect(await (await appPage(cache, await listed(page, "text/html"), network("the server's page"))).text()).toBe("the server's page");
    expect(await (await appPage(cache, await listed(page, "text/html"), offline)).text()).toBe(page);
  });
});

describe("what the worker answers", () => {
  const ownHeaders = (answer: Response | undefined) => ({
    status: answer?.status,
    url: answer?.url,
    type: answer?.headers.get("Content-Type"),
    nosniff: answer?.headers.get("X-Content-Type-Options"),
    referrer: answer?.headers.get("Referrer-Policy"),
    policy: answer?.headers.get("Content-Security-Policy"),
    other: answer?.headers.get("Cache-Control"),
  });
  const kept = { "Content-Type": "text/plain", "Cache-Control": "max-age=60" };

  it("answers a kept file with a new response of the checked bytes and its own headers", async () => {
    const worker = "self.onmessage = () => {};";
    const cache = await cacheOf({});
    cache.entries.set("/index.html", answered("https://kumbu.example/elsewhere/index.html", page, kept));
    cache.entries.set("/assets/main.js", answered("https://kumbu.example/elsewhere/main.js", script, kept));
    cache.entries.set("/assets/kdfWorker-B1x2.js", answered("https://kumbu.example/elsewhere/kdf.js", worker, kept));

    const own = { status: 200, url: "", nosniff: "nosniff", referrer: "same-origin", other: null };
    const pageAnswer = await keptPage(cache, await listed(page, "text/html"));
    expect(ownHeaders(pageAnswer)).toEqual({ ...own, type: "text/html", policy: appPolicy });
    expect(await pageAnswer!.text()).toBe(page);
    const scriptAnswer = await asset(cache, "/assets/main.js", await listed(script), offline);
    expect(ownHeaders(scriptAnswer)).toEqual({ ...own, type: "text/javascript", policy: appPolicy });
    expect(await scriptAnswer.text()).toBe(script);
    const workerAnswer = await asset(cache, "/assets/kdfWorker-B1x2.js", await listed(worker), offline);
    expect(ownHeaders(workerAnswer)).toEqual({ ...own, type: "text/javascript", policy: kdfWorkerPolicy });
  });

  it("keeps the bytes of a fresh answer, not the network's response", async () => {
    const cache = await cacheOf({});
    const fetched = async () => answered("https://kumbu.example/elsewhere/main.js", script, kept);
    const answer = await asset(cache, "/assets/main.js", await listed(script), fetched);
    const own = { status: 200, url: "", type: "text/javascript", nosniff: "nosniff", referrer: "same-origin", policy: appPolicy, other: null };
    expect(ownHeaders(answer)).toEqual(own);
    expect(await answer.text()).toBe(script);
    const stored = cache.entries.get("/assets/main.js");
    expect(ownHeaders(stored)).toEqual(own);
    expect(await stored!.text()).toBe(script);
  });
});

describe("sha256", () => {
  it("is the hex digest the build writes", async () => {
    expect(await sha256(new TextEncoder().encode("abc"))).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  });
});
