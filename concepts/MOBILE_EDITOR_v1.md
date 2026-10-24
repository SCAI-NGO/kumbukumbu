# Kumbukumbu — Mobile Editor Spec

Status: **ready for implementation** · Source of truth for look & behaviour: the clickable prototype ("Kumbu Mobile Editor" canvas).
Stack: TypeScript + Vite + React + Tailwind, `pnpm`. Mobile-first; desktop/tablet layout is a later iteration.

---

## 1. Principles

1. **One-handed first.** Everything touched often lives in the bottom third of the screen. The top bar is for orientation only.
2. **Sheets, not pages.** File tree and search open as overlays on top of the current note. Closing them returns you exactly where you were.
3. **Markdown is the truth.** Read mode is a rendering of the raw Markdown. Every interaction in Read mode (e.g. ticking a task) is a text edit on the source.
4. **Review over writing.** On a phone the human mostly *reviews* what agents wrote. Agent provenance is a first-class feature, not decoration.
5. **Explainable search.** Every result says *why* it matched.

---

## 2. Layout (portrait phone, 390×844 reference)

```
┌──────────────────────────────────┐
│ [▤]  vault / folder        [Read|Write]  ← TopBar (64px)
│      Note title                  │
├──────────────────────────────────┤
│ ● Agent key liz edited 3 min ago  [Hide] ← AgentBanner (44px, only if agent edits)
├──────────────────────────────────┤
│                                  │
│   Note content (Read)            │
│   or raw Markdown (Write)        │  ← scrolls
│                                  │
│   LINKED FROM · 2                │  ← Backlinks (Read only)
├──────────────────────────────────┤
│ [📁] ( 🔍 Search kumbu  hybrid ) [+] │ ← Dock (Read) 
│ or                               │
│ [[ ]] ## - [ ] • B ` >    [Done] │ ← MarkdownToolbar (Write, sits above keyboard)
└──────────────────────────────────┘
```

Overlays (absolute, `z-20`):

- **TreeDrawer**: 318px from the left plus a scrim. The scrim closes the drawer.
- **SearchSheet**: full-screen, slides up from the bottom.

---

## 3. Components

| Component | Responsibility |
|---|---|
| `MobileEditorShell` | Layout, overlay state, edge-swipe gesture handler |
| `TopBar` | Tree button, breadcrumb + title (tap opens tree), `ModeToggle` |
| `ModeToggle` | Segmented Read / Write, `aria-pressed` |
| `AgentBanner` | Agent key + relative time, Show/Hide highlights |
| `MarkdownView` | Renders blocks; wiki links, inline code, bold, tasks, quotes, fenced code |
| `BacklinksList` | Notes linking to the current note |
| `MarkdownEditor` | Textarea (see §8 decision), controlled, 16px font to prevent iOS zoom |
| `MarkdownToolbar` | Insert/wrap at cursor without losing focus |
| `Dock` | Files · Search pill (shows current mode) · New note |
| `TreeDrawer` | Vault header, folder tree, "New note here", vault switcher at the **bottom** |
| `SearchSheet` | Header + mode hint, results, empty/recent states, mode chips + input at the **bottom** |
| `SearchResultCard` | Title, path, snippet, score bar, reason tag |

Suggested state: one `useEditorStore` (Zustand or a reducer) holding `currentNoteId`, `mode`, `overlay: 'none' | 'tree' | 'search'`, `expandedFolders`, `activeVault`, `searchQuery`, `searchMode`, `showAgentHighlights` and `recentNoteIds`. Note content comes from the API layer (TanStack Query or similar).

---

## 4. Interactions

### Read / Write

- The toggle switches mode. Entering Write focuses the textarea.
- Write → Read re-renders from the current source. There is no separate "save": edits are debounced and persisted (see §7).

### Read mode

- **Wiki links** `[[Name]]` resolve by note name, case-insensitive, across vaults. If the note doesn't exist yet, show a dashed underline in muted color; tapping it creates the note in the current folder and opens it in Write mode.
- **Tasks**: tapping the checkbox toggles `- [ ]` ↔ `- [x]` on that exact source line (hit target 44×44).
- **Backlinks**: shown under the note, tap to open.

### Write mode toolbar

| Button | Action |
|---|---|
| `[[ ]]` | wrap selection / insert at cursor |
| `##` | line prefix `## ` |
| `- [ ]` | line prefix `- [ ] ` |
| `•` | line prefix `- ` |
| `B` | wrap `**…**` |
| `` ` `` | wrap `` `…` `` |
| `>` | line prefix `> ` |
| Done | back to Read |

- Buttons use `onMouseDown/onPointerDown → preventDefault()` so the textarea keeps focus and the keyboard stays open.
- The toolbar must stick above the virtual keyboard: use the `visualViewport` API and test on real iOS Safari and Android Chrome.

### Tree

- Opens via the tree button, a tap on the breadcrumb/title, or an **edge swipe** (start < 32px from the left edge, dx > 60, |dy| < 60). Swipe left (dx < −60) closes it.
- Folders come first, then files, both alphabetical. Tapping a folder toggles it; folders show their file count.
- Opening a note auto-expands its folder chain and highlights the active file.
- "New note here" creates `Untitled`, `Untitled 2`, … in the active vault/folder and opens it in Write mode.
- The vault switcher (chips) sits at the bottom of the drawer.

### Search

- The input sits at the bottom, above the mode chips **Hybrid · Meaning · Exact**.
- The header shows the result count, the current mode and a one-line explanation of the mode.
- Each result shows title, `vault / folder`, a snippet (the first line containing a query term, with Markdown syntax stripped), a score bar with %, and a reason tag: `title` · `exact` · `all words` · `meaning` · `related`.
- Empty query: show Recent notes (last 5 opened).
- No results in Exact mode: offer **"Search by meaning instead"**, which switches the mode.
- Debounce the query at 200 ms and cancel in-flight requests.

---

## 5. Agent provenance

- The banner appears when the current note has edits by an agent key that the user has not seen yet (or within the last N days; this needs deciding).
- "Show" highlights the affected blocks with an accent tint and a key label (e.g. `liz`) in the top-right corner of the block.
- **Backend requirement:** block-level authorship. Proposal: since chunking is already per heading, store `lastEditedBy: apiKeyName` and `lastEditedAt` per chunk, plus a per-user `lastSeenRevision` per note, so the API can return the changed ranges since the user last looked.
- Later: "Accept / Revert" per block. This is out of scope for v1, but leave room for it in the component.

---

## 6. Visual design

- **Fonts:** IBM Plex Sans (UI and body), IBM Plex Mono (paths, H2, code, editor).
- **Tokens:**

| Token | Value |
|---|---|
| `bg` | `#111113` |
| `surface` | `#1A1A1E` |
| `surface-2` | `#232328` |
| `line` | `#26262B` / `#2E2E34` |
| `text` | `#ECEAE4` |
| `text-body` | `#D9D6CF` |
| `muted` | `#A09D96` |
| `dim` | `#8A8781` |
| `accent` (amber) | `#F0A848` |
| `on-accent` | `#17110A` |

