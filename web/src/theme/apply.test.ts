// SPDX-License-Identifier: AGPL-3.0-or-later
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { applyScheme, storedScheme } from "./apply";
import { schemes } from "./schemes";

function mockSystem(dark: boolean) {
  vi.stubGlobal("matchMedia", (q: string) => ({ matches: dark && q.includes("dark"), addEventListener() {} }));
}

describe("applyScheme", () => {
  beforeEach(() => {
    localStorage.clear();
    delete document.documentElement.dataset.scheme;
  });

  it("follows the system until a scheme is chosen", () => {
    mockSystem(true);
    applyScheme(null);
    expect(document.documentElement.dataset.scheme).toBe("kumbu");
    mockSystem(false);
    applyScheme(null);
    expect(document.documentElement.dataset.scheme).toBe("catppuccin-latte");
  });

  it("keeps a choice, which beats the system", () => {
    mockSystem(true);
    applyScheme("catppuccin-latte");
    expect(storedScheme()).toBe("catppuccin-latte");
    expect(document.documentElement.dataset.scheme).toBe("catppuccin-latte");
  });

  it("ignores a stored name it doesn't know, and works without storage", () => {
    localStorage.setItem("kumbu.scheme", "nope");
    expect(storedScheme()).toBeNull();
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    mockSystem(false);
    expect(() => applyScheme("kumbu")).not.toThrow();
    expect(document.documentElement.dataset.scheme).toBe("kumbu");
  });
});

describe("index.css", () => {
  const css = readFileSync(resolve("src/index.css"), "utf8");
  const kebab = (s: string) => s.replace(/[A-Z0-9]/g, (c) => "-" + c.toLowerCase());

  it.each(schemes)("repeats the tokens of $name", (scheme) => {
    const block = css.match(new RegExp(`\\[data-scheme="${scheme.id}"\\]\\s*\\{([^}]*)\\}`))?.[1] ?? "";
    for (const [token, value] of Object.entries(scheme.tokens)) {
      expect(block).toContain(`--${kebab(token)}: ${value};`);
    }
  });
});
