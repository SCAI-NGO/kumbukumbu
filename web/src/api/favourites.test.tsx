// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { followVaults } from "../discreet/state";
import { fakeApi, json } from "../test/app";
import { favouriteKey, useFavourites } from "./favourites";

const added = "2026-11-18T09:00:00+00:00";
const plan = { kind: "note", vault: "Work", path: "projects/plan.md" } as const;
const palette = { kind: "command", id: "settings.appearance" } as const;

function setup() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  return { queryClient, ...renderHook(() => useFavourites(), { wrapper }) };
}

afterEach(() => vi.unstubAllGlobals());

describe("favouriteKey", () => {
  it("names a favourite by its kind and what it points at", () => {
    expect(favouriteKey(palette)).toBe("command:settings.appearance");
    expect(favouriteKey({ kind: "board", vault: "Work" })).toBe("board:Work");
    expect(favouriteKey(plan)).toBe("note:Work/projects/plan.md");
  });
});

describe("useFavourites", () => {
  it("toggle adds and removes by key", async () => {
    let stored: unknown[] = [{ ...palette, added }];
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: stored }),
      "PUT /api/account/favourites": async (r) => {
        const { favourites } = (await r.json()) as { favourites: unknown[] };
        stored = favourites.map((f) => ({ ...(f as object), added }));
        return json(200, { favourites: stored });
      },
    });
    const { result } = setup();
    await waitFor(() => expect(result.current.has(palette)).toBe(true));
    expect(result.current.has(plan)).toBe(false);

    act(() => result.current.toggle(plan));
    await waitFor(() => expect(result.current.has(plan)).toBe(true));
    expect(result.current.list.map(favouriteKey)).toEqual(["command:settings.appearance", "note:Work/projects/plan.md"]);

    act(() => result.current.toggle(palette));
    await waitFor(() => expect(result.current.has(palette)).toBe(false));
    expect(result.current.list.map(favouriteKey)).toEqual(["note:Work/projects/plan.md"]);

    // Stale keys go in the same change.
    act(() => result.current.toggle(palette, ["note:Work/projects/plan.md"]));
    await waitFor(() => expect(result.current.list.map(favouriteKey)).toEqual(["command:settings.appearance"]));
  });

  it("PUT carries the whole visible list without added", async () => {
    const bodies: unknown[] = [];
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: [{ ...palette, added }, { kind: "vault", vault: "Work", added }] }),
      "PUT /api/account/favourites": async (r) => {
        bodies.push(await r.json());
        return json(200, { favourites: [{ kind: "vault", vault: "Work", added }] });
      },
    });
    const { result } = setup();
    await waitFor(() => expect(result.current.list).toHaveLength(2));
    act(() => result.current.toggle(palette));
    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toEqual({ favourites: [{ kind: "vault", vault: "Work" }] });
  });

  it("rolls back on a failed write", async () => {
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: [{ ...palette, added }] }),
      "PUT /api/account/favourites": () => json(400, { error: "A favourite is an object with a kind." }),
    });
    const { result } = setup();
    await waitFor(() => expect(result.current.has(palette)).toBe(true));
    act(() => result.current.toggle(palette));
    await waitFor(() => expect(result.current.has(palette)).toBe(true));
    act(() => result.current.toggle(plan));
    await waitFor(() => expect(result.current.has(plan)).toBe(false));
    expect(result.current.list.map(favouriteKey)).toEqual(["command:settings.appearance"]);
  });

  it("carries the version the list was read with, and the next write the one the last answered", async () => {
    const versions: unknown[] = [];
    let n = 0;
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: [], version: "v0" }),
      "PUT /api/account/favourites": async (r) => {
        const body = (await r.json()) as { favourites: object[]; version: string };
        versions.push(body.version);
        return json(200, { favourites: body.favourites.map((f) => ({ ...f, added })), version: `v${++n}` });
      },
    });
    const { result } = setup();
    await waitFor(() => expect(result.current.ready).toBe(true));
    act(() => {
      result.current.toggle(plan);
      result.current.toggle(palette);
    });
    await waitFor(() => expect(versions).toEqual(["v0", "v1"]));
  });

  it("after a 409 reads the list again and makes the same change to it", async () => {
    let held: unknown[] = [];
    let version = "v0";
    const bodies: { favourites: unknown[]; version: string }[] = [];
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: held, version }),
      "PUT /api/account/favourites": async (r) => {
        const body = (await r.json()) as { favourites: object[]; version: string };
        bodies.push(body);
        if (body.version !== version) return json(409, { error: "Your favourites changed elsewhere; try again." });
        held = body.favourites.map((f) => ({ ...f, added }));
        version = `${version}+`;
        return json(200, { favourites: held, version });
      },
    });
    const { result } = setup();
    await waitFor(() => expect(result.current.ready).toBe(true));
    // Another device stars the palette meanwhile.
    held = [{ ...palette, added }];
    version = "v1";
    act(() => result.current.toggle(plan));
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1]).toEqual({ favourites: [palette, plan], version: "v1" });
    await waitFor(() => expect(result.current.list.map(favouriteKey)).toEqual(["command:settings.appearance", "note:Work/projects/plan.md"]));
    expect(result.current.error).toBeNull();
  });

  it("gives up after a second 409, says so and reads again", async () => {
    let reads = 0;
    const put = vi.fn(() => json(409, { error: "Your favourites changed elsewhere; try again." }));
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: reads++ === 0 ? [] : [{ ...palette, added }], version: `v${reads}` }),
      "PUT /api/account/favourites": put,
    });
    const { result } = setup();
    await waitFor(() => expect(reads).toBe(1));
    act(() => result.current.toggle(plan));
    await waitFor(() => expect(result.current.error).toBe("Your favourites changed elsewhere; try again."));
    expect(put).toHaveBeenCalledTimes(2);
    await waitFor(() => expect(result.current.list.map(favouriteKey)).toEqual(["command:settings.appearance"]));
  });

  it("says why a write failed, until the next change", async () => {
    let puts = 0;
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: [], version: "v0" }),
      "PUT /api/account/favourites": async (r) => {
        const body = (await r.json()) as { favourites: object[] };
        return puts++ === 0 ? json(400, { error: "Your favourites are full (200)." }) : json(200, { favourites: body.favourites.map((f) => ({ ...f, added })), version: "v1" });
      },
    });
    const { result } = setup();
    await waitFor(() => expect(result.current.ready).toBe(true));
    expect(result.current.error).toBeNull();
    act(() => result.current.toggle(plan));
    await waitFor(() => expect(result.current.error).toBe("Your favourites are full (200)."));
    act(() => result.current.toggle(palette));
    await waitFor(() => expect(result.current.error).toBeNull());
  });

  it("refetches when the vaults change", async () => {
    let reads = 0;
    fakeApi({ "GET /api/account/favourites": () => json(200, { favourites: reads++ === 0 ? [] : [{ kind: "vault", vault: "Secret", added }] }) });
    const { result, queryClient } = setup();
    const stop = followVaults(queryClient);
    await waitFor(() => expect(reads).toBe(1));
    act(() => queryClient.setQueryData(["vaults"], [{ name: "Secret", discreet: true }]));
    await waitFor(() => expect(result.current.has({ kind: "vault", vault: "Secret" })).toBe(true));
    expect(reads).toBe(2);
    stop();
  });

  it("changes nothing until the list has been read", async () => {
    let answer: () => void = () => {};
    const put = vi.fn(() => json(200, { favourites: [] }));
    fakeApi({
      "GET /api/account/favourites": () => new Promise<Response>((r) => (answer = () => r(json(200, { favourites: [{ ...palette, added }] })))),
      "PUT /api/account/favourites": put,
    });
    const { result } = setup();
    expect(result.current.ready).toBe(false);
    act(() => result.current.toggle(plan));
    await act(async () => {});
    expect(put).not.toHaveBeenCalled();
    act(() => answer());
    await waitFor(() => expect(result.current.ready).toBe(true));
    expect(result.current.list.map(favouriteKey)).toEqual(["command:settings.appearance"]);
  });

  it("two toggles in a row send both", async () => {
    const bodies: { favourites: unknown[] }[] = [];
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: [] }),
      "PUT /api/account/favourites": async (r) => {
        const body = (await r.json()) as { favourites: object[] };
        bodies.push(body);
        return json(200, { favourites: body.favourites.map((f) => ({ ...f, added })) });
      },
    });
    const { result } = setup();
    await waitFor(() => expect(result.current.ready).toBe(true));
    act(() => {
      result.current.toggle(plan);
      result.current.toggle(palette);
    });
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1]).toEqual({ favourites: [plan, palette] });
    await waitFor(() => expect(result.current.list).toHaveLength(2));
  });

  it("keeps no favourites cached when the vaults change", async () => {
    fakeApi({ "GET /api/account/favourites": () => json(200, { favourites: [{ kind: "vault", vault: "Secret", added }] }) });
    const { result, queryClient, unmount } = setup();
    await waitFor(() => expect(result.current.ready).toBe(true));
    unmount();
    expect(queryClient.getQueryData(["favourites"])).toBeDefined();
    const stop = followVaults(queryClient);
    act(() => queryClient.setQueryData(["vaults"], []));
    expect(queryClient.getQueryData(["favourites"])).toBeUndefined();
    stop();
  });

  it("reads again once every write has failed", async () => {
    let reads = 0;
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: reads++ === 0 ? [] : [{ ...palette, added }] }),
      "PUT /api/account/favourites": () => json(400, { error: "No." }),
    });
    const { result } = setup();
    await waitFor(() => expect(result.current.ready).toBe(true));
    act(() => {
      result.current.toggle(plan);
      result.current.toggle({ kind: "vault", vault: "Work" });
    });
    await waitFor(() => expect(reads).toBe(2));
    await waitFor(() => expect(result.current.list.map(favouriteKey)).toEqual(["command:settings.appearance"]));
  });

  it("ends with the answer of the write that succeeded after one that failed", async () => {
    let puts = 0;
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: [] }),
      "PUT /api/account/favourites": async (r) => {
        const body = (await r.json()) as { favourites: object[] };
        return puts++ === 0 ? json(400, { error: "No." }) : json(200, { favourites: body.favourites.map((f) => ({ ...f, added })) });
      },
    });
    const { result } = setup();
    await waitFor(() => expect(result.current.ready).toBe(true));
    act(() => {
      result.current.toggle(plan);
      result.current.toggle(palette);
    });
    await waitFor(() => expect(puts).toBe(2));
    await waitFor(() => expect(result.current.list.map(favouriteKey)).toEqual(["note:Work/projects/plan.md", "command:settings.appearance"]));
  });

  it("does not write an answer into a cache that was cleared meanwhile", async () => {
    let release: () => void = () => {};
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: [] }),
      "PUT /api/account/favourites": () => new Promise<Response>((r) => (release = () => r(json(200, { favourites: [{ ...plan, added }] })))),
    });
    const { result, queryClient, unmount } = setup();
    await waitFor(() => expect(result.current.ready).toBe(true));
    act(() => result.current.toggle(plan));
    unmount();
    await act(() => queryClient.resetQueries({ queryKey: ["favourites"] }));
    await act(async () => {
      release();
    });
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(queryClient.getQueryData(["favourites"])).toBeUndefined();
  });

  it("ignores the answer to a write sent before the list was read again for other vaults", async () => {
    let release: () => void = () => {};
    let reads = 0;
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: reads++ === 0 ? [] : [{ kind: "vault", vault: "Secret", added }], version: `v${reads}` }),
      "PUT /api/account/favourites": () => new Promise<Response>((r) => (release = () => r(json(200, { favourites: [{ ...plan, added }], version: "late" })))),
    });
    const { result, queryClient } = setup();
    const stop = followVaults(queryClient);
    await waitFor(() => expect(result.current.ready).toBe(true));
    act(() => result.current.toggle(plan));
    await waitFor(() => expect(queryClient.isMutating()).toBe(1));
    act(() => queryClient.setQueryData(["vaults"], [{ name: "Secret", discreet: true }]));
    await waitFor(() => expect(result.current.has({ kind: "vault", vault: "Secret" })).toBe(true));
    await act(async () => {
      release();
    });
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(result.current.list.map(favouriteKey)).toEqual(["vault:Secret"]);
    stop();
  });

  it("sends no second PUT when the vaults change during the read after a 409", async () => {
    let reads = 0;
    let answer: () => void = () => {};
    const put = vi.fn(() => json(409, { error: "Your favourites changed elsewhere; try again." }));
    fakeApi({
      "GET /api/account/favourites": () =>
        ++reads === 2 ? new Promise<Response>((r) => (answer = () => r(json(200, { favourites: [], version: "v2" })))) : json(200, { favourites: [], version: `v${reads}` }),
      "PUT /api/account/favourites": put,
    });
    const { result, queryClient } = setup();
    const stop = followVaults(queryClient);
    await waitFor(() => expect(result.current.ready).toBe(true));
    act(() => result.current.toggle(plan));
    await waitFor(() => expect(reads).toBe(2));
    act(() => queryClient.setQueryData(["vaults"], [{ name: "Secret", discreet: true }]));
    await act(async () => {
      answer();
    });
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(put).toHaveBeenCalledTimes(1);
    stop();
  });

  it("shows no error of a write sent before the vaults changed", async () => {
    let refuse: () => void = () => {};
    fakeApi({
      "GET /api/account/favourites": () => json(200, { favourites: [], version: "v1" }),
      "PUT /api/account/favourites": () => new Promise<Response>((r) => (refuse = () => r(json(400, { error: "There is no vault 'Tagebuch'." })))),
    });
    const { result, queryClient } = setup();
    const stop = followVaults(queryClient);
    await waitFor(() => expect(result.current.ready).toBe(true));
    act(() => result.current.toggle({ kind: "board", vault: "Tagebuch" }));
    await waitFor(() => expect(queryClient.isMutating()).toBe(1));
    act(() => queryClient.setQueryData(["vaults"], []));
    await act(async () => {
      refuse();
    });
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(result.current.error).toBeNull();
    stop();
  });
});
