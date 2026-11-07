// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import StorageNotice from "./StorageNotice";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const MB = 1024 ** 2;
const GB = 1024 ** 3;

/** The notice for her own storage at `used` of `limit`; resolves once the figure was asked. */
async function renderAt(used: number, limit: number | null) {
  const fetch = fakeApi({ "GET /api/account/storage": () => json(200, { used, limit }) });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <StorageNotice />
    </QueryClientProvider>,
  );
  await waitFor(() => expect(fetch).toHaveBeenCalled());
  await waitFor(() => expect(queryClient.getQueryState(["account", "storage"])?.status).toBe("success"));
}

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

  it("says nothing without a limit", async () => {
    await renderAt(50 * GB, null);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });
});
