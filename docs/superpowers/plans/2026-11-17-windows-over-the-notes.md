# Windows over the notes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Settings (with Access and Skills), Admin, Console, Recent and What's on open as windows over the note shell instead of replacing it.

**Architecture:** A new layout route, `WindowHost`, takes the shell's place in `router.tsx`. It renders `MobileShell` against the *location behind* (the last non-window location, kept in a small store) and, on a window route, the window over it through its `<Outlet/>`. One frame component, `WindowFrame`, draws all three kinds; a navigation pane and a lock serve Settings and Admin.

**Tech Stack:** React 19, React Router 7 data router, zustand, Tailwind 4, lucide-react, Vitest + Testing Library. pnpm in `web/`.

**Spec:** `docs/superpowers/specs/2026-11-17-windows-over-the-notes-design.md` (read it first; it holds the reasons).

## Global Constraints

- British English in code, comments, UI copy and commits; source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`; match the surrounding comment density and idiom.
- Desktop/phone split is `useDesktop()` (`web/src/hooks/useDesktop.ts`, `lg` = 1024 px). Below it, the phone sheet.
- Sizes, each capped at the viewport less 32 px per side (`min(Wpx, 100vw - 64px)`, `min(Hpx, 100dvh - 64px)`): navigated **960 × 720**, console **1100 × 760**, light **672 × 720**. Navigation pane **220 px**. Title bar **56 px**; ✕ a **48 px** target, `aria-label="Close"`.
- Frames: robust = **2 px** `border-line`, strong shadow, title bar on `bg-surface-2` with a line below, backdrop **black 60 %**; light = **1 px** `border-line`, soft shadow, title bar on the content's surface with a hairline below, backdrop **black 35 %**. Corners **14 px** (desktop), top corners **16 px** (phone).
- Phone sheet: full width, top at `max(32px, 6dvh)` plus `env(safe-area-inset-top)`, flush with the bottom with safe-area padding; slides up in **200 ms**, no motion under `prefers-reduced-motion` (`motion-safe:`).
- Phone navigation tab: slim bar at the left edge, a visible `ChevronRight` (open: `ChevronLeft`), target **48 × 72 px**, vertically centred; the pane slides in at **75 %** width and dims the content. No swipe gestures.
- Closing: ✕, Esc, Back close every window; a backdrop click closes **light** windows only. Esc closes the topmost dialog first (`useDialog`'s stack).
- History: a window is **one** history entry. Every move inside a window replaces. Closing a window reached in-app is `navigate(-1)`; one reached by a deep link or reload closes with `navigate("/", { replace: true })`. (This meets the spec's "Back after closing doesn't reopen it" without leaving a duplicate entry.)
- Links from inside a window to a non-window location (a note, the lightbox, the calendar, a board) navigate with `replace`, so the window's entry becomes that place.
- Console colours: Green text `#33ff66` on `#030a05`, Amber `#ffb000` on `#0a0600`; dimmed parts AA against the ground. Kept per device in `localStorage` key `kumbu.consoleScheme` (`"plain" | "green" | "amber"`), every access in `try`.
- Tests: `pnpm -C web test --run <file>`; whole suite `pnpm -C web test --run`; build `pnpm -C web build`. Lint/type: the build runs `tsc`.

## Review Focus

1. **Write mode with an unsaved draft**, then Settings opened and closed: still Write mode, draft intact, no reload of the note. → Task 1 test.
2. **Deep link onto a window on a fresh device** (no kept note): the window over the vault list; ✕ shows the vault list, never a blank shell. → Task 1 test.
3. **A window opened over a discreet note** (the address bar shows `/discreet`): closing returns to that note, and the window's own address never names the vault. → Task 1 test.
4. **A secret on show and Back pressed**: the leave dialog asks; "Stay" keeps the window *and* the secret, "Leave" closes the window. → Task 5 test.
5. **Opening a note from Recent, then Back**: Back shows the note before Recent was opened, not the Recent window. → Task 6 test.

## File structure

