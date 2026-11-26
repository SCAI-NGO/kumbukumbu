// SPDX-License-Identifier: AGPL-3.0-or-later
import "fake-indexeddb/auto";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useOfflineCopy } from "../offline/copy";
import { enginesReconnected } from "../offline/engines";
import { turnOff, turnOn } from "../offline/turnOn";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, user } from "../test/app";
import { useMarkDiscreet } from "./api";

vi.mock("../offline/engines", async (actual) => ({ ...(await actual<typeof import("../offline/engines")>()), enginesReconnected: vi.fn() }));

const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
const work = { id: "v-work", tag: "t1", canWrite: true, pending: false };
const home = { id: "v-home", tag: "t2", canWrite: true, pending: false };

// jsdom has no Web Locks: one tab, so a lock is only a queue.
let tail: Promise<unknown> = Promise.resolve();
const locks = {
  request: (_name: string, ...rest: unknown[]) => {
    const fn = rest[rest.length - 1] as () => Promise<unknown>;
    const run = tail.then(fn, fn);
    tail = run.catch(() => {});
    return run;
  },
};

/** The app's fake API, also for the store's requests, which fetch with a URL and options rather than a Request. */
function api(listed: () => (typeof work)[]) {
  const fake = fakeApi({
    "POST /api/session/confirm": () => noContent(),
    "POST /api/account/mirror-slots": () => json(201, { id: "s1", token: "tok", key, vaults: listed() }),
    "POST /api/account/mirror-slots/contact": () => json(200, { id: "s1", key, vaults: listed() }),
    "DELETE /api/account/mirror-slots/*": () => noContent(),
    "PUT /api/account/discreet/vaults/*": () => noContent(),
  });
  vi.stubGlobal("fetch", (input: Request | string, init?: RequestInit) =>
    fake(typeof input === "string" ? new Request(new URL(input, window.location.origin), init) : input));
  return fake;
}

const asked = (fake: ReturnType<typeof fakeApi>) => fake.mock.calls.map(([r]) => `${(r as Request).method} ${new URL((r as Request).url).pathname}`);

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  Object.defineProperty(navigator, "locks", { value: locks, configurable: true });
  useSession.getState().signedIn({ user, previousSignInAt: null });
});

afterEach(async () => {
  const header = useOfflineCopy.getState().header;
  if (header) await turnOff(header);
  vi.unstubAllGlobals();
  vi.mocked(enginesReconnected).mockClear();
  useSession.getState().reset();
});

describe("marking a vault discreet", () => {
  it("marking a vault discreet makes contact and the vault leaves the copy", { timeout: 20_000 }, async () => {
    let discreet = false;
    const fake = api(() => (discreet ? [work] : [work, home]));
    await turnOn(user, "correct horse");
    const mirror = useOfflineCopy.getState().mirror!;
    await mirror.adopt(work.id);
    await mirror.adopt(home.id);
    expect(mirror.vault(home.id)).toBeDefined();

    const { result } = renderHook(() => useMarkDiscreet(), { wrapper });
    discreet = true;
    await result.current.mutateAsync({ vault: "Home", discreet: true });

    await waitFor(() => expect(mirror.vault(home.id)).toBeUndefined());
    expect(mirror.vault(work.id)).toBeDefined();
    expect(asked(fake).filter((a) => a === "POST /api/account/mirror-slots/contact")).toHaveLength(1);
    expect(enginesReconnected).toHaveBeenCalledTimes(1);
  });

  it("making a vault ordinary again asks the slot nothing", async () => {
    const fake = api(() => [work]);
    const { result } = renderHook(() => useMarkDiscreet(), { wrapper });
    await result.current.mutateAsync({ vault: "Home", discreet: false });
    expect(asked(fake)).toEqual(["PUT /api/account/discreet/vaults/Home"]);
    expect(enginesReconnected).not.toHaveBeenCalled();
  });
});
