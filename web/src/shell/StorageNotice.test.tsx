// SPDX-License-Identifier: AGPL-3.0-or-later
import { focusManager, QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, user } from "../test/app";
import StorageNotice from "./StorageNotice";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const MB = 1024 ** 2;
const GB = 1024 ** 3;

const session = (storageLimited: boolean) => () =>
  json(200, { user, previousSignInAt: null, passwordWindowOpen: true, publishEnabled: false, storageLimited });

/** The notice for her own storage at `used` of `limit`; resolves once the figure was asked. */
async function renderAt(used: number, limit: number | null, archive = 0) {
  const fetch = fakeApi({ "GET /api/session": session(true), "GET /api/account/storage": () => json(200, { used, limit, archive }) });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <StorageNotice />
    </QueryClientProvider>,
  );
  await waitFor(() => expect(queryClient.getQueryState(["account", "storage"])?.status).toBe("success"));
  return fetch;
}

const storageAsked = (fetch: ReturnType<typeof fakeApi>) =>
  fetch.mock.calls.filter(([request]) => new URL((request as Request).url).pathname === "/api/account/storage").length;

describe("StorageNotice", () => {
  it("warns the owner from 90 %", async () => {
    // 85 %: nothing yet.
    await renderAt(0.85 * 2 * GB, 2 * GB);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    cleanup();

    await renderAt(1844 * MB, 2 * GB);
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Your storage is 90 % full: 1.8 GB of 2 GB. Empty the trash or ask your admin for more.",
    );
    cleanup();

    await renderAt(2 * GB + 5, 2 * GB);
    expect(await screen.findByRole("status")).toHaveTextContent("Your storage is full (2 GB). Empty the trash or ask your admin for more.");
  });

  it("names the archive along with the trash once it holds a tenth of the used space", async () => {
    await renderAt(1900 * MB, 2 * GB, 190 * MB);
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Your storage is 92 % full: 1.9 GB of 2 GB. Empty the trash or the archive, or ask your admin for more.",
    );
    cleanup();

    await renderAt(1900 * MB, 2 * GB, 171 * MB);
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Your storage is 92 % full: 1.9 GB of 2 GB. Empty the trash or ask your admin for more.",
    );
    cleanup();

    await renderAt(2 * GB, 2 * GB, 0.1 * 2 * GB);
    expect(await screen.findByRole("status")).toHaveTextContent("Your storage is full (2 GB). Empty the trash or the archive, or ask your admin for more.");
  });

  it("says nothing without a limit", async () => {
    await renderAt(50 * GB, null);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("doesn't ask for the storage when the session says no limit applies", async () => {
    // Counting her storage costs the server a read of every document of her vaults; without a limit it buys nothing.
    const fetch = fakeApi({ "GET /api/session": session(false), "GET /api/account/storage": () => json(200, { used: 0, limit: null }) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <StorageNotice />
      </QueryClientProvider>,
    );
    await waitFor(() => expect(queryClient.getQueryState(["session"])?.status).toBe("success"));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(storageAsked(fetch)).toBe(0);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("asks once a minute at most, not on every focus", async () => {
    const fetch = await renderAt(0, 2 * GB);
    focusManager.setFocused(false);
    focusManager.setFocused(true);
    focusManager.setFocused(undefined);
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(storageAsked(fetch)).toBe(1);
  });
});