- `web/src/windows/store.ts` — the location behind, how the window was reached, the navigation lock.
- `web/src/windows/WindowHost.tsx` — the layout route: shell against the location behind, window outlet, close logic.
- `web/src/windows/WindowFrame.tsx` — the frame of all three kinds, desktop and phone.
- `web/src/windows/WindowNav.tsx` — the navigation pane and the phone tab.
- `web/src/windows/context.ts` — `useInWindow()`, `useCloseWindow()`, `useWindowLock()`.
- `web/src/routes/settings/` — `SettingsWindow.tsx` (frame + nav + outlet), one file per entry (`Appearance.tsx`, `Vaults.tsx`, `Calendar.tsx`, `Security.tsx`, `Data.tsx`, `About.tsx`), `Section.tsx`, `redirects.tsx`. `routes/Settings.tsx` is split into these and removed.
- `web/src/routes/admin/AdminWindow.tsx` replaces `AdminOnly.tsx`'s frame.
- `web/src/console/crt.ts` + changes to `ConsolePage.tsx`.
- `components/form.tsx` loses `AccountScreen`.

---

### Task 1: The window host and the location behind

**Files:**
- Create: `web/src/windows/store.ts`, `web/src/windows/WindowHost.tsx`, `web/src/windows/context.ts`, `web/src/windows/WindowFrame.tsx` (minimal: title bar, ✕, Esc, backdrop rule, sizes per kind; phone sheet and nav come in Tasks 2–3)
- Modify: `web/src/router.tsx:43,124-145`
- Test: `web/src/windows/WindowHost.test.tsx`

**Interfaces:**
- Produces:
  - `type WindowKind = "navigated" | "console" | "light"`
  - `type Behind = { pathname: string; search: string; hash: string; state: unknown }`
  - `useWindows` (zustand): `{ behind: Behind | null; inApp: boolean; lock: string | null }`
  - `useInWindow(): boolean` — true inside a window's outlet.
  - `useCloseWindow(): () => void` — closes per the History constraint.
  - `WindowFrame(props: { kind: WindowKind; title: string; heading?: string; nav?: ReactNode; actions?: ReactNode; children: ReactNode })` — `role="dialog"`, `aria-modal="true"`, labelled by its title; `heading` is the phone's "Settings · Security" suffix; `actions` sit left of ✕.
  - Window routes carry `handle: { window: true }`; `WindowHost`'s own route carries `handle: { shell: true }` (Root uses it for `inShell`).

- [ ] **Step 1: Write the failing tests** in `WindowHost.test.tsx`, using `renderApp`/`fakeApi`/`signedInApi` as `shell/MobileShell.test.tsx` does. Move `/console` under the host in this task (its page unchanged inside a `WindowFrame kind="console" title="Console"`) and test with it:
  - `opens over the note shown`: at `/notes/Work/inbox.md`, follow the drawer's Console link → `getByRole("dialog", { name: "Console" })` and the note's text still in the document.
  - `closes back to the note with ✕, Esc and Back` (three cases): router location afterwards `/notes/Work/inbox.md`, and `router.state.historyAction`/index shows no entry left forward that is the console when Back was used (`router.navigate(-1)` then location is the note).
  - `keeps Write mode and its draft`: set Write mode, type into the editor, open Console, close → `useEditor.getState().mode === "write"` and the typed text present (Review Focus 1).
  - `a deep link shows the window over the kept note`: `localStorage` kept note set, `renderApp("/console")` → dialog and the kept note's text; ✕ → `/notes/Work/inbox.md` with `replace`.
  - `a deep link on a fresh device closes to the vault list` (Review Focus 2): no kept note, `renderApp("/console")`, ✕ → location `/` and the vault list visible.
  - `over a discreet note, closing returns to it` (Review Focus 3): reveal a discreet vault as `TreeDrawerDiscreet.test.tsx` does, open its note, open Console; the router location is `/console`, closing shows the discreet note again.
  - `a vault hiding while a window is open moves the location behind to /`: open Console over a discreet note, end the reveal (`useDiscreet` as `discreet/*` tests do) → dialog still open; ✕ → `/` and nothing names the vault.
