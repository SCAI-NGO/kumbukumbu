// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { act } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { usePasswordPrompt } from "../state/passwordPrompt";
import { useSession } from "../state/session";
import { fakeApi, json, noContent } from "../test/app";
import PasswordPrompt from "./PasswordPrompt";

afterEach(() => {
  vi.unstubAllGlobals();
  useSession.getState().reset();
  vi.restoreAllMocks();
  act(() => usePasswordPrompt.getState().settle(false));
});

/** The dialog, open for one guarded request; `settled` is what that request hears. */
function open() {
  render(<PasswordPrompt />);
  let settled!: Promise<boolean>;
  act(() => {
    settled = usePasswordPrompt.getState().ask();
  });
  return settled;
}

describe("PasswordPrompt", () => {
  it("confirms the password and lets the request go on", async () => {
    const fetch = fakeApi({ "POST /api/session/confirm": () => noContent() });
    const settled = open();
    expect(screen.getByRole("alertdialog", { name: "Confirm your password" })).toBeInTheDocument();
    expect(screen.getByText("It's a while since you last gave your password on this device. Give it again to go on.")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Your password"), "correct horse");
    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));
    expect(await settled).toBe(true);
    expect(await fetch.mock.calls[0][0].json()).toEqual({ password: "correct horse" });
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("says so in the dialog when the password is wrong", async () => {
    fakeApi({ "POST /api/session/confirm": () => json(400, { error: "The password is wrong." }) });
    open();
    await userEvent.type(screen.getByLabelText("Your password"), "wrong");
    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));
    expect(await screen.findByText("The password is wrong.")).toBeInTheDocument();
    expect(screen.getByRole("alertdialog")).toBeInTheDocument();
    expect(usePasswordPrompt.getState().open).toBe(true);
  });

  it("shows the wait of a rate limit", async () => {
    fakeApi({ "POST /api/session/confirm": () => json(429, { error: "Too many attempts; try again in a minute." }) });
    open();
    await userEvent.type(screen.getByLabelText("Your password"), "wrong");
    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));
    expect(await screen.findByText("Too many attempts; try again in a minute.")).toBeInTheDocument();
  });

  it("gives up when the session itself has ended meanwhile", async () => {
    fakeApi({ "POST /api/session/confirm": () => json(401, { error: "Sign in first." }) });
    const settled = open();
    await userEvent.type(screen.getByLabelText("Your password"), "correct horse");
    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));
    expect(await settled).toBe(false);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("cancels the action on Cancel", async () => {
    const fetch = fakeApi({});
    const settled = open();
    await userEvent.type(screen.getByLabelText("Your password"), "half typed");
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(await settled).toBe(false);
    expect(fetch).not.toHaveBeenCalled();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    // The next time it opens, the password typed before is gone.
    act(() => {
      void usePasswordPrompt.getState().ask();
    });
    expect(screen.getByLabelText("Your password")).toHaveValue("");
  });
});
