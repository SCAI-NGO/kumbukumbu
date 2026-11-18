# List filters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A filter by name and by status, with counts per status, for the Users, Invitations and Access lists.

**Architecture:** One hook in `web/src/components/ListFilter.tsx` holds the filter state, filters the loaded entries in the browser and renders the filter bar; each list calls it with its entries and renders the visible ones. No API change.

**Tech Stack:** React 19, TypeScript, Tailwind, TanStack Query, Vitest with Testing Library, pnpm.

**Spec:** `docs/superpowers/specs/2026-11-18-list-filters-design.md`

## Global Constraints

- British English in code, comments and UI copy; source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- Everything shown when a list opens; the filter lives in component state only (no storage, no URL) and resets when the view closes.
- Matching: trimmed text, `toLocaleLowerCase()` on both sides, substring.
- Copy, exactly: "Filter by name", "Filter by note", "All", "Showing {n} of {m}", "Show all", "No {noun} match."
- Statuses and their order: Users "Active", "Deactivated"; Invitations "Open", "Used", "Expired", "Revoked"; Access "Invited", "To confirm", "Member".
- Commands: `pnpm -C web install --frozen-lockfile` once, `pnpm -C web test --run`, `pnpm -C web build`.

## Review Focus

- Whitespace-only text behaves as an empty filter (no "Showing" line, everything listed).
- A refetch that changes an entry's status (an invitation revoked while "Open" is chosen) keeps the chosen status, and the entry leaves the visible list with the counts updated.
- An invitation without a note never matches a non-empty text filter, and doesn't crash it (`note: null`).
- A status with count 0 stays in the row and can be chosen, giving "No … match." rather than an empty space.
- Names with capitals beyond ASCII ("ÄRGER" matches "ärger").

---

### Task 1: The filter hook and its bar

**Files:**
- Create: `web/src/components/ListFilter.tsx`
- Test: `web/src/components/ListFilter.test.tsx`

**Interfaces:**
- Produces:
  ```ts
  export type Status = { value: string; label: string };
  export function useListFilter<T>(items: readonly T[], options: {
    name(item: T): string;          // the text the search matches against
    status(item: T): string;        // one of statuses[].value
    statuses: readonly Status[];    // in display order
    searchLabel: string;            // "Filter by name" | "Filter by note"
    noun: string;                   // "users" | "invitations" | "members", for "No … match."
  }): { visible: T[]; bar: ReactNode };
  ```
  `bar` is `null` when `items` is empty. Otherwise it holds: a search input (accessible name = `searchLabel`); a `fieldset` with legend "Status" holding native radio inputs "All" then each status, each label showing its count (accessible names "All 3", "Open 1", …); when anything is hidden, a line "Showing {visible} of {items}" with a "Show all" button clearing both filters; when nothing is visible, "No {noun} match.". Counts are taken over the entries matching the text; "All" counts them all.

- [ ] **Step 1: Write the failing tests** in `ListFilter.test.tsx`, rendering a small test component that calls `useListFilter` on `[{n:"Anna",s:"open"},{n:"Bob",s:"used"},{n:"ÄRGER",s:"open"}]` with statuses Open/Used/Revoked and lists `visible` names:
  - `shows everything and no summary at first`: three names listed, radio "All 3" checked, no text "Showing".
  - `matches the name ignoring case`: type "anna" → only Anna; "Showing 1 of 3"; type "ärger" → ÄRGER.
  - `treats blank text as no filter`: type "   " → three listed, no "Showing".
  - `counts follow the text filter`: type "n" (only Anna matches) → radios "All 1", "Open 1", "Used 0", "Revoked 0".
  - `filters by status and keeps empty statuses choosable`: choose "Used 1" → Bob only; choose "Revoked 0" → "No things match." shown (noun "things"), list empty.
  - `Show all clears both filters`: type "bob", choose "Used 1", click "Show all" → three listed, input empty, "All 3" checked.
  - `keeps the filter when the items change`: rerender with Bob's status changed to "revoked" while "Used" is chosen → "Used" still checked, Bob gone, "No things match.".
  - `renders no bar for an empty list`: `bar` is null, no searchbox.
