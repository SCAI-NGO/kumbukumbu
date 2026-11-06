// SPDX-License-Identifier: AGPL-3.0-or-later

/** "Snapshot of 5 Nov 2026, 14:32", in the reader's own time zone (or `timeZone`): nothing of the owner's is sent. */
export function snapshotText(iso: string, timeZone?: string): string {
  const parts = new Intl.DateTimeFormat("en-GB", {
    day: "numeric",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23",
    timeZone,
  }).formatToParts(new Date(iso));
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? "";
  return `Snapshot of ${part("day")} ${part("month")} ${part("year")}, ${part("hour")}:${part("minute")}`;
}
