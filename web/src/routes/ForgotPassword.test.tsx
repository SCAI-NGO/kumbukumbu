// SPDX-License-Identifier: AGPL-3.0-or-later
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, noContent, renderApp } from "../test/app";

afterEach(() => vi.unstubAllGlobals());

async function fill() {
  await userEvent.type(screen.getByLabelText("Username"), "ada");
  await userEvent.type(screen.getByLabelText("Recovery key"), "ABCD-EFGH");
  await userEvent.type(screen.getByLabelText("New password"), "a brand new passphrase");
  await userEvent.click(screen.getByRole("button", { name: "Set new password" }));
}

describe("ForgotPassword", () => {
  it("sets a new password with the recovery key and points to sign-in", async () => {
    const fetch = fakeApi({ "POST /api/recover": () => noContent() });
    renderApp("/forgot");
    expect(screen.getByText(/less safe than GitHub/)).toBeInTheDocument();
    await fill();

    expect(await screen.findByText(/Your new password is set/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/signin");
    expect(await fetch.mock.calls[0][0].json()).toEqual({ username: "ada", recoveryKey: "ABCD-EFGH", newPassword: "a brand new passphrase" });
  });

  it("shows the server's sentence when the key is wrong", async () => {
    fakeApi({ "POST /api/recover": () => json(401, { error: "The username or recovery key is wrong." }) });
    renderApp("/forgot");
    await fill();
    expect(await screen.findByRole("alert")).toHaveTextContent("The username or recovery key is wrong.");
  });
});
