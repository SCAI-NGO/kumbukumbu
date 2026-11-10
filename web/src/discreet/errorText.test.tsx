// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { NO_SUCH_NOTE, useErrorTextFor } from "./errorText";
import { useDiscreet } from "./state";

afterEach(() => {
  vi.unstubAllGlobals();
  useDiscreet.getState().reset();
  useDiscreet.setState({ gone: new Set() });
});

const missing = new HttpError(404, "There is no note at /Tagebuch/Fehlt.md (if it was removed, it is in the trash).");
const heute = new HttpError(404, "There is no note at /Tagebuch/Heute.md (if it was removed, it is in the trash).");

/**
 * The hook for `path`, with the vault list `vaults` in the cache; without it, the list isn't in yet. `fetch` records
 * what is asked, and never answers.
 */
function hook(path: string, vaults?: { name: string }[]) {
  const fetch = vi.fn(() => new Promise<Response>(() => {}));
  vi.stubGlobal("fetch", fetch);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (vaults) queryClient.setQueryData(["vaults"], vaults);
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  return { errorFor: renderHook(() => useErrorTextFor(path), { wrapper }).result.current, fetch };
}

const text = (path: string, error: unknown, vaults?: { name: string }[]) => hook(path, vaults).errorFor(error);

describe("the error text of a note's request", () => {
  it("is the sentence without its path for a 404 in a discreet vault, shown or hidden again", () => {
    useDiscreet.setState({ names: new Set(["Tagebuch"]) });
    expect(text("/Tagebuch/Fehlt.md", missing, [{ name: "Tagebuch" }])).toBe(NO_SUCH_NOTE);
    useDiscreet.setState({ names: new Set(), gone: new Set(["Tagebuch"]) });
    expect(text("/Tagebuch/Fehlt.md", missing, [{ name: "Tagebuch" }])).toBe(NO_SUCH_NOTE);
  });

  it("is the server's sentence for an ordinary vault, and for anything but a 404", () => {
    useDiscreet.setState({ names: new Set(["Tagebuch"]) });
    expect(text("/Work/Fehlt.md", new HttpError(404, "There is no note at /Work/Fehlt.md."), [{ name: "Work" }])).toBe("There is no note at /Work/Fehlt.md.");
    expect(text("/Tagebuch/a.md", new HttpError(409, "The note changed."), [{ name: "Tagebuch" }])).toBe("The note changed.");
  });

  it("is the sentence without its path for a 404 in a vault the list doesn't name (a hidden one after a reload)", () => {
    expect(text("/Tagebuch/Heute.md", heute, [{ name: "Home" }])).toBe(NO_SUCH_NOTE);
  });

  it("is the server's sentence for a 404 in a vault the list names", () => {
    expect(text("/Home/a.md", new HttpError(404, "There is no note at /Home/a.md."), [{ name: "Home" }])).toBe("There is no note at /Home/a.md.");
  });

  it("is today's text for anything but a 404 in a vault the list doesn't name", () => {
    const failed = new HttpError(500, "Internal error at /Tagebuch/Heute.md.");
    expect(text("/Tagebuch/Heute.md", failed, [{ name: "Home" }])).toBe(errorText(failed));
  });

  it("asks nothing of the server: it reads the list the app already has", async () => {
    // A list in the cache, stale at once (the default), and none at all.
    for (const vaults of [[{ name: "Home" }], undefined]) {
      const { fetch } = hook("/Home/a.md", vaults);
      await new Promise((resolve) => setTimeout(resolve, 50));
      expect(fetch).not.toHaveBeenCalled();
    }
  });

  it("is the sentence without its path for a 404 while the list is still loading", () => {
    expect(text("/Tagebuch/Heute.md", heute)).toBe(NO_SUCH_NOTE);
  });
});
