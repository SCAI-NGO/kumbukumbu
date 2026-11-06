// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Reader from "./Reader";
import { snapshotText } from "./snapshot";

const code = "K7QM-4XDA-9PRT-2VNB-6HJC-8EWS-3FGY-5KZD";

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const copy = (markdown: string, files: { index: number; name: string; mediaType: string }[] = []) => ({
  title: "Questions on routing",
  markdown,
  snapshotAt: "2026-11-05T14:32:00Z",
  files,
});

const none = () => json(404, { error: "This link doesn't open a published note: the code is wrong, or the note is no longer published." });

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

beforeEach(() => {
  process.env.TZ = "UTC";
  history.replaceState(null, "", "/s");
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("Reader", () => {
  it("posts the code from the fragment and removes it", async () => {
    history.replaceState(null, "", `/s#${code}`);
    const replaceState = vi.spyOn(history, "replaceState");
    let answer!: (response: Response) => void;
    const fetch = stubFetch({ "POST /api/s": () => new Promise<Response>((resolve) => (answer = resolve)) });

    render(<Reader />);

    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));
    // Gone from the address bar before the answer is in, and before the request went out.
    expect(replaceState).toHaveBeenCalledWith(null, "", "/s");
    expect(replaceState.mock.invocationCallOrder[0]).toBeLessThan(fetch.mock.invocationCallOrder[0]);
    expect(location.hash).toBe("");
    const init = fetch.mock.calls[0][1]!;
    expect(init.body).toBe(JSON.stringify({ code }));
    expect(init.credentials).toBe("same-origin");

    answer(json(200, copy("Which way is shorter?")));
    expect(await screen.findByRole("heading", { name: "Questions on routing" })).toBeInTheDocument();
    expect(screen.getByText("Snapshot of 5 Nov 2026, 14:32")).toBeInTheDocument();
    expect(screen.getByText("Which way is shorter?")).toBeInTheDocument();
  });

  it("reloads with the cookie when there is no fragment", async () => {
    const replaceState = vi.spyOn(history, "replaceState");
    const fetch = stubFetch({ "GET /api/s": () => json(200, copy("Which way is shorter?")) });

    const { unmount } = render(<Reader />);

    expect(await screen.findByRole("heading", { name: "Questions on routing" })).toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch.mock.calls[0][1]?.credentials).toBe("same-origin");
    expect(replaceState).not.toHaveBeenCalled();
    unmount();

    stubFetch({ "GET /api/s": none });
    render(<Reader />);
    expect(await screen.findByLabelText("Enter the code you were given")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Read" })).toBeInTheDocument();
    expect(screen.queryByText("This code doesn't open a published note.")).not.toBeInTheDocument();
  });

  it("asks again after a wrong code", async () => {
    const posted: unknown[] = [];
    stubFetch({
      "GET /api/s": none,
      "POST /api/s": (init) => {
        posted.push(JSON.parse(String(init?.body)));
        return posted.length === 1 ? none() : json(200, copy("Which way is shorter?"));
      },
    });
    render(<Reader />);

    await userEvent.type(await screen.findByLabelText("Enter the code you were given"), "wrong");
    await userEvent.click(screen.getByRole("button", { name: "Read" }));
    expect(await screen.findByText("This code doesn't open a published note.")).toBeInTheDocument();
    expect(screen.getByLabelText("Enter the code you were given")).toBeInTheDocument();

    await userEvent.clear(screen.getByLabelText("Enter the code you were given"));
    await userEvent.type(screen.getByLabelText("Enter the code you were given"), code);
    await userEvent.click(screen.getByRole("button", { name: "Read" }));
    expect(await screen.findByRole("heading", { name: "Questions on routing" })).toBeInTheDocument();
    expect(posted).toEqual([{ code: "wrong" }, { code }]);
  });

  it("says when the server reads no published notes, and when there were too many tries", async () => {
    stubFetch({ "GET /api/s": () => json(404, { error: "Published notes can't be read on this server.", type: "publishing-off" }) });
    const { unmount } = render(<Reader />);
    expect(await screen.findByText("Published notes can't be read on this server.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Enter the code you were given")).not.toBeInTheDocument();
    unmount();

    stubFetch({ "GET /api/s": () => json(429, { error: "Too many attempts; try again in a minute." }) });
    render(<Reader />);
    expect(await screen.findByText("Too many tries; wait a minute and try again.")).toBeInTheDocument();
  });

  it("renders no raw HTML and no dangerous links", async () => {
    const markdown = "<img src=x onerror=alert(1)>\n\n[x](javascript:alert(1))\n\n![a](https://example.org/t.png)\n\n<a href=\"https://example.org\">raw</a>\n\n![b](f/7)";
    const fetch = stubFetch({ "GET /api/s": () => json(200, copy(markdown)) });
    const { container } = render(<Reader />);

    expect(await screen.findByRole("heading", { name: "Questions on routing" })).toBeInTheDocument();
    expect(screen.getByText("x")).toBeInTheDocument();
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector('a[href^="javascript"]')).toBeNull();
    expect(container.querySelector("a")).toBeNull();
    expect(container.querySelector("[onerror]")).toBeNull();
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("shows copied images from blob urls", async () => {
    const createObjectURL = vi.fn(() => "blob:http://localhost/0f1e");
    const revokeObjectURL = vi.fn();
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL, revokeObjectURL }));
    const fetch = stubFetch({
      "GET /api/s": () => json(200, copy("![flow.jpg](f/0)", [{ index: 0, name: "flow.jpg", mediaType: "image/jpeg" }])),
      "GET /api/s/files/0": () => new Response(new Uint8Array([0xff, 0xd8, 0xff]), { status: 200, headers: { "Content-Type": "image/jpeg" } }),
    });
    const { unmount } = render(<Reader />);

    const img = await screen.findByRole("img", { name: "flow.jpg" });
    expect(img.getAttribute("src")).toMatch(/^blob:/);
    expect(fetch.mock.calls.filter(([url]) => new URL(String(url), location.href).pathname === "/api/s/files/0")).toHaveLength(1);
    unmount();
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:http://localhost/0f1e");
  });

  it("opens web links without a referrer", async () => {
    stubFetch({ "GET /api/s": () => json(200, copy("[the map](https://example.org/map) and [Bob](mailto:bob@example.org)")) });
    render(<Reader />);

    const link = await screen.findByRole("link", { name: "the map" });
    expect(link).toHaveAttribute("href", "https://example.org/map");
    expect(link).toHaveAttribute("target", "_blank");
    expect(link).toHaveAttribute("rel", "noopener noreferrer nofollow");
    expect(screen.getByRole("link", { name: "Bob" })).toHaveAttribute("href", "mailto:bob@example.org");
  });
});

describe("snapshotText", () => {
  it("names the snapshot's time", () => {
    expect(snapshotText("2026-11-05T14:32:00Z", "UTC")).toBe("Snapshot of 5 Nov 2026, 14:32");
    expect(snapshotText("2026-11-05T14:32:00Z", "Asia/Tokyo")).toBe("Snapshot of 5 Nov 2026, 23:32");
  });
});
