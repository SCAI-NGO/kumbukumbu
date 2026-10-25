// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { PasswordField } from "./PasswordField";

function Harness({ policy }: { policy?: boolean }) {
  const [value, setValue] = useState("");
  return <PasswordField label="Password" value={value} onChange={setValue} autoComplete="new-password" policy={policy} />;
}

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
});
