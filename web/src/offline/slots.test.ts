// SPDX-License-Identifier: AGPL-3.0-or-later
import { AuthError, OfflineError, RejectedError } from "eidolon-core/engine/ports";
import { afterEach, describe, expect, test, vi } from "vitest";
import { mirrorSlots } from "./slots";

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

afterEach(() => vi.unstubAllGlobals());

describe("the mirror slot port", () => {
  test("makes contact with the token and the changes waiting", async () => {
    const fetch = vi.fn().mockResolvedValue(json(200, { id: "s1", key: "k", vaults: [] }));
    vi.stubGlobal("fetch", fetch);
    expect(await mirrorSlots.contact("tok", 3)).toEqual({ id: "s1", key: "k", vaults: [] });
    expect(fetch).toHaveBeenCalledWith("/api/account/mirror-slots/contact", expect.objectContaining({ method: "POST", body: JSON.stringify({ token: "tok", waiting: 3 }) }));
  });

  test("leaves the count out when it has none, so the server records that it doesn't know", async () => {
    const fetch = vi.fn().mockResolvedValue(json(200, { id: "s1", key: "k", vaults: [] }));
    vi.stubGlobal("fetch", fetch);
    await mirrorSlots.contact("tok", null);
    expect(fetch).toHaveBeenCalledWith("/api/account/mirror-slots/contact", expect.objectContaining({ body: JSON.stringify({ token: "tok" }) }));
  });

  test("hears the wipe signal as gone, and every other failure as the core's errors", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(404, { error: "removed", code: "mirror-wipe" })));
    expect(await mirrorSlots.contact("tok", 0)).toBe("gone");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(401, { error: "Sign in." })));
    await expect(mirrorSlots.contact("tok", 0)).rejects.toBeInstanceOf(AuthError);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(503, { error: "busy" })));
    await expect(mirrorSlots.contact("tok", 0)).rejects.toBeInstanceOf(OfflineError);
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("Failed to fetch")));
    await expect(mirrorSlots.create("Firefox on Linux")).rejects.toBeInstanceOf(OfflineError);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(409, { error: "At most 20 offline copies." })));
    await expect(mirrorSlots.create("Firefox on Linux")).rejects.toThrow(new RejectedError("At most 20 offline copies.", 409));
  });
});
