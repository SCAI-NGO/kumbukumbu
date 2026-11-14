// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import { useSaveNote } from "./notes";

afterEach(() => vi.unstubAllGlobals());

const PLAN = "/Work/plan.md";
const saved = { path: PLAN, content: "Text.", version: "v2", lastWriter: "ada", lastWriteAt: "2026-11-14T09:00:00Z" };

function saving(answer: () => Response) {
  fakeApi({ [`PUT /api/notes${PLAN}`]: answer });
  const queryClient = new QueryClient();
  const invalidated = vi.spyOn(queryClient, "invalidateQueries");
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  const { result } = renderHook(() => useSaveNote(PLAN), { wrapper });
  const keys = () => invalidated.mock.calls.map(([filters]) => filters?.queryKey);
  return { save: () => act(() => result.current.mutateAsync({ content: "Text.", version: "v1" })), keys };
}

describe("saving a note", () => {
  // The tree carries each note's last change and flag: an ordinary edit changes either.
  it("asks the vault's tree again after a save", async () => {
    const { save, keys } = saving(() => json(200, saved));
    await save();
    expect(keys()).toContainEqual(["tree", "Work"]);
  });

  it("not after a conflict, which wrote nothing", async () => {
    const { save, keys } = saving(() => json(409, { error: "changed", current: saved }));
    await save();
    expect(keys()).not.toContainEqual(["tree", "Work"]);
  });
});
