// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, noContent } from "../test/app";
import SharedBanner, { MemberNotices } from "./SharedBanner";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const garten = (extra: Record<string, unknown> = {}) => ({
  name: "garten",
  notes: 2,
  embeddings: "remote",
  pending: false,
  canWrite: true,
  level: "write",
  owner: '"Alice"',
  ownerUsername: "alice",
  shared: true,
  membership: "m1",
  providerChanged: false,
  ownerInactive: false,
  closingAt: null,
  ...extra,
});
const providers = [{ name: "remote", label: "Remote Embeddings", description: null, external: true, available: true, reason: null, default: false }];

function renderIt(children: React.ReactNode) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("SharedBanner", () => {
  it("shows the provider banner until OK", async () => {
    let vault = garten({ providerChanged: true });
    const fetch = fakeApi({
      "GET /api/vaults": () => json(200, [vault]),
      "GET /api/embeddings": () => json(200, providers),
      "POST /api/memberships/m1/provider-seen": () => {
        vault = garten();
        return noContent();
      },
    });
    renderIt(<SharedBanner vault="garten" />);
    const banner = await screen.findByRole("region", { name: "Embedding provider changed" });
    await waitFor(() => expect(banner).toHaveTextContent('"Alice" changed how garten is searched by meaning: its notes now go to Remote Embeddings.'));
    expect(banner).toHaveTextContent("your searches leave garten out");
    expect(within(banner).getByRole("button", { name: "Leave this vault" })).toBeInTheDocument();

    await userEvent.click(within(banner).getByRole("button", { name: "OK" }));
    await waitFor(() => expect(screen.queryByRole("region", { name: "Embedding provider changed" })).not.toBeInTheDocument());
    const ok = fetch.mock.calls.map(([r]) => r as Request).find((r) => r.method === "POST")!;
    // The provider she was shown: an OK for one the owner has replaced since consents to nothing.
    expect(await ok.json()).toEqual({ provider: "remote" });
  });

  it("offers to leave from the provider banner, after asking", async () => {
    fakeApi({ "GET /api/vaults": () => json(200, [garten({ providerChanged: true })]), "GET /api/embeddings": () => json(200, providers) });
    renderIt(<SharedBanner vault="garten" />);
    await userEvent.click(await screen.findByRole("button", { name: "Leave this vault" }));
    expect(screen.getByRole("alertdialog", { name: "Leave garten?" })).toBeInTheDocument();
  });

  it("shows the closing date", async () => {
    fakeApi({ "GET /api/vaults": () => json(200, [garten({ level: "read", canWrite: false, closingAt: "2026-11-08T10:00:00Z", providerChanged: true })]) });
    renderIt(<SharedBanner vault="garten" refusal="garten is read-only: its owner deleted their account, and it is closing." />);
    expect(await screen.findByRole("status")).toHaveTextContent("The owner deleted their account; garten will be deleted on 8 November 2026. Export a copy to keep its notes.");
    // The closing says it all: no provider banner beside it.
    expect(screen.queryByRole("region")).not.toBeInTheDocument();
  });

  it("shows that the owner is deactivated", async () => {
    fakeApi({ "GET /api/vaults": () => json(200, [garten({ level: "read", canWrite: false, ownerInactive: true })]) });
    renderIt(<SharedBanner vault="garten" />);
    expect(await screen.findByRole("status")).toHaveTextContent(
      `"Alice"'s account is deactivated. This vault is read-only until it is active again. You can export a copy.`,
    );
  });

  it("says why a note can't be changed where no banner does", async () => {
    fakeApi({ "GET /api/vaults": () => json(200, [garten({ level: "read", canWrite: false })]) });
    renderIt(<SharedBanner vault="garten" refusal="You can only read garten." />);
    expect(await screen.findByText("You can only read garten.")).toBeInTheDocument();
  });

  it("lists member notices until dismissed", async () => {
    let notices = [{ id: "n1", kind: "vault-deleted", at: "2026-10-31T10:00:00Z", vault: "garten", text: "The owner has deleted garten." }];
    const fetch = fakeApi({
      "GET /api/member-notices": () => json(200, notices),
      "DELETE /api/member-notices/n1": () => {
        notices = [];
        return noContent();
      },
    });
    renderIt(<MemberNotices />);
    const list = await screen.findByRole("list", { name: "Notices" });
    expect(list).toHaveTextContent("The owner has deleted garten.");
    await userEvent.click(within(list).getByRole("button", { name: "Dismiss: The owner has deleted garten." }));
    await waitFor(() => expect(screen.queryByRole("list", { name: "Notices" })).not.toBeInTheDocument());
    expect(fetch.mock.calls.some(([r]) => (r as Request).method === "DELETE")).toBe(true);
  });
});