- Make the accent themeable. Alternatives from the prototype: `#6FC3DF`, `#9BD36A`, `#E58FB0`.

### Colour schemes

The user picks a colour scheme in their settings (decided by Chris, 2026-10-24): the classic Linux schemes, **Catppuccin** and **Tokyo Night** among them, and at least one light scheme. The tokens above are the default scheme ("Kumbu"); every scheme defines the same tokens, so components only ever use tokens.

Candidates: Kumbu (default), Catppuccin Mocha and Latte, Tokyo Night and Tokyo Night Day, Gruvbox Dark and Light, Nord, Dracula, Solarized Dark and Light.

**The schemes can't be taken over unchanged.** Their own colours for secondary text (comments, subtext) often miss the 4.5:1 contrast this spec requires, and on light backgrounds even the accent does. Measured against each scheme's background, with the colour the scheme intends for each role:

| Scheme | text | body | muted | dim | accent |
|---|---|---|---|---|---|
| Kumbu | 15.7 | 13.0 | 7.0 | 5.3 | 9.3 |
| Catppuccin Mocha | 11.3 | 9.3 | 7.4 | 5.8 | 9.3 |
| Catppuccin Latte | 7.1 | 5.5 | **4.4** | **3.5** | **2.6** |
| Tokyo Night | 10.6 | 8.1 | **4.1** | **2.8** | 8.4 |
| Tokyo Night Day | 4.5 | **3.6** | **2.5** | **2.4** | **3.7** |
| Gruvbox Dark | 10.7 | 8.6 | 5.3 | **4.0** | 8.7 |
| Gruvbox Light | 10.2 | 7.8 | **4.3** | **3.2** | **3.3** |
| Nord | 10.8 | 9.2 | 5.4 | **1.7** | 6.2 |
| Dracula | 13.4 | 13.4 | 7.7 | **3.0** | 5.9 |
| Solarized Dark | 5.6 | 4.7 | 4.7 | **2.8** | 4.7 |
| Solarized Light | 5.0 | **4.1** | **4.1** | **2.5** | **3.0** |

