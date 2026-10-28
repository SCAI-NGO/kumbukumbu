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

  it("answers no URL for a file that can't be read", async () => {
    const fetch = fakeApi({ "GET /api/files/W/gone.png": () => new Response(JSON.stringify({ error: "No such file" }), { status: 404, headers: { "Content-Type": "application/json" } }) });
    const { create } = objectUrls();
    const { result } = renderHook(() => useAttachmentUrl("W", "gone.png"));
    await waitFor(() => expect(fetch).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(result.current).toBeUndefined();
    expect(create).not.toHaveBeenCalled();
  });
});
