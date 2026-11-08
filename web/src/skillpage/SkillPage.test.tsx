// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen, waitFor, within } from "@testing-library/react";
import { StrictMode } from "react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SkillPage from "./SkillPage";

const code = "K7QM-4XDA-9PRT-2VNB-6HJC-8EWS-3FGY-5KZD";

const copyId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const skill = (manifest: string, files = [{ index: 0, path: "SKILL.md", mediaType: "text/markdown", size: 412 }], hasScripts = false) => ({
  name: "write-as-alice",
  description: "Write in Alice's voice.",
  manifest,
  snapshotAt: "2026-11-05T14:32:00Z",
  files,
  hasScripts,
  copy: copyId,
});

const none = () => json(404, { error: "This link doesn't open a shared skill: the code is wrong, or the skill is no longer shared." });

type Handler = (init: RequestInit | undefined) => Response | Promise<Response>;

/** Answers `fetch` by "METHOD /path"; any other request fails the test. */
function stubFetch(handlers: Record<string, Handler>) {
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${new URL(String(input), location.href).pathname}`;
    const handler = handlers[key];
    if (!handler) throw new Error(`Unexpected request: ${key}`);
    return handler(init);
  });
  vi.stubGlobal("fetch", fetch);
  return fetch;
}

/** jsdom has no blob URLs: they are stubbed, and spied on. */
function blobUrls() {
  for (const name of ["createObjectURL", "revokeObjectURL"] as const) {
    if (!(name in URL)) Object.defineProperty(URL, name, { value: () => undefined, configurable: true, writable: true });
  }
  return {
    create: vi.spyOn(URL, "createObjectURL").mockImplementation(() => "blob:http://localhost/0"),
    revoke: vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined),
  };
}

const urls = (fetch: ReturnType<typeof stubFetch>) => fetch.mock.calls.map(([url]) => String(url));

beforeEach(() => {
  process.env.TZ = "UTC";
  history.replaceState(null, "", "/k");
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("SkillPage", () => {
  it("renders SKILL.md and lists the files", async () => {
    history.replaceState(null, "", `/k#${code}`);
    const replaceState = vi.spyOn(history, "replaceState");
    const files = [
      { index: 0, path: "SKILL.md", mediaType: "text/markdown", size: 412 },
      { index: 1, path: "examples/post.md", mediaType: "text/plain; charset=utf-8", size: 2048 },
    ];
    const fetch = stubFetch({ "POST /api/k": () => json(200, skill("# Writing\n\nUse **short** sentences.", files)) });

    render(
      <StrictMode>
        <SkillPage />
      </StrictMode>,
    );

    expect(await screen.findByRole("heading", { name: "write-as-alice" })).toBeInTheDocument();
    // The code leaves the address bar before it goes anywhere, and goes once.
    expect(replaceState).toHaveBeenCalledWith(null, "", "/k");
    expect(replaceState.mock.invocationCallOrder[0]).toBeLessThan(fetch.mock.invocationCallOrder[0]);
    expect(location.hash).toBe("");
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch.mock.calls[0][1]?.body).toBe(JSON.stringify({ code }));
    expect(fetch.mock.calls[0][1]?.credentials).toBe("same-origin");

    expect(screen.getByText("Write in Alice's voice.")).toBeInTheDocument();
    expect(screen.getByText("Snapshot of 5 Nov 2026, 14:32")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Writing" })).toBeInTheDocument();
    expect(screen.getByText("short").tagName).toBe("STRONG");
    const list = screen.getByRole("list", { name: "Files" });
    expect(within(list).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["SKILL.md412 B", "examples/post.md2.0 KB"]);
    expect(screen.queryByText(/This skill contains scripts/)).not.toBeInTheDocument();
  });

  it("reopens with the cookie when there is no fragment", async () => {
    const fetch = stubFetch({ "GET /api/k": () => json(200, skill("Body")) });
    const { unmount } = render(<SkillPage />);
    expect(await screen.findByRole("heading", { name: "write-as-alice" })).toBeInTheDocument();
    expect(fetch.mock.calls[0][1]?.credentials).toBe("same-origin");
    unmount();

    stubFetch({ "GET /api/k": none });
    render(<SkillPage />);
    expect(await screen.findByText("This link doesn't open a shared skill: the code is wrong, or the skill is no longer shared.")).toBeInTheDocument();
  });

  it("says when the server opens no shared skills, and when there were too many tries", async () => {
    stubFetch({ "GET /api/k": () => json(404, { error: "Shared skills can't be opened on this server.", type: "publishing-off" }) });
    const { unmount } = render(<SkillPage />);
    expect(await screen.findByText("Shared skills can't be opened on this server.")).toBeInTheDocument();
    unmount();

    stubFetch({ "GET /api/k": () => json(429, { error: "Too many attempts; try again in a minute." }) });
    render(<SkillPage />);
    expect(await screen.findByText("Too many tries; wait a minute and try again.")).toBeInTheDocument();
  });

  it("warns of scripts", async () => {
    const files = [
      { index: 0, path: "SKILL.md", mediaType: "text/markdown", size: 412 },
      { index: 1, path: "scripts/run.py", mediaType: "text/plain; charset=utf-8", size: 90 },
    ];
    stubFetch({ "GET /api/k": () => json(200, skill("Body", files, true)) });
    render(<SkillPage />);
    expect(await screen.findByText("This skill contains scripts. Kumbu² never runs them; a harness you give the skill to may.")).toBeInTheDocument();
  });

  it("downloads the zip with the copy id", async () => {
    const { create, revoke } = blobUrls();
    const clicks: { href: string; download: string }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      clicks.push({ href: this.href, download: this.download });
    });
    const fetch = stubFetch({
      "GET /api/k": () => json(200, skill("Body")),
      "GET /api/k/zip": () => new Response(new Blob(["PK"]), { status: 200, headers: { "Content-Type": "application/zip" } }),
    });
    render(<SkillPage />);

    await userEvent.click(await screen.findByRole("button", { name: "Download as .zip" }));

    await waitFor(() => expect(clicks).toHaveLength(1));
    const zip = fetch.mock.calls.find(([url]) => String(url) === "/api/k/zip")!;
    expect(new Headers(zip[1]?.headers).get("X-Kumbu-Copy")).toBe(copyId);
    expect(zip[1]?.credentials).toBe("same-origin");
    expect(create).toHaveBeenCalledTimes(1);
    expect(clicks[0]).toEqual({ href: "blob:http://localhost/0", download: "write-as-alice.zip" });
    await waitFor(() => expect(revoke).toHaveBeenCalledWith("blob:http://localhost/0"), { timeout: 2000 });
  });

  it("says when the zip can't be downloaded", async () => {
    stubFetch({ "GET /api/k": () => json(200, skill("Body")), "GET /api/k/zip": none });
    render(<SkillPage />);
    await userEvent.click(await screen.findByRole("button", { name: "Download as .zip" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The .zip couldn't be downloaded; open the link again and retry.");
  });

  it("links Add to my skills with the code in the fragment", async () => {
    history.replaceState(null, "", `/k#${code}`);
    stubFetch({ "POST /api/k": () => json(200, skill("Body")) });
    render(<SkillPage />);

    const add = await screen.findByRole("link", { name: "Add to my skills" });
    expect(add).toHaveAttribute("href", `/skills/add#${code}`);
    expect(screen.getByText("Needs an account on this server.")).toBeInTheDocument();
  });

  it("asks nothing of the signed-in app", async () => {
    history.replaceState(null, "", `/k#${code}`);
    blobUrls();
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    const fetch = stubFetch({
      "POST /api/k": () => json(200, skill("# Writing\n\n[elsewhere](https://kumbu.example.org/notes)")),
      "GET /api/k/zip": () => new Response(new Blob(["PK"]), { status: 200 }),
    });
    render(<SkillPage />);
    await userEvent.click(await screen.findByRole("button", { name: "Download as .zip" }));
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2));

    expect(urls(fetch).every((url) => url === "/api/k" || url.startsWith("/api/k/"))).toBe(true);
  });

  it("renders no raw HTML from SKILL.md", async () => {
    const manifest = '<img src=x onerror=alert(1)>\n\n<a href="https://example.org">raw</a>\n\n[x](javascript:alert(1))\n\n[site](https://kumbu.example.org/)';
    stubFetch({ "GET /api/k": () => json(200, skill(manifest)) });
    const { container } = render(<SkillPage />);

    expect(await screen.findByRole("heading", { name: "write-as-alice" })).toBeInTheDocument();
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector("[onerror]")).toBeNull();
    expect(screen.queryByRole("link", { name: "raw" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "x" })).not.toBeInTheDocument();
    const site = screen.getByRole("link", { name: "site" });
    expect(site).toHaveAttribute("target", "_blank");
    expect(site).toHaveAttribute("rel", "noreferrer noopener");
  });

  it("shows images as their alt text and fetches none", async () => {
    const fetch = stubFetch({ "GET /api/k": () => json(200, skill("Look:\n\n![a map](https://kumbu.example.org/x.png)\n\n![](f/0)")) });
    const { container } = render(<SkillPage />);

    expect(await screen.findByText("a map")).toBeInTheDocument();
    expect(container.querySelector("img")).toBeNull();
    await new Promise((r) => setTimeout(r, 20));
    expect(urls(fetch)).toEqual(["/api/k"]);
  });
});