- [ ] **Step 2: Run** `pnpm -C web test --run src/components/ListFilter.test.tsx`. Expected: FAIL, module not found.
- [ ] **Step 3: Implement `useListFilter`** with two `useState`s (text, chosen status `"all" | string`) and `useMemo` for matches and counts. Style with `inputClass` and `labelClass` from `form.tsx`; statuses as a wrapping row of pill labels around visually hidden radios (`sr-only`), the checked one highlighted, focus ring visible. The radio `name` from `useId()`.
- [ ] **Step 4: Run** the same command. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add a filter by name and status for long lists"` with both files.

### Task 2: Wire the filter into Users, Invitations and Access

**Files:**
- Modify: `web/src/routes/admin/Users.tsx` (the list in `Users`, around line 62)
- Modify: `web/src/routes/admin/Invitations.tsx` (the list in `Invitations`, around line 39)
- Modify: `web/src/shell/AccessList.tsx` (the members list in `AccessList`, around line 345; the Departures block stays outside the filter)
- Test: `web/src/routes/admin/Users.test.tsx`, `web/src/routes/admin/Invitations.test.tsx`, `web/src/shell/AccessList.test.tsx`

**Interfaces:**
- Consumes: `useListFilter` from Task 1.
- Users: `name = u => \`${u.display} ${u.username}\``, `status = u => u.active ? "active" : "deactivated"`, statuses Active/Deactivated, `searchLabel "Filter by name"`, `noun "users"`.
- Invitations: `name = i => i.note ?? ""`, `status = i => i.state`, statuses open/Open, used/Used, expired/Expired, revoked/Revoked, `searchLabel "Filter by note"`, `noun "invitations"`. The bar goes below "New invitation"; "No invitations yet." stays for an empty list.
- Access: `name = m => m.label`, `status = m => m.status === "active" ? "member" : m.redeemedAt ? "confirm" : "invited"`, statuses invited/Invited, confirm/To confirm, member/Member, `searchLabel "Filter by name"`, `noun "members"`. The bar goes above the members list; "{vault} isn't shared with anyone." stays for none.

- [ ] **Step 1: Write one failing test per view**, using each file's existing fixtures and API fakes:
  - Users `filters users by name and status`: with two users (one deactivated), typing the second's username lists only them; choosing "Deactivated 1" after clearing lists only the deactivated one.
  - Invitations `filters invitations by note and status`: with the existing `used` (note null) and `open` ("For Bob") fixtures, typing "bob" lists only the open one; choosing "Used 1" after "Show all" lists only the used one.
  - Invitations `keeps the chosen status after a revoke`: choose "Open 1", revoke it (the fake returns the invitation as revoked on the refetch) → "Open" still checked, "No invitations match." shown.
  - Access `filters members by name and status`: with members Bob (active) and Carol (`status: "pending", redeemedAt: null`), choosing "Invited 1" lists only Carol; typing "bob" with "All" lists only Bob.
- [ ] **Step 2: Run** `pnpm -C web test --run src/routes/admin src/shell/AccessList.test.tsx`. Expected: the four new tests FAIL (no searchbox).
- [ ] **Step 3: Wire `useListFilter` into the three views**: call it at the top of each component with the loaded entries (`?? []`), render `bar` where the Interfaces block says, and map over `visible` instead of the full list.
- [ ] **Step 4: Run** the full web suite and the build: `pnpm -C web test --run` and `pnpm -C web build`. Expected: all pass (known exception: the two `src/skillpage/SkillPage.test.tsx` failures that also fail on `master` in the build session).
- [ ] **Step 5: Commit** `git commit -m "Filter the Users, Invitations and Access lists"` with the six files.
