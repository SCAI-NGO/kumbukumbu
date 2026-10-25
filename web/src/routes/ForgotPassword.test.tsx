// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, renderApp } from "../test/app";

const newKey = "WXYZ-2345-6789-ABCD";

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

async function fill() {
  await userEvent.type(screen.getByLabelText("Username"), "ada");
  await userEvent.type(screen.getByLabelText("Recovery key"), "ABCD-EFGH");
  await userEvent.type(screen.getByLabelText("New password"), "a brand new passphrase");
  await userEvent.click(screen.getByRole("button", { name: "Set new password" }));
}

describe("ForgotPassword", () => {
  it("sets a new password, shows the new recovery key once, and then points to sign-in", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    const fetch = fakeApi({ "POST /api/recover": () => json(200, { recoveryKey: newKey }) });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/forgot", queryClient);
    expect(screen.getByText(/less safe than GitHub/)).toBeInTheDocument();
    await fill();

    expect(await screen.findByText(newKey)).toBeInTheDocument();
    expect(screen.getByText(/old recovery key no longer works/)).toBeInTheDocument();
    expect(screen.getByText(/all your API keys were revoked/)).toBeInTheDocument();
    expect(await fetch.mock.calls[0][0].json()).toEqual({ username: "ada", recoveryKey: "ABCD-EFGH", newPassword: "a brand new passphrase" });

    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(writeText).toHaveBeenCalledWith(newKey);
    await userEvent.click(screen.getByLabelText(/I have stored my recovery key/));
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));

    expect(await screen.findByText(/Your new password is set/)).toBeInTheDocument();
    expect(screen.queryByText(newKey)).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/signin");
    // The key lived only in the reveal: not in the address, storage or the client-wide caches.
    expect(router.state.location.pathname + router.state.location.search + router.state.location.hash).toBe("/forgot");
    expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain(newKey);
    const cached = JSON.stringify([
      queryClient.getMutationCache().getAll().map((m) => m.state),
      queryClient.getQueryCache().getAll().map((q) => q.state),
    ]);
    expect(cached).not.toContain(newKey);
  });

  it("doesn't let the user go on until the new recovery key is saved", async () => {
    fakeApi({ "POST /api/recover": () => json(200, { recoveryKey: newKey }) });
    const router = renderApp("/forgot");
    await fill();
    await screen.findByText(newKey);

    await userEvent.click(screen.getByLabelText(/I have stored my recovery key/));
    expect(screen.getByRole("button", { name: "Continue" })).toBeDisabled();

    await act(() => router.navigate("/signin"));
    expect(screen.getByRole("alertdialog")).toHaveTextContent("You haven't saved your recovery key");
    expect(router.state.location.pathname).toBe("/forgot");
  });

  it("shows the server's sentence when the key is wrong", async () => {
    fakeApi({ "POST /api/recover": () => json(401, { error: "The username or recovery key is wrong." }) });
    renderApp("/forgot");
    await fill();
    expect(await screen.findByRole("alert")).toHaveTextContent("The username or recovery key is wrong.");
  });
});
