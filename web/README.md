# Kumbu² web app

A React single-page app for Kumbu²: sign in, browse, read, edit, search and upload notes, manage API keys and app connections, see history and trash, manage checkpoints, and (for admins) users and invitations. It talks only to the server's REST API under `/api`, same origin, and is served by the server from `wwwroot` in the Docker image.

Stack: TypeScript, React 18, Vite 6, Tailwind v4, react-router, TanStack Query, zustand, react-markdown, pdf.js (`pdfjs-dist` 6.4, Apache 2.0, its legacy build), Vitest with Testing Library.

- pdf.js is loaded only when a PDF is shown, in a chunk of its own; its worker and its two plain-script image decoders are assets of the build, served from the app's origin (`vite.config.ts`, `src/attachments/pdf.ts`; the hardening is in `docs/security-architecture.md` §7).
- `package.json` removes `@napi-rs/canvas` with the pnpm override `"-"`: an optional dependency of `pdfjs-dist`, a native canvas for running pdf.js under Node. The app runs pdf.js in the browser only, where it draws into the page's own canvases, and the tests replace pdf.js, so nothing loads it; leaving it out spares a 36 MB native binary per platform.
- The lightbox has no library: its zoom, pan and swipe are the app's own (`src/attachments/gestures.ts`, which says why).

## Scripts

```sh
pnpm install
pnpm dev        # Vite on http://localhost:5173, proxy for /api and /mcp to http://localhost:8083
pnpm test       # Vitest (jsdom)
pnpm build      # tsc --noEmit, then vite build into dist/
pnpm gen:api    # types from a running server's /api/openapi.json (KUMBU_OPENAPI_URL to override)
pnpm icons      # every icon under public/ from icons/k2.svg (the placeholder K² logo)
```

From the repository root, use `pnpm -C web <script>`. See the main README for the `KUMBU_PUBLIC_URL` pitfall in development: the server checks the `Origin` of every write, so leave it empty or set it to `http://localhost:5173`.

## Structure (`src/`)

- `attachments/`: the lightbox for images and PDFs (`/view/:vault/<path>`), its gestures, the PDF pages and pdf.js's options
- `api/`: typed client (`openapi-fetch`), generated `schema.d.ts`, error texts, note path helpers
- `components/`: shared form parts, menu, password field, one-time secret reveal
- `hooks/`: dialogs, edge swipe, visual viewport (keyboard)
- `note/`: note view, Markdown editor and toolbar, `[[` completion, autosave, conflict handling, backlinks, history, the changed-elsewhere bar and the agent banner, line diff; the renderer's plugins (wiki links and embeds, callouts, find in the note), front matter as properties, the outline
- `routes/`: sign-in, join, forgot password, Access (API keys and app connections), the OAuth consent page, settings; `routes/admin/` users and invitations
- `search/`: search sheet, mode chips, result cards
- `shell/`: mobile shell, dock, mode toggle, tree drawer (a docked sidebar from `lg`, 1024 px), vault switcher, new vault, rename and move, trash, checkpoints, search-by-meaning provider
- `state/`: zustand stores for session and editor
- `theme/`: colour schemes, applying them, contrast test
- `upload/`: collecting files and folders, planning the upload, conflict choices
- `update/`: the build version, noticing a newer server (`X-Kumbu-Version`), reloading into it on the same page, the install hint
- `test/`: shared test setup helpers

## Where the look comes from

`concepts/MOBILE_EDITOR_v1.md` and its clickable prototype next to it; where they differ, the concept wins. The colour tokens live in `src/theme/schemes.ts` (repeated as custom properties in `src/index.css`; a test keeps them in step). `contrast.test.ts` holds every text token to 4.5:1. For this reason Kumbu's `dim` is `#8F8C86`, not the concept's `#8A8781`, which is 4.4:1 on `surface-2`.

## Rules the code keeps

- No secrets and no note text in browser storage. `localStorage` holds the colour scheme's name and whether Read mode refreshes automatically; `sessionStorage` a marker against reload loops.
- Secrets (new API keys, recovery keys, invitation links) stay out of the TanStack mutation cache: those mutations use `gcTime: 0` and the reveal is cleared on close.
- Same-origin session cookie only; the app never handles a token.
- A `401` from the API sends the user to `/signin?next=<where they were>`; after signing out on purpose, only a `401` for the session itself does (the page being left may still hear back for a moment).
- Invitation codes travel in the URL fragment (`/join#…`) and are removed from the address bar once read.

## Known gaps (for the device pass)

- The Markdown toolbar above the on-screen keyboard is built on the visual viewport and needs testing on real iOS and Android devices.
- Saving when the page closes (a `keepalive` request) is not verified on mobile browsers.
- Downloading the recovery key on the Join screen may not work in iOS Safari; copying does.
- The first tap on Search focuses a stand-in field in the dock inside the tap, so iOS raises the keyboard while the search sheet loads; the sheet's input then takes the focus over. Verified in tests only, not yet on an iPhone. Find in the note focuses its field as it opens, without a stand-in.
- iOS Safari and Android's gesture navigation use a swipe from the screen's left edge for "back"; the drawer's edge swipe competes with them there.
- The lightbox's pinch, pan and swipe, and the PDF pages' pinch, are tested in jsdom only, not yet on Android Chrome and iOS Safari.
- Rename or move offers the vault's existing folders only; moving into a new folder isn't offered yet (make a note there first).
- A missing link tapped inside an embedded note creates the note in the embedding note's folder, not the embedded one's.
- Notes whose path contains `#`, `|`, `[` or `]` can't be named by a wiki link, so the `[[` list leaves them out.
