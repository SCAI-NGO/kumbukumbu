# Note actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Duplicate a note, reach a note's actions from the command palette, and open the note's menu from the keyboard.

**Architecture:** All in the web app. A pure module makes the copy's text and name; one hook builds the note's menu items for the dock, the keyboard menu and the palette alike; a point-positioned menu shares the dock menu's list; the palette gains a Note group of rows that run a function.

**Tech Stack:** React 19, TypeScript, Tailwind, Zustand, TanStack Query, Vitest (Write mode is a plain textarea).

**Spec:** `docs/superpowers/specs/2026-11-18-note-actions-design.md` (read it first). Stacked on the command palette (`docs/superpowers/specs/2026-11-18-command-palette-design.md`, branch `claude/trusting-davinci-0ml2g8`, #237).

## Global Constraints

- British English in code, comments and UI copy; `// SPDX-License-Identifier: AGPL-3.0-or-later` at the top of every new source file.
- Copy fixed by the spec: menu item "Duplicate" (lucide `CopyPlus`); name "Copy of <name>.md", then "Copy of <name> 2.md", … up to 99, then "There are already 99 copies of <name>; rename one and try again."; notice "Copy of <name>.md created" with **Open**; palette group "Note"; the palette rows carry the menu's labels verbatim.
- Removed from the copy: `when`, `until`, `due`, `task`, `upcoming`, `tz`, `closed`, `reason`, `rank`, `moved`. Kept: everything else, `column` included.
- Keys: Shift+F10 and `ContextMenu` open the note's menu; never over a dialog (`topDialog()`), the palette or another menu, never without an open note. No Ctrl+Space, no right-click override.
- No Duplicate for notes under `_archive/` or where the folder isn't writable.
- Note rows in the palette: no star, Ctrl+D does nothing, not in the empty palette.
- Tests: `pnpm -C web test --run <path>` while iterating; `pnpm -C web test --run` and `pnpm -C web build` once before committing (two failures in `src/skillpage/SkillPage.test.tsx` are pre-existing on `master`).

## Review Focus

1. A note whose frontmatter the properties box can't read (a nested map, a block scalar): the calendar's keys still go, in any case, with `Frontmatter.Set`'s extent, because the server's calendar reads their dates all the same; only a missing or unclosed frontmatter block copies unchanged (Task 1 test `removes_the_keys_from_a_frontmatter_the_properties_box_cant_read`, final review fix).
2. A note without frontmatter, or with `when:` only in its body text: nothing changes in the body (Task 1 test `leaves_the_body_alone`).
3. A name near the length limit: "Copy of " plus a long name cut at the end of the name, before " 2" (Task 1 test `cuts_a_long_name_before_the_number`).
4. Shift+F10 pressed in an input of a dialog or in the palette's field: nothing opens (Task 3 test `not_over_a_dialog_or_the_palette`).
5. Duplicating twice quickly: two copies, "Copy of x.md" and "Copy of x 2.md", never one overwritten (Task 2 test `two_duplicates_in_a_row`).

---

### Task 1: The copy's text and name

**Files:**
- Create: `web/src/note/duplicate.ts`
- Test: `web/src/note/duplicate.test.ts`

**Interfaces:**
- Consumes: `findPropertyBlocks`, `frontmatterUnreadable` (`web/src/note/frontmatterText.ts`).
- Produces:
  - `copyText(content: string): string` — the content with the removed keys' whole entries gone (multi-line values included), every other byte as it was; unreadable frontmatter or none: unchanged.
  - `copyName(path: string, n: number): string` — `/Work/a/plan.md`, 1 → `/Work/a/Copy of plan.md`; 2 → `/Work/a/Copy of plan 2.md`; the name part cut so the file name stays within the limit `nameFromHeading.ts` uses (100 characters before `.md`).

- [ ] **Step 1: Write the failing tests:** `removes_calendar_keys` (each of the eight, scalar), `removes_a_multi_line_value_whole` (`until:` then an indented list), `keeps_column_drops_rank_and_moved` (a card), `keeps_tags_flags_and_other_properties`, `leaves_the_body_alone` (no frontmatter; and `when:` in the body), `copies_unreadable_frontmatter_unchanged`, `names` (n = 1, 2, 10), `cuts_a_long_name_before_the_number`.
- [ ] **Step 2: Run** `pnpm -C web test --run src/note/duplicate` → FAIL (module missing).
- [ ] **Step 3: Implement** both functions; key matching as the server's frontmatter does (a quoted key is the same key, `src/Kumbu.Core/Board/Frontmatter.cs:52`).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `git commit -m "Make a note's copy without its dates"`.

### Task 2: One source for the note's actions, and Duplicate

**Files:**
- Create: `web/src/shell/useNoteActions.tsx`, `web/src/note/DuplicateNotice.tsx`
- Modify: `web/src/shell/MobileShell.tsx` (the inline `noteActions` list at ~455–485 moves into the hook; the dock uses the hook), `web/src/state/editor.ts` (a save flush reachable from outside the editor, if none exists), the Write-mode editor that registers it
- Test: `web/src/shell/useNoteActions.test.tsx`, the existing MobileShell tests stay green

**Interfaces:**
- Consumes: Task 1's `copyText`, `copyName`; the note write `PUT /api/notes/{vault}/{path}` without a version (create-only, 409 when the note exists, as `createEmpty` in `web/src/api/notes.ts:179`).
- Produces:
  - `useNoteActions(path: string | null): { items: MenuItem[]; dialogs: ReactNode }` — the menu's items for the open note in the current mode (exactly today's items and conditions, plus Duplicate), and the dialogs/notices they open, rendered by the shell once. `MenuItem` is `web/src/components/Menu.tsx`'s.
  - `useEditor` gains `flushSave: (() => Promise<boolean>) | null` (registered by the Write-mode editor; saves pending text without ending Write mode; false when the save failed), unless an existing function already does exactly that (check `beforeRead` and `endWrite` in `web/src/state/editor.ts` first, and use it if it fits without ending Write mode).