So each scheme ships as a Kumbu variant of it:

- Background, surfaces and the accent hue stay the scheme's own, so it is recognisable.
- `muted` and `dim` are taken from the scheme's palette where a colour passes 4.5:1, otherwise darkened (light schemes) or lightened (dark schemes) until they do.
- The accent gets two tokens: `accent` for fills and borders (with `on-accent` for text on it, checked at 4.5:1), and `accent-text` for accent-coloured text (H2, links, buttons without fill), adjusted like `muted`. On light schemes the two differ.
- A unit test checks every scheme's text tokens against `bg`, `surface` and `surface-2` at 4.5:1, so a new scheme can't slip below it.
- The default follows `prefers-color-scheme` until the user picks one (Kumbu or a light scheme); the choice is stored per user on the server, so it follows them across devices.
- **H1:** 28px / 700. **H2:** mono, 12px, uppercase, tracking 0.08em, accent color. **Body:** 16px / 1.6.
- **Motion:** drawer slides in from x−28px, sheet from y+24px, both ~180–200 ms ease-out. Respect `prefers-reduced-motion`.
- **Touch targets:** at least 44px everywhere. Text contrast at least 4.5:1, which the tokens above already meet.

---

## 7. Data & API (assumptions, align with the backend)

- `GET /vaults` → list of vaults the API key can read.
- `GET /vaults/{v}/tree` → folder/note structure (names only, lazy load per folder if large).
- `GET /notes/{id}` → `{ id, vault, path[], name, markdown, revision, agentEdits?: [{ key, at, ranges }] }`
- `PUT /notes/{id}` → `{ markdown, baseRevision }`, which returns `409` on conflict.
- `GET /search?q=&mode=hybrid|semantic|exact&vault=` → `[{ noteId, name, path, snippet, score, reason }]`. The UI should get the **same ranking as the MCP `search` tool**, served from one code path.
- `GET /notes/{id}/backlinks`
- **Autosave:** debounce at 800 ms, plus a save on blur and on `visibilitychange`.
- **Conflict on 409:** show a non-blocking toast with "Agent changed this note — reload / keep mine / view diff". Agents edit concurrently, so this *will* happen.

---

## 8. Open decisions

1. **Editor engine:** start with a plain `<textarea>` (rock-solid with mobile IME). Revisit CodeMirror 6 only if syntax highlighting or live preview is really missed.
2. **Offline:** no offline cache in v1. Any cache means plaintext or the vault key on the device, which conflicts with the encryption-at-rest model. This is a deliberate policy choice; document it in the README.
3. **Markdown renderer:** use `markdown-it` or `remark` with a custom wiki-link plugin, instead of the prototype's hand-rolled parser. Preserve Obsidian syntax "as is" (embeds, callouts, frontmatter): render what we support and show the rest raw.
4. **Agent banner window:** "unseen since my last visit" (needs `lastSeenRevision`) vs. "last N days".
5. **Large vaults:** virtualize the tree list above ~500 rows.

---

## 9. Acceptance criteria / tests

- [ ] Ticking a task in Read mode changes exactly one source line (unit test on the toggle function).
- [ ] Toolbar inserts and wraps correctly with and without a selection; the textarea keeps focus (component test).
- [ ] Wiki link to a missing note creates it in the current folder and opens Write mode.
- [ ] Opening a note from search expands its tree path and highlights it.
- [ ] Exact mode with zero results shows "Search by meaning instead", and tapping it switches the mode.
- [ ] Edge swipe opens the tree; swipe left closes it; vertical scrolling does not trigger it.
- [ ] `409` on save shows the conflict toast and never silently overwrites.
- [ ] All interactive elements have a hit target of at least 44px and an accessible name (axe check in CI).
- [ ] Manual device pass on iOS Safari and Android Chrome: keyboard + toolbar, no zoom on focus, safe-area insets respected.
