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
  /** Text that warns (the level `maintain`): amber on dark schemes, a dark ochre on light ones. */
  warning: string;
  /** A mark that says all is well (a publication up to date): each palette's green, 3:1 on every ground. */
  success: string;
  /** Behind `==highlighted==` text: a highlighter's yellow, muted and dark on dark schemes; `text` on it 4.5:1. */
  highlight: string;
  /** A flag's icon (red, green, blue, purple, yellow; the white one is outlined in `text`): a mark, 3:1 on every ground. */
  flagRed: string;
  flagGreen: string;
  flagBlue: string;
  flagPurple: string;
  flagYellow: string;
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
// `warning` is each palette's orange or yellow, lightened (dark schemes) or darkened (light ones) to 4.5:1 where it fell short;
// Kumbu's is a coral, as its accent is already amber. `success` is each palette's green, darkened on light schemes to 3:1;
// Rosé Pine has none, so its foam stands in. `highlight` is a warm yellow in every scheme, as Obsidian's, nearer each palette's yellow.
// The flags are each palette's red, green, blue, purple and yellow, lightened (dark schemes) or darkened (light ones)
// to 3:1 where they fell short. Where a palette has none, Kumbu chose one near it: Dracula's blue, both Rosé Pines' green,
// and Rosé Pine's blue (its pine is too dark on its grounds).
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
      warning: "#ff9466",
      success: "#6cc070",
      highlight: "#5c4a0c",
      flagRed: "#ff6b6b",
      flagGreen: "#6cc070",
      flagBlue: "#6aa8f0",
      flagPurple: "#b08cf0",
      flagYellow: "#f0d048",
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
      warning: "#fab387",
      success: "#a6e3a1",
      highlight: "#5a4a1c",
      flagRed: "#f38ba8",
      flagGreen: "#a6e3a1",
      flagBlue: "#89b4fa",
      flagPurple: "#cba6f7",
      flagYellow: "#f9e2af",
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
      warning: "#a64107",
      success: "#3a9027",
      highlight: "#fbe68f",
      flagRed: "#d20f39",
      flagGreen: "#3a9027",
      flagBlue: "#1e66f5",
      flagPurple: "#8839ef",
      flagYellow: "#ae6f17",
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
      warning: "#ff9e64",
      success: "#9ece6a",
      highlight: "#5c4a1c",
      flagRed: "#f7768e",
      flagGreen: "#9ece6a",
      flagBlue: "#7aa2f7",
      flagPurple: "#bb9af7",
      flagYellow: "#e0af68",
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
      warning: "#804300",
      success: "#587539",
      highlight: "#f5e283",
      flagRed: "#ce2355",
      flagGreen: "#587539",
      flagBlue: "#286cc8",
      flagPurple: "#894cd9",
      flagYellow: "#86683c",
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
      warning: "#feaa64",
      success: "#b8bb26",
      highlight: "#6b5212",
      flagRed: "#fc6a59",
      flagGreen: "#b8bb26",
      flagBlue: "#83a598",
      flagPurple: "#d3869b",
      flagYellow: "#fabd2f",
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
      warning: "#923003",
      success: "#746f0d",
      highlight: "#f9dc6c",
      flagRed: "#9d0006",
      flagGreen: "#726d0d",
      flagBlue: "#076678",
      flagPurple: "#8f3f71",
      flagYellow: "#946110",
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
      warning: "#ebcb8b",
      success: "#a3be8c",
      highlight: "#635533",
      flagRed: "#ce878e",
      flagGreen: "#a3be8c",
      flagBlue: "#81a1c1",
      flagPurple: "#b690af",
      flagYellow: "#ebcb8b",
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
      warning: "#ffb86c",
      success: "#50fa7b",
      highlight: "#5e5318",
      flagRed: "#ff5555",
      flagGreen: "#50fa7b",
      flagBlue: "#7ea7ff",
      flagPurple: "#bd93f9",
      flagYellow: "#f1fa8c",
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
      warning: "#ccad4e",
      success: "#859900",
      highlight: "#5b4a00",
      flagRed: "#e56765",
      flagGreen: "#859900",
      flagBlue: "#3794d6",
      flagPurple: "#8488cd",
      flagYellow: "#b58900",
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
      warning: "#765a00",
      success: "#708100",
      highlight: "#f6dd75",
      flagRed: "#dc322f",
      flagGreen: "#708100",
      flagBlue: "#227dbd",
      flagPurple: "#6c71c4",
      flagYellow: "#987300",
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
      warning: "#eaa788",
      success: "#a7c080",
      highlight: "#5e5130",
      flagRed: "#e67e80",
      flagGreen: "#a7c080",
      flagBlue: "#7fbbb3",
      flagPurple: "#d699b6",
      flagYellow: "#dbbc7f",
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
      warning: "#974d17",
      success: "#768701",
      highlight: "#f7e38c",
      flagRed: "#da4b48",
      flagGreen: "#768701",
      flagBlue: "#3485b1",
      flagPurple: "#c05aa0",
      flagYellow: "#a57600",
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
      warning: "#f6c177",
      success: "#9ccfd8",
      highlight: "#5e4a22",
      flagRed: "#eb6f92",
      flagGreen: "#95c99a",
      flagBlue: "#6e9fd8",
      flagPurple: "#c4a7e7",
      flagYellow: "#f6c177",
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
      warning: "#8d5f1f",
      success: "#538e99",
      highlight: "#fbe39c",
      flagRed: "#b4637a",
      flagGreen: "#5b8a5e",
      flagBlue: "#286983",
      flagPurple: "#907aa9",
      flagYellow: "#b27728",
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
      warning: "#e5c07b",
      success: "#98c379",
      highlight: "#5f4c1e",
      flagRed: "#e06c75",
      flagGreen: "#98c379",
      flagBlue: "#61afef",
      flagPurple: "#c678dd",
      flagYellow: "#e5c07b",
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
      warning: "#865c01",
      success: "#489147",
      highlight: "#fff09a",
      flagRed: "#df5448",
      flagGreen: "#489147",
      flagBlue: "#4078f2",
      flagPurple: "#a626a4",
      flagYellow: "#ae7701",
    },
  },
];

export const defaultSchemeFor = (prefersDark: boolean): string => (prefersDark ? "kumbu" : "catppuccin-latte");

/**
 * The discreet mode's amber, one for every light scheme and one for every dark one, which index.css repeats in each
 * scheme's block. `text` (the pill's words) at 4.5:1 on every ground and on the pill's tint; `line` (the frame, the
 * pill's border) at 3:1, on light schemes a brighter amber than the dark brown the text needs there.
 */
export const discreet = {
  light: { text: "#643b00", line: "#a05800" },
  dark: { text: "#ffd27f", line: "#ffd27f" },
} as const;
