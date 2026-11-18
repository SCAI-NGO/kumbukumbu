# Windows over the notes

Status: design, agreed with the maintainer on 2026-11-17 (UX review included). Target: 0.6.0.

## Why

Settings, Access, Skills, the admin pages, the console, Recent and What's on are full pages today. Each replaces the notes, starts with "Back to the notes", and throws the user out of what they were doing. They are places one visits for a moment, not places one works in. They become windows over the notes: the note stays where it was, dimmed behind, and closing the window returns to it.

## Goals

- Settings (with Access and Skills), Admin, Console, Recent and What's on open as windows over the note shell.
- Two window shapes on the desktop, one sheet shape on the phone, each behaving the same wherever it is used.
- Every existing address keeps working: links, bookmarks, a reload, a deep link.
- A console that can glow green or amber, for the vibe.

## Non-goals

- The workspaces stay full pages: Board, Calendar, Gallery, the lightbox (`/view`), checkpoints. They need the room.
- Pages before the app (sign-in, join, consent, share, add skill) stay as they are.
- No new settings, no changed content inside the pages beyond what the frame needs.
- No swipe gestures for the window or its navigation.

## Anti-goals

- A window that loses typed input or a secret on a stray click.
- A window that jumps in size between its pages.
- A CRT effect that hurts reading: no flicker, no curved glass, no colour fringes.
- A discreet vault's name reaching an address or storage it doesn't reach today.

## The shapes

| Shape | Used by | Desktop size | Click on the dimmed notes | Frame |
|---|---|---|---|---|
| **Fixed window with navigation** | Settings, Admin | 960 × 720 px | does nothing | robust |
| **Fixed window without navigation** | Console | 1100 × 760 px | does nothing | robust |
| **Light window** | Recent, What's on | 672 × 720 px | closes the window | light |

Every size is capped at the viewport less 32 px on each side. The window is centred, fixed in size, and only its content scrolls. Why fixed: switching from "Appearance" to "Security" must not make the window jump. On a 3840 × 1600 screen the navigated window takes a quarter of the width and a bit under half of the height, which the maintainer found right.

**Frames.** The difference tells the eye before the first click whether a stray click is safe, as desktop environments set modal and non-modal windows apart. It carries the feel only, no information: ✕ and Esc work the same everywhere, so nothing is lost for anyone who doesn't see it.

| | Robust | Light |
|---|---|---|
| Border | 2 px `line`, strong shadow | 1 px `line`, soft shadow |
| Title bar | its own surface (`surface-2`), a line below | the content's surface, a hairline below |
| Backdrop | dimmed about 60 % | dimmed about 35 % |
| Corners | 14 px | 14 px |

**Title bar.** 56 px high; the title on the left, ✕ on the right as a 48 px target (`aria-label="Close"`). The console's scheme switch sits beside ✕.

**History.** A window is one entry in the browser's history, however much one moves inside it: every move inside a window (a navigation entry, an in-content tab of Access or Skills, Admin's Users and Invitations) replaces the address rather than pushing one. Why: Back must always close the window, not walk its pages; and closing then leaves no entry behind that Back would reopen. Today's tabs push on click and replace on the arrow keys only (`Access.tsx:45,57`, `Skills.tsx:61,69`, `AdminOnly.tsx:34-37`); inside a window they replace on both.

**Closing.** ✕, Esc and the browser's Back (the phone's back gesture) close every window; a click on the backdrop closes the light ones only. Why: in Settings one types passwords, makes keys and stands in the middle of "Delete account"; in Admin one writes invitations; the console holds a session. Esc closes the topmost dialog first (a dialog opened inside a window, such as Export, as `useDialog` already does).

### On the phone (below the desktop breakpoint)

A sheet in the full width, from a strip at the top down to the bottom edge, as iOS's page sheet:

```
┌─────────────────────┐
│ ░░ note, dimmed ░░  │  ← about 6 %, at least 32 px, plus the safe area
╭─────────────────────╮
│ Settings · Security ✕│  ← title bar, top corners rounded 16 px
├─────────────────────┤
│›                    │
│  content            │  ← the tab for the navigation, left
│                     │
└─────────────────────┘  ← flush with the bottom, safe-area padding
```

Why no strip at the bottom: the bottom is the phone's most valuable room, and with the keyboard up (a password, Recent's field) a strip there would cost height twice. The top strip is enough to show the note is still there. The sheet slides up in about 200 ms; without motion under `prefers-reduced-motion`.

