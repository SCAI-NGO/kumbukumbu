// SPDX-License-Identifier: AGPL-3.0-or-later
import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi } from "../test/app";
import { useAttachmentUrl } from "./notes";

const { createObjectURL, revokeObjectURL } = URL;

/** jsdom has no object URLs: these stand in, and go again after each test. */
function objectUrls() {
  const create = vi.fn((_: Blob) => "blob:shot");
  const revoke = vi.fn();
  URL.createObjectURL = create;
  URL.revokeObjectURL = revoke;
  return { create, revoke };
}

afterEach(() => {
  vi.unstubAllGlobals();
  URL.createObjectURL = createObjectURL;
  URL.revokeObjectURL = revokeObjectURL;
});

describe("useAttachmentUrl", () => {
  it("fetches the bytes with the session into an object URL and revokes it on unmount", async () => {
    const fetch = fakeApi({ "GET /api/files/My%20Vault/a%20b/shot.png": () => new Response(new Uint8Array([0x89, 0x50]), { headers: { "Content-Type": "image/png" } }) });
    const { create, revoke } = objectUrls();

    const { result, unmount } = renderHook(() => useAttachmentUrl("My Vault", "a b/shot.png", "v1"));
    expect(result.current).toBeUndefined();
    await waitFor(() => expect(result.current).toBe("blob:shot"));

    const request = fetch.mock.calls[0][0] as Request;
    expect(new URL(request.url).pathname).toBe("/api/files/My%20Vault/a%20b/shot.png");
    expect(new URL(request.url).search).toBe("?version=v1");
    expect(request.credentials).toBe("include");
    expect(await create.mock.calls[0][0].arrayBuffer()).toEqual(new Uint8Array([0x89, 0x50]).buffer);

    expect(revoke).not.toHaveBeenCalled();
    unmount();
    expect(revoke).toHaveBeenCalledWith("blob:shot");
  });

  it("never answers the previous file's revoked URL after a change of file", async () => {
    fakeApi({
      "GET /api/files/W/one.png": () => new Response(new Uint8Array([1]), { headers: { "Content-Type": "image/png" } }),
      "GET /api/files/W/two.png": () => new Promise<Response>(() => {}), // still loading
    });
    let n = 0;
    const create = vi.fn((_: Blob) => `blob:${++n}`);
    const revoke = vi.fn();
    URL.createObjectURL = create;
    URL.revokeObjectURL = revoke;

    // Every render's answer: the one right after the change, before any effect has run, counts too.
    const seen: [string, string | null | undefined][] = [];
    const { result, rerender, unmount } = renderHook(
      ({ path }) => {
        const url = useAttachmentUrl("W", path);
        seen.push([path, url]);
        return url;
      },
      { initialProps: { path: "one.png" } },
    );
    await waitFor(() => expect(result.current).toBe("blob:1"));
    rerender({ path: "two.png" });
    expect(revoke).toHaveBeenCalledWith("blob:1");
    expect(seen.some(([path]) => path === "two.png")).toBe(true);
    expect(seen.filter(([path, url]) => path === "two.png" && url !== undefined)).toEqual([]);

    // Back to the first while the second is still loading: its old URL is gone, a new one comes.
    seen.length = 0;
    rerender({ path: "one.png" });
    expect(seen.filter(([, url]) => url === "blob:1")).toEqual([]);
    await waitFor(() => expect(result.current).toBe("blob:2"));
    unmount();
    expect(revoke).toHaveBeenCalledWith("blob:2");
  });

  it("answers no URL for a file that can't be read", async () => {
    const fetch = fakeApi({ "GET /api/files/W/gone.png": () => new Response(JSON.stringify({ error: "No such file" }), { status: 404, headers: { "Content-Type": "application/json" } }) });
    const { create } = objectUrls();
    const { result } = renderHook(() => useAttachmentUrl("W", "gone.png"));
    expect(result.current).toBeUndefined();
    await waitFor(() => expect(result.current).toBeNull());
    expect(fetch).toHaveBeenCalled();
    expect(create).not.toHaveBeenCalled();
  });
});
