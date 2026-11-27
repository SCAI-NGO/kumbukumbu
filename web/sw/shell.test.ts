// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { contentType, precache, route } from "./shell";

describe("what the worker keeps", () => {
  const built = [
    "index.html",
    "s.html",
    "k.html",
    "sw.js",
    "assets/main-C1H6-F9b.js",
    "assets/src-WCMCQJEa.css",
    "assets/pdf.worker.min-B5Yc7dP4.mjs",
    "assets/pdfjs-2c5617357e/openjpeg_nowasm_fallback.js",
    "assets/ibm-plex-sans-latin-400-normal-Bz2ap9Ks.woff2",
    "assets/ibm-plex-sans-latin-400-normal-C3YdXUa8.woff",
    "assets/KaTeX_Main-Regular-B22Nviop.ttf",
  ];
  const shell = precache(built, ["manifest.webmanifest", "favicon.svg", "icons/icon-192.png"]);

  it("keeps the app's page, its built files and the public files", () => {
    expect(shell).toEqual([
      "/index.html",
      "/assets/main-C1H6-F9b.js",
      "/assets/src-WCMCQJEa.css",
      "/assets/pdf.worker.min-B5Yc7dP4.mjs",
      "/assets/pdfjs-2c5617357e/openjpeg_nowasm_fallback.js",
      "/assets/ibm-plex-sans-latin-400-normal-Bz2ap9Ks.woff2",
      "/manifest.webmanifest",
      "/favicon.svg",
      "/icons/icon-192.png",
    ]);
  });

  it("never keeps the reader and skill pages, nor the worker itself", () => {
    for (const path of ["/s.html", "/k.html", "/sw.js"]) expect(shell).not.toContain(path);
  });

  it("leaves out the fonts a browser with service workers never loads", () => {
    expect(shell.some((p) => p.endsWith(".woff") || p.endsWith(".ttf"))).toBe(false);
  });
});

describe("the build's list", () => {
  it("gives each kept file its content type", () => {
    expect(
      ["/index.html", "/assets/main-C1H6-F9b.js", "/assets/pdf.worker.min-B5Yc7dP4.mjs", "/assets/src-WCMCQJEa.css", "/assets/plex-Bz2ap9Ks.woff2", "/favicon.svg", "/icons/icon-192.png", "/manifest.webmanifest"].map(contentType),
    ).toEqual(["text/html", "text/javascript", "text/javascript", "text/css", "font/woff2", "image/svg+xml", "image/png", "application/manifest+json"]);
  });

  it("names no type for a file it doesn't know, so the build stops", () => {
    expect(() => contentType("/assets/data.bin")).toThrow("/assets/data.bin");
  });
});

describe("which requests the worker answers", () => {
  const origin = "https://kumbu.example";
  const shell = new Set(["/index.html", "/assets/main-C1H6-F9b.js", "/manifest.webmanifest"]);
  const at = (path: string, mode: RequestMode = "cors", method = "GET", base = origin) =>
    route({ url: new URL(path, base).href, mode, method }, origin, shell);

  it("answers a kept file from the cache", () => {
    expect(at("/assets/main-C1H6-F9b.js")).toBe("asset");
    expect(at("/manifest.webmanifest")).toBe("asset");
  });

  it("leaves a built file it doesn't keep to the network", () => {
    expect(at("/assets/main-old0ld00.js")).toBeNull();
  });

  it("answers a page of the app, from the network first", () => {
    for (const path of ["/", "/notes/Work/plan.md", "/settings", "/index.html"]) expect(at(path, "navigate")).toBe("page");
  });

  it("never answers the API, MCP, sign-in of other apps, WebDAV or the reader and skill pages", () => {
    for (const path of ["/api/version", "/api/files/Work/a.pdf", "/mcp", "/mcp/c/1", "/oauth/authorize?x=1", "/.well-known/oauth-authorization-server", "/dav/Work/", "/healthz", "/s", "/s.html", "/k", "/k.html", "/sw.js"]) {
      expect(at(path, "navigate"), path).toBeNull();
      expect(at(path), path).toBeNull();
    }
  });

  it("never answers a write, another origin, or a page asked for by a script", () => {
    expect(at("/", "navigate", "POST")).toBeNull();
    expect(at("/assets/main-C1H6-F9b.js", "cors", "GET", "https://elsewhere.example")).toBeNull();
    expect(at("/notes/Work/plan.md")).toBeNull();
  });
});
