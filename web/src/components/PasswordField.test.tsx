// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it } from "vitest";
import { useSession } from "../state/session";
import { user } from "../test/app";
import { PasswordField } from "./PasswordField";

function Harness({ policy }: { policy?: boolean }) {
  const [value, setValue] = useState("");
  return <PasswordField label="Password" value={value} onChange={setValue} autoComplete="new-password" policy={policy} />;
}

function Current() {
  const [value, setValue] = useState("");
  return <PasswordField label="Your password" value={value} onChange={setValue} autoComplete="current-password" />;
}

afterEach(() => useSession.getState().reset());

describe("PasswordField", () => {
  it("shows the policy hint and the warning for a new password", () => {
    render(<Harness policy />);
    expect(screen.getByText("At least 12 characters, and not one of the most common passwords.")).toBeInTheDocument();
    expect(screen.getByText(/Username and password are less safe than GitHub with a passkey; those come later/)).toBeInTheDocument();
  });

  it("shows neither when signing in", () => {
    render(<Harness />);
    expect(screen.queryByText(/At least 12 characters/)).not.toBeInTheDocument();
    expect(screen.queryByText(/less safe than GitHub/)).not.toBeInTheDocument();
  });

  it("can show what was typed", async () => {
    render(<Harness />);
    const input = screen.getByLabelText("Password");
    expect(input).toHaveAttribute("type", "password");
    await userEvent.click(screen.getByRole("button", { name: "Show password" }));
    expect(input).toHaveAttribute("type", "text");
  });

  it("a current-password field has a hidden username field before it, with the signed-in name, autocomplete username, read-only, out of the tab order and hidden from screen readers", () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    const { container } = render(<Current />);
    const hidden = container.querySelector<HTMLInputElement>('input[autocomplete="username"]')!;
    expect(hidden).toBeInTheDocument();
    expect(hidden).toHaveValue("ada");
    expect(hidden).toHaveAttribute("readonly");
    expect(hidden).toHaveAttribute("tabindex", "-1");
    expect(hidden).toHaveAttribute("aria-hidden", "true");
    // Managers skip a field that is display: none, so it is hidden by clipping instead.
    expect(hidden).toHaveClass("sr-only");
    expect(hidden.compareDocumentPosition(screen.getByLabelText("Your password")) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("has no username field when nobody is signed in, as on the sign-in page, or for a new password", () => {
    const { container, unmount } = render(<Current />);
    expect(container.querySelector('input[autocomplete="username"]')).toBeNull();
    unmount();
    useSession.getState().signedIn({ user, previousSignInAt: null });
    const again = render(<Harness />);
    expect(again.container.querySelector('input[autocomplete="username"]')).toBeNull();
  });
});
