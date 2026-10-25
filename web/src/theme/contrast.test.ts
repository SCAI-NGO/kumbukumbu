// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { schemes, type Tokens } from "./schemes";

function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const v = parseInt(hex.slice(i, i + 2), 16) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const textTokens = ["text", "textBody", "muted", "dim", "accentText"] as const satisfies readonly (keyof Tokens)[];
const grounds = ["bg", "surface", "surface2"] as const satisfies readonly (keyof Tokens)[];

describe("colour schemes", () => {
  it("ships Kumbu and Catppuccin Latte", () => {
    expect(schemes.map((s) => s.id)).toEqual(["kumbu", "catppuccin-latte"]);
  });

  describe.each(schemes)("$name", (scheme) => {
    it.each(textTokens.flatMap((t) => grounds.map((g) => [t, g] as const)))(
      "%s on %s is at least 4.5:1",
      (text, ground) => {
        expect(contrast(scheme.tokens[text], scheme.tokens[ground])).toBeGreaterThanOrEqual(4.5);
      },
    );

    it("on-accent on accent is at least 4.5:1", () => {
      expect(contrast(scheme.tokens.onAccent, scheme.tokens.accent)).toBeGreaterThanOrEqual(4.5);
    });
  });

  it("has a checker that notices a failing pair", () => {
    expect(contrast("#777777", "#808080")).toBeLessThan(4.5);
  });
});
