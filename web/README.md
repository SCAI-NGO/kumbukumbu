# Kumbu² web app

A React single-page app for Kumbu²: sign in, browse, read, edit, search and upload notes, manage API keys, and (for admins) users and invitations. It talks only to the server's REST API under `/api`, same origin, and is served by the server from `wwwroot` in the Docker image.

Stack: TypeScript, React 18, Vite 6, Tailwind v4, react-router, TanStack Query, zustand, react-markdown, Vitest with Testing Library.

## Scripts

```sh
pnpm install
pnpm dev        # Vite on http://localhost:5173, proxy for /api and /mcp to http://localhost:8083
pnpm test       # Vitest (jsdom)
pnpm build      # tsc --noEmit, then vite build into dist/
pnpm gen:api    # types from a running server's /api/openapi.json (KUMBU_OPENAPI_URL to override)
```

From the repository root, use `pnpm -C web <script>`. See the main README for the `KUMBU_PUBLIC_URL` pitfall in development: the server checks the `Origin` of every write, so leave it empty or set it to `http://localhost:5173`.

## Structure (`src/`)

- `api/`: typed client (`openapi-fetch`), generated `schema.d.ts`, error texts, note path helpers
- `components/`: shared form parts, menu, password field, one-time secret reveal
- `hooks/`: dialogs, edge swipe, visual viewport (keyboard)
- `note/`: note view, Markdown editor and toolbar, `[[` completion, autosave, conflict handling, backlinks, line diff; the renderer's plugins (wiki links and embeds, callouts, find in the note), front matter as properties, the outline
- `routes/`: sign-in, join, forgot password, keys, settings; `routes/admin/` users and invitations
- `search/`: search sheet, mode chips, result cards
- `shell/`: mobile shell, dock, mode toggle, tree drawer, rename and move
- `state/`: zustand stores for session and editor
- `theme/`: colour schemes, applying them, contrast test
- `upload/`: collecting files and folders, planning the upload, conflict choices
- `test/`: shared test setup helpers

## Where the look comes from

`concepts/MOBILE_EDITOR_v1.md` and its clickable prototype next to it; where they differ, the concept wins. The colour tokens live in `src/theme/schemes.ts` (repeated as custom properties in `src/index.css`; a test keeps them in step). `contrast.test.ts` holds every text token to 4.5:1. For this reason Kumbu's `dim` is `#8F8C86`, not the concept's `#8A8781`, which is 4.4:1 on `surface-2`.

## Rules the code keeps

- No secrets and no note text in browser storage. The only thing in `localStorage` is the name of the colour scheme.
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
- Notes are text only; attachments are left to a later, carefully designed version.
