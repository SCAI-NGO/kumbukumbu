// SPDX-License-Identifier: AGPL-3.0-or-later

export type Direction = "up" | "down" | "left" | "right";

/** The closest node within 45° either side of the direction (up is towards smaller y, as on the screen); null when there is none. */
export function nearestInDirection(from: { x: number; y: number }, others: ReadonlyArray<{ key: string; x: number; y: number }>, dir: Direction): string | null {
  let best: string | null = null;
  let bestDistance = Infinity;
  for (const o of others) {
    const [dx, dy] = [o.x - from.x, o.y - from.y];
    const [along, across] = dir === "right" ? [dx, dy] : dir === "left" ? [-dx, dy] : dir === "down" ? [dy, dx] : [-dy, dx];
    if (along <= 0 || Math.abs(across) > along) continue;
    const distance = Math.hypot(dx, dy);
    if (distance < bestDistance) [best, bestDistance] = [o.key, distance];
  }
  return best;
}
