// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { asset, type Kept, keptPage, sha256 } from "./kept";

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

describe("a kept file", () => {
  it("is served from the cache while its bytes are the build's, without asking the network", async () => {
    const cache = await cacheOf({ "/assets/main.js": script });
    const answer = await asset(cache, "/assets/main.js", await sha256(new TextEncoder().encode(script)), offline);
    expect(await answer.text()).toBe(script);
  });

  it("changed in the cache is deleted and fetched from the server, and the server's is kept again", async () => {
    const cache = await cacheOf({ "/assets/main.js": "the attacker's script" });
    const answer = await asset(cache, "/assets/main.js", await sha256(new TextEncoder().encode(script)), network(script));
    expect(await answer.text()).toBe(script);
    expect(await (await cache.match("/assets/main.js"))!.text()).toBe(script);
  });

  it("changed in the cache is never served offline: the request fails instead", async () => {
    const cache = await cacheOf({ "/assets/main.js": "the attacker's script" });
    await expect(asset(cache, "/assets/main.js", await sha256(new TextEncoder().encode(script)), offline)).rejects.toThrow("Failed to fetch");
    expect(cache.entries.has("/assets/main.js")).toBe(false);
  });

  it("from the network that isn't the build's is passed on, but not kept", async () => {
    const cache = await cacheOf({});
    const answer = await asset(cache, "/assets/main.js", await sha256(new TextEncoder().encode(script)), network("another build's file"));
    expect(await answer.text()).toBe("another build's file");
    expect(cache.entries.has("/assets/main.js")).toBe(false);
  });

  it("kept with another status than 200 counts as changed", async () => {
    const cache = await cacheOf({});
    cache.entries.set("/assets/main.js", new Response(script, { status: 203 }));
    await expect(asset(cache, "/assets/main.js", await sha256(new TextEncoder().encode(script)), offline)).rejects.toThrow();
    expect(cache.entries.has("/assets/main.js")).toBe(false);
  });
});

describe("the kept page, for a start without a network", () => {
  it("is served while its bytes are the build's", async () => {
    const cache = await cacheOf({ "/index.html": page });
    expect(await (await keptPage(cache, await sha256(new TextEncoder().encode(page))))!.text()).toBe(page);
  });

  it("changed in the cache is deleted and not served", async () => {
    const cache = await cacheOf({ "/index.html": "<script>steal()</script>" });
    expect(await keptPage(cache, await sha256(new TextEncoder().encode(page)))).toBeUndefined();
    expect(cache.entries.has("/index.html")).toBe(false);
  });
});

describe("sha256", () => {
  it("is the hex digest the build writes", async () => {
    expect(await sha256(new TextEncoder().encode("abc"))).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  });
});
