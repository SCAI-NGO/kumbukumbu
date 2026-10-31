// SPDX-License-Identifier: AGPL-3.0-or-later

/** "created": the server's order, oldest first; "unused": the least recently used first, to find ones to revoke. */
export type UsageOrder = "created" | "unused";

/** Never used first, then the oldest use; a stable sort, so ties keep the server's order. */
export function inUsageOrder<T extends { lastUsedAt?: string | null }>(items: T[], order: UsageOrder): T[] {
  if (order === "created") return items;
  // 0 for never used: earlier than any real use, and 0 - 0 is still a tie (unlike -Infinity).
  const used = (item: T) => (item.lastUsedAt ? new Date(item.lastUsedAt).getTime() : 0);
  return [...items].sort((a, b) => used(a) - used(b));
}

const tabClass = (on: boolean) =>
  `min-h-11 flex-1 rounded-[10px] px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${on ? "bg-accent text-on-accent" : "text-text-body"}`;

export function UsageOrderSwitch({ label, order, onOrder }: { label: string; order: UsageOrder; onOrder(order: UsageOrder): void }) {
  return (
    <div role="group" aria-label={label} className="flex gap-1 rounded-[12px] border border-line p-1">
      <button type="button" aria-pressed={order === "created"} onClick={() => onOrder("created")} className={tabClass(order === "created")}>
        By creation
      </button>
      <button type="button" aria-pressed={order === "unused"} onClick={() => onOrder("unused")} className={tabClass(order === "unused")}>
        Least recently used first
      </button>
    </div>
  );
}
