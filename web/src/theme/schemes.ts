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

// Values per concepts/MOBILE_EDITOR_v1.md section 6; the other schemes are Kumbu variants of their published palettes (source named per scheme). index.css repeats them as custom properties;
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
    // catppuccin.com/palette (Mocha): mantle, base, surface0, surface1, text, subtext1, overlay2, mauve.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: dim.
    id: "catppuccin-mocha",
    name: "Catppuccin Mocha",
    light: false,
    tokens: {
      bg: "#181825",
      surface: "#1e1e2e",
      surface2: "#313244",
      line: "#45475a",
      text: "#cdd6f4",
      textBody: "#bac2de",
      muted: "#a6adc8",
      dim: "#949ab3",
      accent: "#cba6f7",
      accentText: "#cba6f7",
      onAccent: "#0c0c13",
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
  {
    // folke/tokyonight.nvim (night): bg, bg_dark/storm, bg_highlight, fg_gutter, fg, fg_dark, dark5, comment, blue.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: muted, dim, muted (order).
    id: "tokyo-night",
    name: "Tokyo Night",
    light: false,
    tokens: {
      bg: "#1a1b26",
      surface: "#24283b",
      surface2: "#292e42",
      line: "#3b4261",
      text: "#c0caf5",
      textBody: "#a9b1d6",
      muted: "#959ab9",
      dim: "#8f95b1",
      accent: "#7aa2f7",
      accentText: "#7aa2f7",
      onAccent: "#0d0e13",
    },
  },
  {
    // folke/tokyonight.nvim (day) and the tokyo-night-vscode light foreground #343b58: bg, bg_dark, bg_highlight, fg_gutter, fg, dark5, comment, blue.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: textBody, muted, dim, muted (order), accentText, accent.
    id: "tokyo-night-day",
    name: "Tokyo Night Day",
    light: true,
    tokens: {
      bg: "#e1e2e7",
      surface: "#d0d5e3",
      surface2: "#c4c8da",
      line: "#a8aecb",
      text: "#343b58",
      textBody: "#2e51a0",
      muted: "#484e6b",
      dim: "#4e536b",
      accent: "#2b74d9",
      accentText: "#1e529a",
      onAccent: "#ffffff",
    },
  },
  {
    // morhetz/gruvbox (dark, medium): bg0, bg1, bg2, bg3, fg0, fg1, fg3, fg4, bright yellow.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: muted, dim, muted (order).
    id: "gruvbox-dark",
    name: "Gruvbox Dark",
    light: false,
    tokens: {
      bg: "#282828",
      surface: "#3c3836",
      surface2: "#504945",
      line: "#665c54",
      text: "#fbf1c7",
      textBody: "#ebdbb2",
      muted: "#cabea8",
      dim: "#c2b8a9",
      accent: "#fabd2f",
      accentText: "#fabd2f",
      onAccent: "#141414",
    },
  },
  {
    // morhetz/gruvbox (light, medium): bg0, bg1, bg2, bg3, fg0, fg1, fg3, fg4, neutral orange.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: muted, dim, muted (order), accentText, accent.
    id: "gruvbox-light",
    name: "Gruvbox Light",
    light: true,
    tokens: {
      bg: "#fbf1c7",
      surface: "#ebdbb2",
      surface2: "#d5c4a1",
      line: "#bdae93",
      text: "#282828",
      textBody: "#3c3836",
      muted: "#564d46",
      dim: "#5b5149",
      accent: "#c3550d",
      accentText: "#893c09",
      onAccent: "#ffffff",
    },
  },
  {
    // nordtheme.com/docs/colors-and-palettes: nord0-nord6, nord8.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: muted, dim, muted (order), accentText.
    id: "nord",
    name: "Nord",
    light: false,
    tokens: {
      bg: "#2e3440",
      surface: "#3b4252",
      surface2: "#434c5e",
      line: "#4c566a",
      text: "#eceff4",
      textBody: "#d8dee9",
      muted: "#bbc2d2",
      dim: "#b5bccb",
      accent: "#88c0d0",
      accentText: "#8fc4d3",
      onAccent: "#171a20",
    },
  },
  {
    // draculatheme.com/contribute: background, current line, foreground, comment, purple; surface and surface-2 are steps between background and current line (Dracula has no mid-tones).
    // Adjusted to 4.5:1 where the scheme's own colour fell short: dim, accentText.
    id: "dracula",
    name: "Dracula",
    light: false,
    tokens: {
      bg: "#282a36",
      surface: "#313342",
      surface2: "#3a3d4e",
      line: "#44475a",
      text: "#f8f8f2",
      textBody: "#e2e2dc",
      muted: "#bdc3de",
      dim: "#9ea8c7",
      accent: "#bd93f9",
      accentText: "#be95f9",
      onAccent: "#14151b",
    },
  },
  {
    // ethanschoonover.com/solarized: base03, base02, base01, base1, base2, blue; surface-2 is a step between base02 and base01.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: textBody, muted, dim, muted (order), accentText.
    id: "solarized-dark",
    name: "Solarized Dark",
    light: false,
    tokens: {
      bg: "#002b36",
      surface: "#073642",
      surface2: "#124753",
      line: "#586e75",
      text: "#eee8d5",
      textBody: "#a3afaf",
      muted: "#aab6b7",
      dim: "#a3b0b5",
      accent: "#268bd2",
      accentText: "#72b4e2",
      onAccent: "#00161b",
    },
  },
  {
    // ethanschoonover.com/solarized: base3, base2, base1, base01, base02, blue; surface-2 is a step between base2 and base1.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: textBody, muted, dim, muted (order), accentText, accent.
    id: "solarized-light",
    name: "Solarized Light",
    light: true,
    tokens: {
      bg: "#fdf6e3",
      surface: "#eee8d5",
      surface2: "#e1dac4",
      line: "#93a1a1",
      text: "#073642",
      textBody: "#4f6369",
      muted: "#4e5e65",
      dim: "#566263",
      accent: "#217ab9",
      accentText: "#1b6497",
      onAccent: "#ffffff",
    },
  },
  {
    // sainnhe/everforest (dark, medium): bg0, bg1, bg2, bg4, fg, grey1, grey2, green.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: muted, dim, muted (order).
    id: "everforest-dark",
    name: "Everforest Dark",
    light: false,
    tokens: {
      bg: "#2d353b",
      surface: "#343f44",
      surface2: "#3d484d",
      line: "#4f585e",
      text: "#d3c6aa",
      textBody: "#d3c6aa",
      muted: "#b3bcb4",
      dim: "#adb6b0",
      accent: "#a7c080",
      accentText: "#a7c080",
      onAccent: "#171b1e",
    },
  },
  {
    // sainnhe/everforest (light, medium): bg0, bg1, bg3, bg4, fg, grey1, grey2, green.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: text, textBody, muted, dim, muted (order), accentText, accent.
    id: "everforest-light",
    name: "Everforest Light",
    light: true,
    tokens: {
      bg: "#fdf6e3",
      surface: "#f4f0d9",
      surface2: "#e6e2cc",
      line: "#e0dcc7",
      text: "#58666d",
      textBody: "#58666d",
      muted: "#576257",
      dim: "#5e665d",
      accent: "#6e7e01",
      accentText: "#5d6a01",
      onAccent: "#ffffff",
    },
  },
  {
    // rosepinetheme.com/palette (main): base, surface, overlay, highlight med, text, subtle, muted, iris.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: dim, muted (order).
    id: "rose-pine",
    name: "Rosé Pine",
    light: false,
    tokens: {
      bg: "#191724",
      surface: "#1f1d2e",
      surface2: "#26233a",
      line: "#403d52",
      text: "#e0def4",
      textBody: "#e0def4",
      muted: "#9491ad",
      dim: "#8e8ba1",
      accent: "#c4a7e7",
      accentText: "#c4a7e7",
      onAccent: "#0d0c12",
    },
  },
  {
    // rosepinetheme.com/palette (dawn): base, surface, overlay, highlight med, text, subtle, muted, iris.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: muted, dim, muted (order), accentText, accent.
    id: "rose-pine-dawn",
    name: "Rosé Pine Dawn",
    light: true,
    tokens: {
      bg: "#faf4ed",
      surface: "#fffaf3",
      surface2: "#f2e9e1",
      line: "#dfdad9",
      text: "#575279",
      textBody: "#575279",
      muted: "#65627b",
      dim: "#6c6875",
      accent: "#826e98",
      accentText: "#736287",
      onAccent: "#ffffff",
    },
  },
  {
    // navarasu/onedark.nvim (dark): bg0, bg1, bg2, bg3, fg, light_grey, comment grey, blue.
    // `text` (#dcdfe4) is brightened beyond the published fg #abb2bf, which is kept as textBody.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: muted, dim, muted (order), accentText.
    id: "one-dark",
    name: "One Dark",
    light: false,
    tokens: {
      bg: "#282c34",
      surface: "#31353f",
      surface2: "#393f4a",
      line: "#3b3f4c",
      text: "#dcdfe4",
      textBody: "#abb2bf",
      muted: "#acb1ba",
      dim: "#a7abb2",
      accent: "#61afef",
      accentText: "#63b0ef",
      onAccent: "#14161a",
    },
  },
  {
    // Atom One Light syntax theme (mono-1/2/3, hue-2 blue) and navarasu/onedark.nvim (light) for the backgrounds.
    // Adjusted to 4.5:1 where the scheme's own colour fell short: muted, dim, muted (order), accentText, accent.
    id: "one-light",
    name: "One Light",
    light: true,
    tokens: {
      bg: "#fafafa",
      surface: "#f0f0f0",
      surface2: "#e6e6e6",
      line: "#dcdcdc",
      text: "#383a42",
      textBody: "#4b4e57",
      muted: "#5e616a",
      dim: "#656569",
      accent: "#3c71e3",
      accentText: "#3461c4",
      onAccent: "#ffffff",
    },
  },
];

export const defaultSchemeFor = (prefersDark: boolean): string => (prefersDark ? "kumbu" : "catppuccin-latte");