- [ ] **Step 1: Write the failing tests:** `offers_the_same_items_as_before` (the dock's labels in Read mode for a writable note, now plus "Duplicate"), `no_duplicate_under_archive_or_without_write`, `duplicate_writes_the_copy_beside` (PUT body equals `copyText` of the note), `two_duplicates_in_a_row` (second gets "Copy of x 2.md" after a 409), `gives_up_after_99`, `saves_unsaved_text_first` (Write mode: the flush runs before the read; the copy holds the new text), `a_failed_save_copies_nothing`, `the_notice_names_the_copy_and_open_opens_it`, `the_original_stays_open`.
- [ ] **Step 2: Run** `pnpm -C web test --run src/shell/useNoteActions` → FAIL.
- [ ] **Step 3: Implement** the hook by moving the list out of MobileShell unchanged, then add Duplicate; the notice follows `ConflictToast`'s look (`web/src/note/ConflictToast.tsx`), `role="status"`, **Open** and ✕, Esc while focused, gone after 8 s unless the focus is in it.
- [ ] **Step 4: Run** the new tests and `pnpm -C web test --run src/shell` → PASS.
- [ ] **Step 5: Commit** `git commit -m "Duplicate a note, from one list of the note's actions"`.

### Task 3: The note's menu from the keyboard

**Files:**
- Create: `web/src/components/PointMenu.tsx`, `web/src/shell/useNoteMenuKey.ts`
- Modify: `web/src/components/Menu.tsx` (extract the list rendering and its keyboard handling into a part both menus use; the button-anchored behaviour unchanged), `web/src/shell/MobileShell.tsx` (render the point menu), `web/src/note/MarkdownEditor.tsx` (the caret's coordinates)
- Test: `web/src/components/PointMenu.test.tsx`, `web/src/shell/useNoteMenuKey.test.tsx`; `web/src/components/Menu.test.tsx` stays green

**Interfaces:**
- Consumes: Task 2's `useNoteActions(path).items`.
- Produces: `PointMenu({ label, items, at: { left: number; top: number }, onClose(): void })` — the same list as `Menu`, fixed at a point, flipping above or left where there is no room; focus on the first item; Esc and a click outside close it and call `onClose`. The caret's coordinates are measured in the Write-mode textarea with a mirror element (corrected after the build: there is no CodeMirror).

- [ ] **Step 1: Write the failing tests:** `shift_f10_in_read_mode_opens_the_note_menu_at_the_dock_button` (anchored at the ⋯ button's rect), `context_menu_key_too`, `shift_f10_in_write_mode_opens_at_the_caret`, `esc_returns_focus_to_the_editor_at_the_same_caret`, `not_over_a_dialog_or_the_palette`, `nothing_without_an_open_note`, `items_fit_the_mode` (no "Find in note" in Write mode), `point_menu_flips_where_there_is_no_room`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.** The key listener on `document` in the capture phase with `preventDefault` (as `web/src/palette/usePaletteShortcut.ts`); Read mode places the `PointMenu` at the dock's ⋯ button (the same list where the mouse knows it), Write mode at the caret.
- [ ] **Step 4: Run** the new tests and `pnpm -C web test --run src/components src/shell src/note` → PASS.
- [ ] **Step 5: Commit** `git commit -m "Open a note's menu with Shift+F10 or the menu key"`.

### Task 4: The note's actions in the palette

**Files:**
- Modify: `web/src/palette/rows.ts` (`Row.favourite` optional; `run` gains `{ action(): void }`), `web/src/palette/filter.ts` (favourite keys only for rows with `favourite`), `web/src/palette/Palette.tsx` (the Note group; the star and Ctrl+D skip rows without `favourite`)
- Test: `web/src/palette/Palette.test.tsx`, `web/src/palette/rows.test.ts`

**Interfaces:**
- Consumes: Task 2's `useNoteActions(path).items`; the open note's path from `useEditor` (`currentPath`).
- Produces: `noteActionRows(title: string, items: MenuItem[]): Row[]` in `rows.ts` — group `"Note"`, name the item's label, place the note's title, key `note-action:<label>`, `run: { action: item.onSelect }`, no `favourite`. The group sorts before Commands.

- [ ] **Step 1: Write the failing tests:** `note_group_with_a_note_open_and_text_typed`, `not_in_the_empty_palette`, `not_without_a_note`, `labels_are_the_menus` (e.g. "Delete note", "Add to favourites"), `no_star_and_ctrl_d_does_nothing`, `delete_note_asks_first` (Enter on it opens the same question the menu opens), `no_pub_finds_publish`, `the_palette_closes_before_the_action_runs`.
- [ ] **Step 2: Run** `pnpm -C web test --run src/palette` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS; the full web suite and `pnpm -C web build` once.
- [ ] **Step 5: Commit** `git commit -m "Reach a note's actions from the palette"`.