- [ ] **Step 2: Run them** — `pnpm -C web test --run src/windows/WindowHost.test.tsx` — expected FAIL (module not found).
- [ ] **Step 3: Implement** `store.ts`, `context.ts`, `WindowHost.tsx`, minimal `WindowFrame.tsx`, and move `/console` under the host in `router.tsx`. `WindowHost`: `useMatches()` decides whether a window route is active; on a non-window location it writes `behind` (pathname, search, hash, state) and `inApp = true` for the next window; on a window reached without a `behind` (deep link) it sets `behind = { pathname: "/", … }`, `inApp = false`. It renders `MobileShell` inside `<Routes location={behindLocation}><Route path="*" element={<MobileShell />} /></Routes>` so every `useLocation`/`useMatch` in the shell sees the location behind; if React Router warns about the parent path lacking `*`, give the host's notes children `path: "/notes/:vault/*"` and `"/"` as today and check the warning is gone. The shell's subtree gets `inert` while a window is open. Subscribe to `useDiscreet`: when `behind` names a vault (`vaultOfRoute`) in `gone`, set `behind` to `/`. Root's `inShell` (`router.tsx:43`) becomes "a match has `handle.shell`". Esc and focus via `useDialog`.
- [ ] **Step 4: Run** the new tests and `src/shell/MobileShell.test.tsx`, `src/console/ConsolePage.test.tsx` — expected PASS (update the console tests' "Back to the notes" to "Close").
- [ ] **Step 5: Commit** — "Open the console as a window over the notes".

### Task 2: The frame's look, desktop and phone

**Files:**
- Modify: `web/src/windows/WindowFrame.tsx`
- Test: `web/src/windows/WindowFrame.test.tsx`

**Interfaces:** Consumes Task 1's `WindowFrame` props unchanged.

- [ ] **Step 1: Write the failing tests** (render `WindowFrame` in a memory router; `useDesktop` via `matchMedia` stub as `DockedSidebar.test.tsx` does):
  - `robust kinds don't close on a backdrop click; light ones do` (`navigated`, `console` vs `light`).
  - `the frame marks its kind` — `data-frame="robust"` for `navigated`/`console`, `"light"` for `light`; desktop size classes per kind (assert the class strings carrying 960/1100/672 and 720/760).
  - `on the phone it is a sheet` — `data-sheet` present below `lg`; the title reads `"Settings · Security"` when `heading="Security"`; on the desktop only `"Settings"`.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement** the Global Constraints' frames, sizes, sheet geometry, `motion-safe` slide-in. Backdrop is a sibling element under the window, `aria-hidden`.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — "Give the windows their frames".

### Task 3: The navigation pane and the lock

**Files:**
- Create: `web/src/windows/WindowNav.tsx`
- Modify: `web/src/windows/WindowFrame.tsx`, `web/src/windows/context.ts`
- Test: `web/src/windows/WindowNav.test.tsx`

**Interfaces:**
- Produces:
  - `type NavEntry = { to: string; label: string; match: string }` — `match` is the path prefix that marks it current.
  - `WindowNav(props: { label: string; entries: NavEntry[] })` — pass as `WindowFrame`'s `nav`; links navigate with `replace`; current entry `aria-current="page"`.
  - `useWindowLock(text: string | null): void` — while non-null, entries are `aria-disabled="true"`, not followable, dimmed, with `text` shown above them; the phone tab is disabled.
- Consumes: `useWindows.lock` from Task 1.

- [ ] **Step 1: Write the failing tests**:
  - `marks the current entry and replaces on a move` — two entries; click the second → `aria-current` moves, `router.state.historyAction === "REPLACE"`.
  - `the phone tab opens and folds the pane` — tab `getByRole("button", { name: "Show navigation" })` with `aria-expanded`; open → entries visible; choosing one folds it; Esc folds it without closing the window; a tap on the dimmed content folds it.
  - `a lock disables the entries and the tab` — a test component calling `useWindowLock("Save the key first")` → text visible, clicking an entry doesn't navigate, tab disabled; unmount → unlocked.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.** Desktop: a 220 px column under the title bar, full height. Phone: the tab and an in-sheet pane per Global Constraints. Esc on the open pane must be handled before `useDialog` closes the window (stop it in a capture listener while the pane is open).
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — "Add the windows' navigation and its lock".

### Task 4: Settings as a window, Access and Skills inside it

**Files:**
- Create: `web/src/routes/settings/SettingsWindow.tsx`, `Section.tsx`, `Appearance.tsx`, `Vaults.tsx`, `Calendar.tsx`, `Security.tsx`, `Data.tsx`, `About.tsx`, `redirects.tsx`
- Delete: `web/src/routes/Settings.tsx` (its sections move unchanged; `Settings.test.tsx` moves to `routes/settings/*.test.tsx` by entry)
- Modify: `web/src/routes/Access.tsx`, `web/src/routes/Skills.tsx`, `web/src/router.tsx`, `web/src/shell/TreeDrawer.tsx:446-468`, `web/src/update/InstallHint.tsx:70`, `web/src/discreet/PinDialog.tsx:86`, and every `YourDataSection` importer
- Test: `web/src/routes/settings/SettingsWindow.test.tsx`, `web/src/routes/settings/redirects.test.tsx`

**Interfaces:** Consumes `WindowFrame`, `WindowNav`, `NavEntry`, `useInWindow`.

Entries, in this order, title "Settings": Appearance `/settings/appearance` (Colour scheme, Changes made elsewhere, Downloads, Install as an app), Vaults `/settings/vaults` (Vaults, Shared vaults, Published notes), Access `/settings/access/:tab`, Skills `/settings/skills/:tab`, Calendar `/settings/calendar`, Security `/settings/security` (Password, Discreet vaults, Recovery key, Re-encrypt my vaults, Sessions), Your data `/settings/data` (Your data, Delete account), About `/settings/about`. Section ids `install` and `discreet-vaults` stay.

- [ ] **Step 1: Write the failing tests**:
  - `lists the eight entries in order` — the nav's link names equal `["Appearance","Vaults","Access","Skills","Calendar","Security","Your data","About"]`.
  - `each entry shows its sections only` — at `/settings/security` headings Password…Sessions present, "Colour scheme" absent.
  - Redirects (`it.each`, location after, `replace`): `/settings`→`/settings/appearance`; `/settings#install`→`/settings/appearance#install`; `/settings#discreet-vaults`→`/settings/security#discreet-vaults`; `/access`→`/settings/access/keys`; `/keys`→`/settings/access/keys`; `/access/apps`→`/settings/access/apps`; `/skills`→`/settings/skills/installed`; `/skills/try`→`/settings/skills/try`; `/skills/add` still renders AddSkill.
  - `Access and Skills tabs replace inside the window` — click a tab → `historyAction === "REPLACE"`, Back closes the window.
  - Drawer links point at `/settings/appearance`, `/settings/access/keys`, `/settings/skills/installed`.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.** `SettingsWindow` is a pathless layout route with `handle: { window: true }`, `WindowFrame kind="navigated" title="Settings" heading={current entry label}`. Access and Skills drop `AccountScreen`, their tabs' `Link`s get `replace`, their internal `Navigate`s point at `/settings/…`. Anchors: keep whatever `Settings.tsx` does today for scrolling to a hash.
- [ ] **Step 4: Run** the new tests and the moved `Settings`, `Access`, `Skills`, `Keys`, `Connections`, `DavShares`, `MobileShell` tests — PASS.
- [ ] **Step 5: Commit** — "Make the settings a window with a navigation".

### Task 5: Admin as a window, and every reveal locks

**Files:**
- Create: `web/src/routes/admin/AdminWindow.tsx`
- Modify: `web/src/routes/admin/AdminOnly.tsx` (keeps `useIsAdmin` and the non-admin message, loses the frame), `Users.tsx`, `Invitations.tsx`, `router.tsx`; reveals: `routes/KeyReveal.tsx`, `routes/ConnectorReveal.tsx`, `routes/DavShares.tsx`, `routes/DavConnect.tsx`, `components/RecoveryKeyReveal.tsx` (both uses in Security), `routes/admin/InvitationReveal.tsx`, `skills/Shared.tsx`
- Test: `web/src/routes/admin/AdminWindow.test.tsx`, additions to the reveals' existing tests

**Interfaces:** Consumes `useWindowLock`. Admin entries: Users `/admin/users`, Invitations `/admin/invitations`; title "Admin".

Lock texts (copy): new key "Save the key first"; connector "Copy the connector's details first"; WebDAV "Save the password first"; recovery key "Save the recovery key first"; invitation "Copy the invitation link first"; skill share "Copy the share link first".

- [ ] **Step 1: Write the failing tests**:
  - `Admin is a window with Users and Invitations`, moving between them replaces; a non-admin sees "Only an admin can see this." inside the window.
  - Per reveal (`it.each` over the seven): while shown, the window's nav entries are `aria-disabled` and the lock text visible; after Done they are enabled.
  - Review Focus 4: new key shown, `router.navigate(-1)` → the leave dialog; "Stay" → window and key still shown; again and "Leave" → window closed.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.** Each reveal calls `useWindowLock(shown ? text : null)` (a no-op outside a window). Access's tab hiding on reveal stays as today.
- [ ] **Step 4: Run** — PASS, plus `Users`, `Invitations`, `Keys`, `DavShares`, `Shared` tests.
- [ ] **Step 5: Commit** — "Make admin a window, and lock the navigation while a secret is shown".

### Task 6: Recent and What's on as light windows

**Files:**
- Modify: `web/src/recent/RecentPage.tsx:78-85,165-172`, `web/src/calendar/WhatsOnPage.tsx:31-40,85`, `web/src/calendar/EntryItem.tsx:66-68`, `web/src/calendar/CouldntRead.tsx:50-57`, `web/src/calendar/EntryLine.tsx` (its links), `router.tsx`
- Test: `web/src/recent/RecentPage.test.tsx`, `web/src/calendar/WhatsOnPage.test.tsx`

**Interfaces:** Consumes `WindowFrame kind="light"`, `useInWindow`. What's on passes its `TagFilter` as `actions`.

- [ ] **Step 1: Write the failing tests**:
  - Both open as `dialog` named "Recent changes" / "What's on" over the note; a backdrop click closes them.
  - Review Focus 5: from note A open Recent, open note B from it → location B, window gone; `router.navigate(-1)` → note A.
  - What's on: ticking Done keeps the window open; the calendar link closes it and shows the calendar (replace); a note opened from it keeps its "What's on" way back (`cameFrom`), and following that reopens the window.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.** Drop both headers; links to non-window places get `replace={inWindow}` (EntryItem and EntryLine are shared with the calendar page, where `useInWindow()` is false).
- [ ] **Step 4: Run** — PASS, plus `calendar/*` tests.
- [ ] **Step 5: Commit** — "Open Recent and What's on as windows".

### Task 7: The CRT console

**Files:**
- Create: `web/src/console/crt.ts`
- Modify: `web/src/console/ConsolePage.tsx`, `web/src/index.css` (scanline and glow utilities, if not expressible inline)
- Test: `web/src/console/ConsolePage.test.tsx`

**Interfaces:**
- Produces: `type ConsoleScheme = "plain" | "green" | "amber"`; `storedConsoleScheme(): ConsoleScheme`; `keepConsoleScheme(s: ConsoleScheme): void`.

- [ ] **Step 1: Write the failing tests**:
  - Desktop: a group "Console scheme" with three radio-like buttons Plain/Green/Amber (`aria-pressed`) in the title bar; choosing Amber sets `data-scheme="amber"` on the output and `localStorage["kumbu.consoleScheme"] === "amber"`; a fresh render starts in Amber; storage throwing → Plain, no crash.
  - Phone: one button whose name includes the current scheme; a click cycles Plain → Green → Amber → Plain.
  - An error entry in Green carries the word "error" and `data-error` (inverse video); selecting output text is still possible (scanline layer `pointer-events-none`, `aria-hidden`).
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement** the Global Constraints' colours; glow via `text-shadow` in the phosphor colour; scanlines a `repeating-linear-gradient` at ~6 % opacity; block cursor blinking only under `motion-safe`. No flicker, curvature or fringes. Verify AA of the dimmed colours with a quick contrast calculation and note the ratios in a comment.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — "Let the console glow green or amber".

### Task 8: Clean up and verify

**Files:**
- Modify: `web/src/components/form.tsx` (remove `AccountScreen`), any test still naming "Back to the notes" for a window page (`MobileShell.test.tsx:444,559`, `Users.test.tsx:471`), `README.md` if it describes these pages.

- [ ] **Step 1:** `rg -n "AccountScreen|Back to the notes" web/src` — only workspace pages (Board, Calendar, Gallery) may remain.
- [ ] **Step 2:** `pnpm -C web test --run` — all pass; `pnpm -C web build` — succeeds.
- [ ] **Step 3:** Screenshots with Playwright at 1440 × 900 and an iPhone profile: Settings · Security, Admin, Console Green and Amber, Recent; in one dark and one light scheme. Look at them; fix what looks wrong.
- [ ] **Step 4: Commit** — "Remove the account page frame".
