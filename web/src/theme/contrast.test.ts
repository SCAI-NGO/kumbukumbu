// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { discreet, schemes, type Tokens } from "./schemes";

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

const textTokens = ["text", "textBody", "muted", "dim", "accentText", "warning"] as const satisfies readonly (keyof Tokens)[];
const grounds = ["bg", "surface", "surface2"] as const satisfies readonly (keyof Tokens)[];
const flagTokens = ["flagRed", "flagGreen", "flagBlue", "flagPurple", "flagYellow"] as const satisfies readonly (keyof Tokens)[];

describe("colour schemes", () => {
  it("ships the schemes decided in MOBILE_EDITOR section 6, each once", () => {
    expect(schemes.map((s) => s.id).sort()).toEqual(
      [
        "kumbu", "catppuccin-mocha", "catppuccin-latte", "tokyo-night", "tokyo-night-day", "gruvbox-dark", "gruvbox-light",
        "nord", "dracula", "solarized-dark", "solarized-light", "everforest-dark", "everforest-light", "rose-pine",
        "rose-pine-dawn", "one-dark", "one-light",
      ].sort(),
    );
  });

  it("marks a scheme light exactly when its background is light", () => {
    for (const s of schemes) expect(s.light).toBe(luminance(s.tokens.bg) > 0.4);
  });

  it("writes every token as a six-digit lower-case hex colour", () => {
    for (const s of schemes) for (const v of Object.values(s.tokens)) expect(v).toMatch(/^#[0-9a-f]{6}$/);
  });

  describe.each(schemes)("$name", (scheme) => {
    it.each(textTokens.flatMap((t) => grounds.map((g) => [t, g] as const)))(
      "%s on %s is at least 4.5:1",
      (text, ground) => {
        expect(contrast(scheme.tokens[text], scheme.tokens[ground])).toBeGreaterThanOrEqual(4.5);
      },
    );

    // A mark, not text: 3:1 (WCAG 1.4.11), as the green dot of a publication up to date.
    it.each(grounds)("success on %s is at least 3:1", (ground) => {
      expect(contrast(scheme.tokens.success, scheme.tokens[ground])).toBeGreaterThanOrEqual(3);
    });

    // A flag's icon, not text: 3:1 (WCAG 1.4.11) where it is drawn, the tree, the note's header and the menus.
    it.each(flagTokens.flatMap((t) => grounds.map((g) => [t, g] as const)))("%s on %s is at least 3:1", (flag, ground) => {
      expect(contrast(scheme.tokens[flag], scheme.tokens[ground])).toBeGreaterThanOrEqual(3);
    });

    // `==highlighted==` text in the note view.
    it("text on highlight is at least 4.5:1", () => {
      expect(contrast(scheme.tokens.text, scheme.tokens.highlight)).toBeGreaterThanOrEqual(4.5);
    });

    it("on-accent on accent is at least 4.5:1", () => {
      expect(contrast(scheme.tokens.onAccent, scheme.tokens.accent)).toBeGreaterThanOrEqual(4.5);
    });
  });

  /** `color` over `ground` at `alpha`, as the pill's tint lies over the drawer. */
  const over = (color: string, ground: string, alpha: number) =>
    "#" + [1, 3, 5].map((i) => Math.round(parseInt(color.slice(i, i + 2), 16) * alpha + parseInt(ground.slice(i, i + 2), 16) * (1 - alpha)).toString(16).padStart(2, "0")).join("");

  it.each(schemes)("the discreet amber reads at 4.5:1 on $name's grounds and on its tint", (scheme) => {
    const { text } = scheme.light ? discreet.light : discreet.dark;
    for (const ground of grounds) {
      expect(contrast(text, scheme.tokens[ground])).toBeGreaterThanOrEqual(4.5);
      expect(contrast(text, over(text, scheme.tokens[ground], 0.14))).toBeGreaterThanOrEqual(4.5);
    }
  });

  // The frame and the pill's border: a mark, not text, so 3:1 (WCAG 1.4.11), brighter than the text where it can be.
  it.each(schemes)("the discreet frame stands out at 3:1 on $name's grounds", (scheme) => {
    const { line } = scheme.light ? discreet.light : discreet.dark;
    for (const ground of grounds) expect(contrast(line, scheme.tokens[ground])).toBeGreaterThanOrEqual(3);
  });

  it("draws the light frame brighter than the light text", () => {
    expect(luminance(discreet.light.line)).toBeGreaterThan(luminance(discreet.light.text));
  });

  it("has a checker that notices a failing pair", () => {
    expect(contrast("#777777", "#808080")).toBeLessThan(4.5);
  });
});
