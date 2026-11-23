// SPDX-License-Identifier: AGPL-3.0-or-later
// The order people expect for text, Ü next to U rather than after z (Kumbu.Core Sh/TextOrder.Default): the root
// collation, which .NET's invariant culture uses, with ties broken by code unit so the order is total.

const collator = new Intl.Collator("und");

export function textOrder(a: string, b: string): number {
  return collator.compare(a, b) || (a < b ? -1 : a > b ? 1 : 0);
}
