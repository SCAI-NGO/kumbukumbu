// SPDX-License-Identifier: AGPL-3.0-or-later
import { atLeast, isPrivate, levelOf, othersWrite, type Level, type VaultView } from "../api/notes";

/** What `maintain` adds to `write` (collab concept §7), said wherever it is chosen or shown. */
export const maintainSentence = "Can change the vault's structure: the board's columns, checkpoints.";

/** "read", "read and write", "read, write and maintain" (in the warning colour), or a right this app doesn't know, as it is. */
export function RightText({ right }: { right: string }) {
  if (right === "maintain") return <span className="text-warning">read, write and maintain</span>;
  return <>{right === "write" ? "read and write" : right}</>;
}

const rank = (right: string): Level | null => (right === "read" || right === "write" || right === "maintain" ? right : null);

/** Whether a credential's `right` on `vault` is more than the caller has there now, so it acts as less. */
export function isCapped(right: string, vault: VaultView) {
  const sealed = rank(right);
  const level = levelOf(vault);
  return !!sealed && level !== "owner" && !atLeast(level, sealed);
}

/**
 * A credential's right on `vault` (collab concept §7.1): a member may give her keys more than her role, which they get
 * only once the owner raises her; until then the right is shown with what it acts as, her level now.
 */
export function CappedRight({ right, vault }: { right: string; vault: VaultView | undefined }) {
  if (!vault || !isCapped(right, vault)) return <RightText right={right} />;
  const level = levelOf(vault);
  return (
    <>
      {right}, acts as {level} while you can only {level} <em>{vault.name}</em>
    </>
  );
}

/**
 * The vault others can write into, when one credential also reaches a private vault of hers (collab design §10.1,
 * §13 M6): text from others could talk her agent into copying what is private. Null where it doesn't.
 */
export function mixedVault(vaults: VaultView[] | undefined, names: string[]) {
  const covered = names.map((name) => vaults?.find((v) => v.name === name)).filter((v): v is VaultView => !!v);
  return covered.some(isPrivate) ? (covered.find(othersWrite)?.name ?? null) : null;
}

/** The warning for a credential that covers a vault others can write into and a private one. */
export function MixedWarning({ vault }: { vault: string }) {
  return (
    <p role="note" className="text-sm text-warning">
      Others can write into <em>{vault}</em>. An agent that reads their text and also reaches your private vaults could be talked into copying from them. Consider a
      separate key for shared vaults.
    </p>
  );
}
