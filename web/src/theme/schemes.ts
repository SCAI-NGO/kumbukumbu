// SPDX-License-Identifier: AGPL-3.0-or-later

/** The colours of a scheme. Components only ever use these tokens (see index.css). */
export interface Tokens {
  bg: string;
  surface: string;
  surface2: string;
  line: string;
  text: string;
  textBody: string;
  muted: string;
  dim: string;
  /** Fills and borders. */
  accent: string;
  /** Accent-coloured text (H2, links); differs from `accent` where `accent` is too faint to read. */
  accentText: string;
  /** Text on an `accent` fill. */
  onAccent: string;
}

export interface Scheme {
  id: string;
  name: string;
  light: boolean;
  tokens: Tokens;
}

// Values per concepts/MOBILE_EDITOR_v1.md section 6. index.css repeats them as custom properties;
// schemes.test.ts keeps the two in step, contrast.test.ts holds every text token to 4.5:1.
// Kumbu's `dim` is #8f8c86, not the spec's #8a8781: that one is 4.4:1 on `surface-2`, which the test rejects.
export const schemes: readonly Scheme[] = [
  {
    id: "kumbu",
    name: "Kumbu",
    light: false,
    tokens: {
      bg: "#111113",
      surface: "#1a1a1e",
      surface2: "#232328",
      line: "#2e2e34",
      text: "#eceae4",
      textBody: "#d9d6cf",
      muted: "#a09d96",
      dim: "#8f8c86",
      accent: "#f0a848",
      accentText: "#f0a848",
      onAccent: "#17110a",
    },
  },
  {
    // Catppuccin Latte's base, mantle and crust; muted, dim and the accent darkened to 4.5:1 (spec: "adjusted like muted").
    id: "catppuccin-latte",
    name: "Catppuccin Latte",
    light: true,
    tokens: {
      bg: "#eff1f5",
      surface: "#e6e9ef",
      surface2: "#dce0e8",
      line: "#ccd0da",
      text: "#3c3f58",
      textBody: "#4c4f69",
      muted: "#555870",
      dim: "#5f6278",
      accent: "#8839ef",
      accentText: "#7a2fd6",
      onAccent: "#ffffff",
    },
  },
];

export const defaultSchemeFor = (prefersDark: boolean): string => (prefersDark ? "kumbu" : "catppuccin-latte");