**The navigation's tab.** A slim bar at the left edge with a clearly visible arrow (›) that says "something opens here", vertically centred, a 48 × 72 px target. A tap slides the navigation in from the left inside the sheet (about 75 % wide), dims the content beside it and turns the arrow (‹). Choosing an entry goes there and folds the navigation away; a tap on the dimmed content, the arrow or Esc folds it without going anywhere. No edge swipe: it is the system's back gesture on iOS and Android, and Back closes the whole window here. Since the navigation is mostly out of sight, the title names the entry: "Settings · Security". On the desktop the title is "Settings"; the navigation marks the entry.

## Settings

One window, a page per navigation entry, each with its own address. Why pages and not one long page with jump links: one sees a few sections instead of today's long roll, and on the phone the tab would otherwise have little to do.

The order is the maintainer's, with his reasons:

| Entry | Address | Sections (today's, unchanged) |
|---|---|---|
| Appearance | `/settings/appearance` | Colour scheme, Changes made elsewhere, Downloads, Install as an app |
| Vaults | `/settings/vaults` | Vaults, Shared vaults, Published notes |
| Access | `/settings/access/:tab` | today's Access page: API keys · Apps · WebDAV tabs |
| Skills | `/settings/skills/:tab` | today's Skills page: Installed · Try · Shared · Marketplaces tabs |
| Calendar | `/settings/calendar` | Calendar |
| Security | `/settings/security` | Password, Discreet vaults, Recovery key, Re-encrypt my vaults, Sessions |
| Your data | `/settings/data` | Your data, Delete account |
| About | `/settings/about` | About |

Why this order: Kumbu² is built around agents, so Access stands high; Skills follow closely, they matter; the calendar's settings are rarely changed.

Access and Skills keep their tabs inside the content (my call): the navigation stays eight calm entries, and the tabs keep their reveal handling and their tests.

**Old addresses** redirect, with `replace`: `/settings` → `/settings/appearance`; `/settings#install` → `/settings/appearance#install`; `/settings#discreet-vaults` → `/settings/security#discreet-vaults`; `/access`, `/keys`, `/access/:tab` → `/settings/access/…`; `/skills`, `/skills/:tab` → `/settings/skills/…`. `/skills/add` is not a window and stays where it is (it is before the app). The in-app links (`TreeDrawer.tsx:446-468`, `InstallHint.tsx:70`, `PinDialog.tsx:86`) point at the new addresses directly.

## Admin

A window of its own, the same shape as Settings, with the entries Users (`/admin/users`) and Invitations (`/admin/invitations`); today's tab row in `AdminOnly.tsx` becomes the navigation. Why separate from Settings: an admin at work uses it more often than the settings, as more people ask for accounts, and a window of its own is quicker to reach. A non-admin sees the window with "Only an admin can see this." as today.

## Console

A robust window without navigation, 1100 × 760 px; on the phone the sheet. Its content is today's `ConsolePage` without its header.

**Scheme switch** in the title bar: `Plain · Green · Amber` as a segmented control; on the phone one button that cycles and shows the current name. Kept per device in `localStorage` (guarded), not in the account: it is taste.

| | Plain | Green | Amber |
|---|---|---|---|
| Text | as today, from the scheme | `#33ff66` | `#ffb000` |
| Ground | as today | `#030a05` | `#0a0600` |
| Dimmed (path, `>`) | `muted` | the phosphor colour, dimmed, AA against the ground | likewise |

In Green and Amber:

- **Glow:** a light `text-shadow` in the phosphor colour; the text stays sharp.
- **Scanlines:** a line pattern at about 6 % opacity over the output, `pointer-events: none`, so selecting and copying work as before.
- **Cursor:** a block cursor, blinking only without `prefers-reduced-motion`.
- **Errors:** a phosphor screen has one colour, so errors show in inverse video (light bar, dark text) with the word "error". Status never by colour alone: the word "Error:" is visible in every scheme, Plain included, so an error never rests on its colour.
- **Frame:** the title bar and border keep the app's look; only the inside glows, a screen in a case.
- Left out on purpose: flicker, curvature, colour fringes. Enough feeling; more would break usability.

## Recent and What's on

Light windows, 672 × 720 px; their content as today without their header (What's on keeps its tag filter, in the title bar beside ✕). Opening a note from either closes the window and shows the note. Links that lead to a workspace (What's on's "couldn't read" link to the calendar, a board link) close the window and go there.

## A secret on show

While a window shows a secret once, the navigation stays visible but locked: dimmed, `aria-disabled`, not clickable, with a short line above its entries ("Save the key first", worded per secret); on the phone the tab is locked too. Why not hidden, as the Access tabs are today: on the desktop the window would lose its left column and jump. ✕, Esc and Back close the window through a navigation, so the existing `useLeaveGuard` catches them and asks.

Which reveals lock (my call): every secret shown once inside a window, the same set that hides the Access tabs today and more. That is a new key (`KeyReveal`), a connector's details (`ConnectorReveal`, which locks although it deliberately has no leave guard, as the tabs hide for it today), a WebDAV password (`DavShares`, `DavConnect`), the recovery key in Security (both `RecoveryKeyReveal`s, `Settings.tsx:325,382`), an invitation link (`InvitationReveal`) and a skill's share link (`skills/Shared.tsx`). Each reports through `onRevealChange`/`useReveal` to the window frame, which holds the lock.

## A discreet vault hiding while a window is open

A window can be open over a note of a discreet vault when the vault hides again (the clock, ✕ on the reveal, another device). `hideAgain` and `leftElsewhere` navigate only when the address names the vault or shows the neutral address, and a window's address does neither. So the shell's memory of the location behind follows `useDiscreet` itself (my call): when the vault of the note behind is gone, the location behind becomes `/` (the vault list), the window stays open, and nothing typed in it is lost. Why not close the window: the anti-goal of losing input; and the window's own content already follows the discreet state as the pages do today. The discreet code itself stays untouched.

The same holds when the note behind a window moves or goes because another session renames or hides its vault: the window stays open and ✕ shows the new place (the renamed note, or the vault list), and the old address stays in the history before it, as it does without a window. Why: the sync socket's moves are not the person's, so they mustn't cost what she typed.

## The how (approach A, chosen)

Three ways were considered:

- **A, the window routes as children of the shell (chosen).** `/settings/*`, `/admin/*`, `/console`, `/recent` and `/whats-on` move under `MobileShell` (`router.tsx:124`). The shell renders its `<Outlet/>` inside the window frame. Why: clean addresses, every link stays valid, and the shell stays mounted, so Write mode and its draft survive a window; the discreet logic is untouched, as no window address names a vault.
- **B, React Router's background-location pattern.** The shell stays untouched, but the pattern fits the data router badly, and a reload or deep link has no background anyway, so it needs A's fallback as well: two ways instead of one.
- **C, the window as a query parameter on the note's address** (`/notes/Work/x.md?window=settings`). The shell stays untouched, but addresses get ugly, sub-pages become parameters, old links need redirects, and the discreet mode's neutral address would carry parameters, which we don't want to touch.

**The shell behind a window.** The shell derives today's note from the address (`MobileShell.tsx:146-190`: `path`, the start logic around `lastNote`, `overview`). With a window open the address names no note, and that must not read as "no note":

- Navigating from a note (or the vault list) to a window keeps showing what was shown: the shell holds the last shown location in memory and renders that while a window route is active.
- A reload or deep link straight onto a window starts the shell as `/` does (the kept last note, else the vault list) and opens the window over it.
- Closing navigates to the location behind (its path, search and state), with `replace` where the window was reached in-app so that Back after closing doesn't reopen it; a window reached by deep link closes to `/`.
- The last-note keeping, the start settlement and the discreet effects keep working on the note shown, not on the address.

This part is the risky one and is built test-first.

The update bar: `Root` shows it above every page outside the shell (`router.tsx:43`); window routes are inside the shell now and use the shell's bar. While a window is open the bar sits behind it, `inert` with the rest of the shell, and is reachable again once the window closes; the update is not urgent enough to be worth a way through the modal.

The frame: one component for both shapes (`shape: "navigated" | "plain"`, `frame: "robust" | "light"`), `role="dialog"`, `aria-modal`, labelled by its title, using `useDialog` for focus, Tab and Esc, the shell behind it `inert`. `AccountScreen` (`components/form.tsx:20`) goes; its users render into the window.

## Tests

Test-first (Vitest), at least:

- Each window opens over the note shown; ✕, Esc and Back return to that note, with Write mode and a draft intact.
- A reload of `/settings/security`, `/console`, `/recent` shows the window over the kept last note, or over the vault list.
- The backdrop closes light windows only.
- Moving inside a window (entries, tabs) adds no history entry: Back after Appearance → Security → Access closes the window, and Back after closing doesn't reopen it.
- A discreet vault hiding while a window is open over its note: the window stays, ✕ then shows the vault list.
- The redirects of every old address, anchors included.
- The navigation: current entry marked (`aria-current="page"`), the phone's tab opens and folds it, choosing folds it.
- A secret on show locks the navigation and the tab; ✕, Esc and Back ask before leaving.
- The console's scheme switch, kept per device, and errors in words in every scheme.
- The existing tests that click "Back to the notes" move to "Close".

## Left to the builder

- Where the shell keeps "the location behind" and how the window routes are recognised.
- The exact phosphor glow and dimmed colours, within AA.
- The wording of the lock lines per secret.
- Splitting `Settings.tsx` into a file per entry, if that reads better.
