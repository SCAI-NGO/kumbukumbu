// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import FlagIcon from "./FlagIcon";
import { FLAGS } from "./api";

afterEach(cleanup);

describe("the flag's icon", () => {
  it("every flag has its name, never its colour alone", () => {
    const names = FLAGS.map((flag) => {
      render(<FlagIcon flag={flag} />);
      const icon = screen.getByRole("img");
      const name = icon.getAttribute("aria-label");
      expect(icon.querySelector("title")).toHaveTextContent(name!);
      cleanup();
      return name;
    });
    expect(names).toEqual(["Red flag", "Green flag", "Blue flag", "Purple flag", "Yellow flag", "White flag", "Done", "Important"]);
  });

  it("the coloured flags are filled in their colour", () => {
    for (const flag of ["red", "green", "blue", "purple", "yellow"] as const) {
      render(<FlagIcon flag={flag} />);
      const icon = screen.getByRole("img");
      expect(icon).toHaveAttribute("fill", "currentColor");
      expect(icon).toHaveClass(`text-flag-${flag}`);
      cleanup();
    }
  });

  it("the white flag is outlined", () => {
    render(<FlagIcon flag="white" />);
    const icon = screen.getByRole("img", { name: "White flag" });
    expect(icon).toHaveAttribute("fill", "none");
    expect(icon).toHaveClass("text-text");
  });

  it("the tick is a green check, important an exclamation mark", () => {
    render(<FlagIcon flag="done" />);
    expect(screen.getByRole("img", { name: "Done" })).toHaveClass("lucide-check", "text-flag-green");
    cleanup();
    render(<FlagIcon flag="important" />);
    expect(screen.getByRole("img", { name: "Important" })).toHaveClass("lucide-exclamation-mark");
  });

  it("a tag that is no flag shows nothing", () => {
    const { container } = render(<FlagIcon flag="maybe" />);
    expect(container).toBeEmptyDOMElement();
  });
});
