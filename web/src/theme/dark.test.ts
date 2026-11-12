// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { useDarkScheme } from "./dark";
import { schemes } from "./schemes";

afterEach(() => {
  delete document.documentElement.dataset.scheme;
});

describe("useDarkScheme", () => {
  it("useDarkScheme follows data-scheme", async () => {
    document.documentElement.dataset.scheme = schemes.find((s) => s.light)!.id;
    const { result } = renderHook(() => useDarkScheme());
    expect(result.current).toBe(false);
    await act(async () => {
      document.documentElement.dataset.scheme = schemes.find((s) => !s.light)!.id;
    });
    expect(result.current).toBe(true);
  });
});
