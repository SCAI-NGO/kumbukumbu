// SPDX-License-Identifier: AGPL-3.0-or-later
import type { GNode, SizeBy } from "./model";
import { AGE_MAX_MS, AGE_MIN_MS, darkRamp, lightRamp, neutral } from "./ramps";

export const R_MIN = 5;
export const R_MAX = 22;
export const R_GHOST = 4;
export const R_ATTACHMENT = 6;
/** The measure at which a note reaches the full radius: sixty links, a hundred thousand bytes. */
const M_LINKS = 60;
const M_LENGTH = 100_000;

export const MIN_DISTANCE = 40;
export const SPAN = 110;
export const GAP = 8;

export const measureOf = (node: GNode, by: SizeBy): number => (by === "links" ? node.in + node.out : by === "backlinks" ? node.in : (node.size ?? 0));

/** Ghosts and attachments keep one size; notes grow with the measure, on a log scale so a hub does not swallow the rest. */
export function radius(node: GNode, by: SizeBy): number {
  if (node.kind === "ghost") return R_GHOST;
  if (node.kind === "attachment") return R_ATTACHMENT;
  const max = by === "length" ? M_LENGTH : M_LINKS;
  return R_MIN + (R_MAX - R_MIN) * Math.min(1, Math.log1p(measureOf(node, by)) / Math.log1p(max));
}

const rgb = (hex: string) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
const hex = (c: number[]) => "#" + c.map((v) => Math.round(v).toString(16).padStart(2, "0")).join("");

/** The star colour of a note by its age, interpolated on the log of the age between the ramp's stops, clamped at both ends; neutral without a readable time. */
export function ageColour(writtenAt: string | null, now: number, dark: boolean): string {
  const written = writtenAt === null ? NaN : Date.parse(writtenAt);
  if (Number.isNaN(written)) return dark ? neutral.dark : neutral.light;
  const ramp = dark ? darkRamp : lightRamp;
  const age = Math.min(AGE_MAX_MS, Math.max(AGE_MIN_MS, now - written));
  const t = (Math.log(age) - Math.log(AGE_MIN_MS)) / (Math.log(AGE_MAX_MS) - Math.log(AGE_MIN_MS));
  const at = t * (ramp.length - 1);
  const i = Math.min(ramp.length - 2, Math.floor(at));
  const [a, b] = [rgb(ramp[i]), rgb(ramp[i + 1])];
  return at >= ramp.length - 1 ? ramp[ramp.length - 1] : hex(a.map((v, k) => v + (b[k] - v) * (at - i)));
}

// `s` is the similarity of an edge's two ends, 0 to 1: the more alike, the thicker, the stronger and the closer.
export const edgeWidth = (s: number): number => 0.75 + 2.25 * s;
export const edgeOpacity = (s: number): number => 0.25 + 0.6 * s;
export const linkDistance = (s: number): number => MIN_DISTANCE + (1 - s) * SPAN;
export const linkStrength = (s: number): number => 0.15 + 0.85 * s;
export const collideRadius = (r: number): number => r + GAP;
