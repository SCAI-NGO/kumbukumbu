// SPDX-License-Identifier: AGPL-3.0-or-later
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { StrictMode } from "react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Reader from "./Reader";
import { snapshotText } from "./snapshot";

const code = "K7QM-4XDA-9PRT-2VNB-6HJC-8EWS-3FGY-5KZD";

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const copyId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

const copy = (markdown: string, files: { index: number; name: string; mediaType: string }[] = []) => ({
  title: "Questions on routing",
  markdown,
  snapshotAt: "2026-11-05T14:32:00Z",
  files,
  copy: copyId,
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

/** jsdom has no blob URLs: they are stubbed, and spied on. */
function blobUrls() {
  for (const name of ["createObjectURL", "revokeObjectURL"] as const) {
    if (!(name in URL)) Object.defineProperty(URL, name, { value: () => undefined, configurable: true, writable: true });
  }
  let n = 0;
  return {
    create: vi.spyOn(URL, "createObjectURL").mockImplementation(() => `blob:http://localhost/${n++}`),
    revoke: vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined),
  };
}

const deferred = () => {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>((r) => (resolve = r));
  return { promise, resolve };
};

const filesFetched = (fetch: ReturnType<typeof stubFetch>, index: number) =>
  fetch.mock.calls.filter(([url]) => new URL(String(url), location.href).pathname === `/api/s/files/${index}`).length;

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

  it("sends one request from the fragment under StrictMode", async () => {
    history.replaceState(null, "", `/s#${code}`);
    const fetch = stubFetch({ "POST /api/s": () => json(200, copy("Which way is shorter?")) });

    render(
      <StrictMode>
        <Reader />
      </StrictMode>,
    );

    expect(await screen.findByRole("heading", { name: "Questions on routing" })).toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch.mock.calls[0][1]?.method).toBe("POST");
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

    const input = await screen.findByLabelText("Enter the code you were given");
    expect(input).toHaveAttribute("autocapitalize", "characters");
    expect(input).toHaveAttribute("autocorrect", "off");
    expect(input).toHaveAttribute("spellcheck", "false");
    expect(input).not.toHaveAttribute("aria-invalid", "true");
    await userEvent.type(input, "wrong");
    await userEvent.click(screen.getByRole("button", { name: "Read" }));
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("This code doesn't open a published note.");
    expect(screen.getByLabelText("Enter the code you were given")).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByLabelText("Enter the code you were given")).toHaveAttribute("aria-describedby", alert.id);
    expect(alert.id).not.toBe("");

    await userEvent.clear(screen.getByLabelText("Enter the code you were given"));
    await userEvent.type(screen.getByLabelText("Enter the code you were given"), code);
    await userEvent.click(screen.getByRole("button", { name: "Read" }));
    expect(await screen.findByRole("heading", { name: "Questions on routing" })).toBeInTheDocument();
    expect(posted).toEqual([{ code: "wrong" }, { code }]);
  });

  it("waits while a code is read, and keeps only the latest answer", async () => {
    const answers = [deferred(), deferred()];
    let posts = 0;
    stubFetch({ "GET /api/s": none, "POST /api/s": () => answers[posts++].promise });
    render(<Reader />);

    const input = await screen.findByLabelText("Enter the code you were given");
    await userEvent.type(input, "wrong");
    await userEvent.click(screen.getByRole("button", { name: "Read" }));
    await waitFor(() => expect(posts).toBe(1));
    expect(screen.getByRole("button", { name: "Read" })).toBeDisabled();
    expect(screen.getByRole("form", { name: "Read a published note" })).toHaveAttribute("aria-busy", "true");

    // The button waits; a form can still be sent another way, and then only the latest answer counts.
    await userEvent.clear(input);
    await userEvent.type(input, code);
    fireEvent.submit(screen.getByRole("form", { name: "Read a published note" }));
    await waitFor(() => expect(posts).toBe(2));

    answers[1].resolve(json(200, copy("Which way is shorter?")));
    expect(await screen.findByRole("heading", { name: "Questions on routing" })).toBeInTheDocument();
    answers[0].resolve(none());
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.getByRole("heading", { name: "Questions on routing" })).toBeInTheDocument();
    expect(screen.queryByLabelText("Enter the code you were given")).not.toBeInTheDocument();
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
    // Skipped, not shown as text either.
    expect(container.textContent).not.toMatch(/onerror|<a /);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("shows copied images from blob urls", async () => {
    const urls = blobUrls();
    const fetch = stubFetch({
      "GET /api/s": () => json(200, copy("![flow.jpg](f/0)", [{ index: 0, name: "flow.jpg", mediaType: "image/jpeg" }])),
      "GET /api/s/files/0": () => new Response(new Uint8Array([0xff, 0xd8, 0xff]), { status: 200, headers: { "Content-Type": "image/jpeg" } }),
    });
    const { unmount } = render(<Reader />);

    const img = await screen.findByRole("img", { name: "flow.jpg" });
    expect(img.getAttribute("src")).toMatch(/^blob:/);
    expect(filesFetched(fetch, 0)).toBe(1);
    // With the copy's id: the reader cookie may be another tab's copy's by now.
    const init = fetch.mock.calls.find(([url]) => String(url).endsWith("/api/s/files/0"))![1]!;
    expect(new Headers(init.headers).get("X-Kumbu-Copy")).toBe(copyId);
    expect(init.credentials).toBe("same-origin");
    unmount();
    expect(urls.revoke).toHaveBeenCalledWith(img.getAttribute("src"));
  });

  it("fetches each file once when the page renders again", async () => {
    blobUrls();
    const fetch = stubFetch({
      "GET /api/s": () => json(200, copy("![flow.jpg](f/0)", [{ index: 0, name: "flow.jpg", mediaType: "image/jpeg" }])),
      "GET /api/s/files/0": () => new Response(new Uint8Array([0xff, 0xd8, 0xff]), { status: 200, headers: { "Content-Type": "image/jpeg" } }),
    });
    const { rerender } = render(<Reader />);
    await screen.findByRole("img", { name: "flow.jpg" });

    rerender(<Reader />);
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.getByRole("img", { name: "flow.jpg" })).toBeInTheDocument();
    expect(filesFetched(fetch, 0)).toBe(1);
  });

  it("shows an image that does not load as its name", async () => {
    blobUrls();
    stubFetch({
      "GET /api/s": () => json(200, copy("![flow.jpg](f/0)", [{ index: 0, name: "flow.jpg", mediaType: "image/jpeg" }])),
      "GET /api/s/files/0": () => new Response(new Uint8Array([0]), { status: 200, headers: { "Content-Type": "image/jpeg" } }),
    });
    render(<Reader />);

    fireEvent.error(await screen.findByRole("img", { name: "flow.jpg" }));
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    expect(screen.getByText("flow.jpg")).toBeInTheDocument();
  });

  it("keeps the note's headings below the title", async () => {
    stubFetch({ "GET /api/s": () => json(200, copy("# One\n\n## Two\n\n### Three\n\n#### Four\n\n##### Five\n\n###### Six")) });
    render(<Reader />);

    expect(await screen.findByRole("heading", { level: 1 })).toHaveTextContent("Questions on routing");
    expect(screen.getByRole("heading", { name: "One" }).tagName).toBe("H2");
    expect(screen.getByRole("heading", { name: "Two" }).tagName).toBe("H3");
    expect(screen.getByRole("heading", { name: "Three" }).tagName).toBe("H4");
    expect(screen.getByRole("heading", { name: "Four" }).tagName).toBe("H5");
    expect(screen.getByRole("heading", { name: "Five" }).tagName).toBe("H6");
    expect(screen.getByRole("heading", { name: "Six" }).tagName).toBe("H6");
  });

  it("opens web links without a referrer", async () => {
    stubFetch({ "GET /api/s": () => json(200, copy("[the map](https://example.org/map) and [Bob](mailto:bob@example.org)")) });
    render(<Reader />);

    const link = await screen.findByRole("link", { name: "the map" });
    expect(link).toHaveAttribute("href", "https://example.org/map");
    expect(link).toHaveAttribute("target", "_blank");
    expect(link).toHaveAttribute("rel", "noopener noreferrer nofollow");
    const mail = screen.getByRole("link", { name: "Bob" });
    expect(mail).toHaveAttribute("href", "mailto:bob@example.org");
    expect(mail).not.toHaveAttribute("target");
  });
});

describe("snapshotText", () => {
  it("names the snapshot's time", () => {
    expect(snapshotText("2026-11-05T14:32:00Z", "UTC")).toBe("Snapshot of 5 Nov 2026, 14:32");
    expect(snapshotText("2026-11-05T14:32:00Z", "Asia/Tokyo")).toBe("Snapshot of 5 Nov 2026, 23:32");
  });
});
