// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, test, vi } from "vitest";
import { useSession } from "../state/session";
import { user } from "../test/app";
import PasswordChangedCopies, { markPasswordChanged } from "./PasswordChangedCopies";

const here = { id: "s1", label: "Firefox on Linux", createdAt: "2026-11-20T10:00:00Z", contactAt: "2026-11-22T10:00:00Z", waiting: 0, current: true };
const phone = { id: "s2", label: "Safari on iOS", createdAt: "2026-11-01T10:00:00Z", contactAt: "2026-11-19T10:00:00Z", waiting: 0, current: false };

function serve(slots: unknown[]) {
  const fetch = vi.fn((input: Request) => Promise.resolve(input.method === "GET"
    ? new Response(JSON.stringify(slots), { status: 200, headers: { "Content-Type": "application/json" } })
    : new Response(null, { status: 204 })));
  vi.stubGlobal("fetch", fetch);
  return fetch;
}

const show = () => render(<QueryClientProvider client={new QueryClient()}><PasswordChangedCopies /></QueryClientProvider>);
const region = () => screen.queryByRole("region", { name: "Offline copies after your password change" });

beforeEach(() => {
  localStorage.clear();
  useSession.getState().signedIn({ user, previousSignInAt: null });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
});

describe("after a password change", () => {
  test("the devices with a copy are listed, each with Remove offline copy, until Done", async () => {
    const fetch = serve([here, phone]);
    markPasswordChanged();
    show();
    const list = await screen.findByRole("list", { name: "Devices with a copy" });
    expect(within(list).queryByText("Firefox on Linux")).toBeNull();
    await userEvent.click(within(list).getByRole("button", { name: "Remove offline copy" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Remove" }));
    await vi.waitFor(() => expect(fetch.mock.calls.some(([r]) => r.method === "DELETE")).toBe(true));
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(region()).toBeNull();
    cleanup();
    show();
    await new Promise((r) => setTimeout(r, 50));
    expect(region()).toBeNull();
  });

  test("says nothing without another device's copy, or without a password change", async () => {
    serve([here]);
    markPasswordChanged();
    show();
    await new Promise((r) => setTimeout(r, 50));
    expect(region()).toBeNull();
    expect(localStorage.getItem("kumbu.passwordChanged")).toBeNull();
    cleanup();
    serve([here, phone]);
    show();
    await new Promise((r) => setTimeout(r, 50));
    expect(region()).toBeNull();
  });
});
