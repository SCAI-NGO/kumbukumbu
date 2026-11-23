// SPDX-License-Identifier: AGPL-3.0-or-later
// The note hooks while offline writes are on (offline mode design §3, work package 7): into the copy, not to the server.
import "fake-indexeddb/auto";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { versionOf } from "eidolon-core/engine/version";
import { afterEach, expect, it, vi } from "vitest";
import { offlineCopy } from "../offline/testCopy";
import { setOfflineWrites } from "../offline/writing";
import { useCreateNote, useCreateUntitled, useDeleteNote, useSaveNote } from "./notes";

afterEach(() => {
  setOfflineWrites(null);
  vi.unstubAllGlobals();
});

async function offline() {
  const copy = await offlineCopy();
  await copy.vaults.Work!.files().writeIf("plan.md", null, "one");
  const fetched = vi.fn(() => Promise.reject(new TypeError("offline")));
  vi.stubGlobal("fetch", fetched);
  const queryClient = new QueryClient();
  queryClient.setQueryData(["note", "/Work/plan.md"], { path: "/Work/plan.md", content: "one", version: await versionOf("one"), lastWriter: "ada" });
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  return { ...copy, fetched, queryClient, wrapper };
}

it("a save goes into the copy, and the note's cache takes the new version", async () => {
  const { vaults, fetched, queryClient, wrapper } = await offline();
  const { result } = renderHook(() => useSaveNote("/Work/plan.md"), { wrapper });
  const answer = await act(async () => result.current.mutateAsync({ content: "two", version: await versionOf("one") }));
  expect(answer).toMatchObject({ saved: { path: "/Work/plan.md", content: "two", version: await versionOf("two"), lastWriter: "ada" } });
  expect(await vaults.Work!.files().read("plan.md")).toBe("two");
  expect(queryClient.getQueryData(["note", "/Work/plan.md"])).toMatchObject({ content: "two" });
  expect(fetched).not.toHaveBeenCalled();
});

it("new notes and trashing too", async () => {
  const { vaults, fetched, wrapper } = await offline();
  const create = renderHook(() => useCreateNote(), { wrapper }).result;
  const untitled = renderHook(() => useCreateUntitled(), { wrapper }).result;
  const remove = renderHook(() => useDeleteNote(), { wrapper }).result;
  expect(await act(() => create.current.mutateAsync("/Work/Missing link.md"))).toBe("/Work/Missing link.md");
  // A link's target that is there already is simply opened.
  expect(await act(() => create.current.mutateAsync("/Work/plan.md"))).toBe("/Work/plan.md");
  expect(await act(() => untitled.current.mutateAsync("/Work"))).toBe("/Work/Untitled.md");
  expect(await act(() => remove.current.mutateAsync("/Work/plan.md"))).toBe("/Work/Untitled.md");
  expect(vaults.Work!.list().filter((e) => e.local).map((e) => e.path).sort()).toEqual(["Missing link.md", "Untitled.md"]);
  expect(fetched).not.toHaveBeenCalled();
});
