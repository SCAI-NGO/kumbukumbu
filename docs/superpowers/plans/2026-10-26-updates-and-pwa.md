# App Updates, Version Display and PWA Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kumbu² picks up every release by itself (reloading on the same page when nothing is unsaved), shows its version, and installs as an app from the browser menu, without a service worker.

**Architecture:** The image build's version (`x.y.z` / `0.1.0-pre.N` plus the short commit) goes into both the server assembly and the web bundle. The server sends it as `X-Kumbu-Version` on every `/api` response and from an anonymous `GET /api/version`; the web app compares it with its own on every response and when it becomes visible, and reloads at a safe moment, with a one-reload-per-version guard. A web app manifest and generated "K²" placeholder icons make it installable.

**Tech Stack:** .NET 10 minimal API, xUnit; React 18, Vite 6, Zustand, TanStack Query, Vitest; `@resvg/resvg-js` (dev only) for the icons.

**Spec:** `docs/superpowers/specs/2026-10-26-demo-round-design.md` §1 (binding), with its "Global" section.

## Where and how this runs

- One branch `updates-and-pwa` from `master`, one pull request; Chris merges it himself. `origin/step-10-oauth` may be merged first and touches `Program.cs`, `Security.cs` and `web/src/routes/Settings.tsx`: symbols are named here, not line numbers; rebase onto `master` before opening the PR.
- Commands: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~<Class>"` (MongoDB as in the existing test setup), `pnpm -C web test`, `pnpm -C web build`. After the server contract change in Task 1, regenerate the API types: run the server (`dotnet run --project src/Kumbu.Server`, port 8083) and `pnpm -C web gen:api`.
- Commit messages: imperative, free-form, each ending with the session's attribution lines.

## Global Constraints

- **No service worker**, no `vite-plugin-pwa`, no Workbox. Only a manifest.
- `index.html` keeps `Cache-Control: no-cache`, `/assets/*` keeps `public, max-age=31536000, immutable` (`Program.cs`, `webApp` static file options), as today.
- Header name exactly `X-Kumbu-Version`; value `<version>+<short sha>`, e.g. `0.2.0+a6362ec`; the same string in the web bundle and `GET /api/version` → `{ "version": "0.2.0+a6362ec" }`.
- A version **without** `+<sha>`, or starting `0.0.0-dev`, on either side switches update detection off (local builds, `dotnet run`, `vite dev`).
- At most **one** automatic reload per server version (loop guard).
- Browser storage holds no note path and no content (the editor's rule, `web/src/state/editor.ts`): `sessionStorage` keys `kumbu.update.reloadedFor` (a version string) and `kumbu.update.restore` (`{ mode, scrollTop }` only; the URL already says where); `localStorage` key `kumbu.installHint` (`"dismissed"`). Every access guarded in `try`/`catch`, as `web/src/note/autoRefresh.ts` does.
- Exact copy (English, British spelling):
  - bar while text is unsaved: `Kumbu² <semver> is available` with button `Update`
  - after a reload: `Updated to <semver>` (status, gone after 4 s)
  - loop guard: `Kumbu² couldn't load version <semver>. Reload the page, or ask your admin.`
  - Settings section `About`: `Kumbu² <semver> (<sha>)`; when the server differs: `Server: <semver> (<sha>)`; a development build: `Kumbu² development build`
  - Settings section `Install as an app`, and the one-time phone hint `Install Kumbu² as an app` with buttons `How` and `Not now`
- Manifest: `name` and `short_name` `Kumbu²`, `display` `standalone`, `start_url` `/`, `scope` `/`, `theme_color` and `background_color` `#111113` (the Kumbu scheme's `bg`, `web/src/theme/schemes.ts`). Icon: "K²" in `#f0a848` (the scheme's `accent`) on `#111113`.
- Icon source is one SVG with the glyphs as **paths** (no `<text>`, no font), so every renderer draws the same.
- British English in code, comments and docs; the repo's comment density.

## Review Focus

1. **A tab that stays on one note in Read mode for days** (phone app paused in the background): it must update when it becomes visible again, without any navigation. Test in Task 3 (`reloads when the page becomes visible again`).
2. **A save in flight or a conflict at the moment of reloading**: the editor's guard says no, so nothing reloads and the bar appears; no text is lost and no `beforeunload` prompt appears on a reload that does happen. Tests in Task 3 (`never reloads while the editor can't save`, `asks the editor to save before reloading in Write mode`).
3. **A proxy or CDN that keeps serving the old shell**: exactly one reload, then the loop-guard sentence; never a reload loop. Test in Task 3 (`reloads once per version, then says it couldn't`).
4. **Signed out** (sign-in, join, forgot-password screens): updates still apply there, since `GET /api/version` needs no session and the watcher lives in the router's `Root`. Test in Task 3 (`updates on the sign-in screen too`).
5. **A server without the header** (an older image during a deploy, a test double): no update, no error, no reload. Test in Task 2 (`a response without the header changes nothing`).

---

### Task 1: Build version in the server, header and `GET /api/version`

**Files:**
- Create: `src/Kumbu.Server/BuildVersion.cs`
- Modify: `src/Kumbu.Server/Program.cs` (replace the local function `ServerVersion()`, used for the MCP `ServerInfo`; map `/api/version` next to `/healthz`)
- Modify: `src/Kumbu.Server/Api/Security.cs` (`UseSecurityHeaders`, inside the `OnStarting` block where `/api` gets `no-store`)
- Modify: `Dockerfile` (web and build stages)
- Test: `tests/Kumbu.Server.Tests/Api/VersionTests.cs`

**Interfaces:**
- Produces: `public static class BuildVersion { public static string Full { get; } public static string Semver { get; } }` — `Full` is the assembly's `AssemblyInformationalVersionAttribute` (`0.2.0+a6362ec`, or `0.0.0-dev` / `0.1.0` locally), `Semver` the part before `+`. `public sealed record VersionView(string Version);` `GET /api/version` → `200 VersionView(BuildVersion.Full)`, anonymous, in OpenAPI.
- Produces for Task 2: header `X-Kumbu-Version: <BuildVersion.Full>` on every response under `/api` (errors and 401s included).

- [ ] **Step 1: Write the failing tests** in `VersionTests.cs` (`[Collection("app")]`, `KumbuAppFactory`, as `StaticFilesTests`):
  - `Version_answers_without_signing_in`: `GET /api/version` → 200, body `version` equals `BuildVersion.Full`.
  - `Every_api_response_carries_the_version`: `GET /api/version`, `GET /api/vaults` without credentials (401) and `GET /api/nothing-here` (404) all have `X-Kumbu-Version` equal to `BuildVersion.Full`.
  - `Semver_drops_the_commit`: `BuildVersion.Semver` contains no `+` and is a prefix of `BuildVersion.Full`.
- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~VersionTests"` — expected: FAIL (`BuildVersion` not defined).
- [ ] **Step 3: Implement** `BuildVersion` (read the attribute once, `Split('+')[0]` for `Semver`, fall back to `"0.0.0-dev"` if absent), replace `ServerVersion()` with `BuildVersion.Semver`, map `app.MapGet("/api/version", () => TypedResults.Ok(new VersionView(BuildVersion.Full))).AllowAnonymous()`, and add the header in `UseSecurityHeaders` under the same `/api` condition as `no-store`.
- [ ] **Step 4: Run** the tests again — expected: PASS. Run the whole server suite once (`dotnet test tests/Kumbu.Server.Tests`) — expected: PASS (the OpenAPI snapshot test, if any, updated for the new endpoint).
- [ ] **Step 5: Dockerfile.** In the `web` stage, after `COPY web web`: `ARG VERSION=0.0.0-dev` and `ARG GIT_SHA=""`, and run the build with `KUMBU_VERSION="$VERSION" KUMBU_GIT_SHA="$GIT_SHA" pnpm -C web build`. In the `build` stage: the same two `ARG`s and `dotnet publish … -p:Version="$VERSION" -p:SourceRevisionId="$GIT_SHA"` (the SDK appends `+$SourceRevisionId` to the informational version; with an empty sha it stays `0.0.0-dev`). Keep the final stage's `ARG`s and `/app/VERSION` as they are. Comment why both stages need them (one version for app and server).
- [ ] **Step 6: Verify the image:** `docker build --build-arg VERSION=0.2.0-test --build-arg GIT_SHA=abc1234 -t kumbu:version-test .`, then `docker run --rm --entrypoint sh kumbu:version-test -c 'grep -l "0.2.0-test+abc1234" /app/wwwroot/assets/*.js && strings /app/Kumbu.Server.dll | grep -m1 "0.2.0-test+abc1234"'` — expected: one asset path and the version string printed. (The `KUMBU_VERSION` define arrives with Task 2; run this step after Task 2 if it fails only on the asset grep.)
- [ ] **Step 7: Commit** `Send the build version on every API response`.

### Task 2: The web app knows its version and notices a newer server

**Files:**
- Modify: `web/vite.config.ts` (`define`)
- Create: `web/src/vite-env.d.ts` (or add to an existing ambient declaration file) declaring `__KUMBU_VERSION__: string`
- Create: `web/src/update/version.ts`
- Create: `web/src/update/store.ts`
- Modify: `web/src/api/client.ts` (a second middleware next to `sessionEnds`)
- Test: `web/src/update/version.test.ts`, `web/src/update/store.test.ts`

**Interfaces:**
- Consumes: header and `GET /api/version` from Task 1 (regenerate `schema.d.ts`).
- Produces:
  - `export const appVersion: string` (= `__KUMBU_VERSION__`; Vite defines it as `` `${KUMBU_VERSION}+${KUMBU_GIT_SHA}` `` when both env vars are set, else `"0.0.0-dev"`).
  - `export type Version = { semver: string; sha: string }`; `export function parseVersion(text: string | null | undefined): Version | null` — null for empty, for no `+`, for a semver starting `0.0.0-dev`.
  - `export function newerOnServer(app: string, server: string | null): Version | null` — the server's parsed version when both parse and the full strings differ, else null.
  - `useUpdate` (Zustand): `{ server: string | null; available: Version | null; failed: Version | null; updated: string | null; report(server: string | null): void; fail(v: Version): void; markUpdated(semver: string | null): void }`. `report` sets `server` and `available = newerOnServer(appVersion, server)`; a null or missing header leaves everything as it was.
  - `export async function checkVersion(): Promise<void>` — `GET /api/version` through `api`, errors swallowed (a failed check says nothing about versions).

- [ ] **Step 1: Write the failing tests:**

```ts
test("parses a release and a pre-release with their commit", () => {
  expect(parseVersion("0.2.0+a6362ec")).toEqual({ semver: "0.2.0", sha: "a6362ec" });
  expect(parseVersion("0.1.0-pre.42+abc1234")).toEqual({ semver: "0.1.0-pre.42", sha: "abc1234" });
});
test("development builds and versions without a commit switch detection off", () => {
  for (const v of ["0.0.0-dev", "0.0.0-dev+abc1234", "0.1.0", "", null, undefined]) expect(parseVersion(v)).toBeNull();
  expect(newerOnServer("0.0.0-dev", "0.2.0+a6362ec")).toBeNull();
  expect(newerOnServer("0.2.0+a6362ec", "0.1.0")).toBeNull();
});
test("a different server build is an update, the same one is not", () => {
  expect(newerOnServer("0.1.0-pre.41+aaaaaaa", "0.2.0+a6362ec")).toEqual({ semver: "0.2.0", sha: "a6362ec" });
  expect(newerOnServer("0.2.0+a6362ec", "0.2.0+a6362ec")).toBeNull();
});
```
  Store tests (`store.test.ts`, stubbing `appVersion` via `vi.mock("./version", …)` keeping the real `parseVersion`/`newerOnServer`): `every API response reports the header` (a `fakeApi` response with `X-Kumbu-Version: 0.2.0+a6362ec` through `call(api.GET("/api/vaults"))` sets `available.semver` to `"0.2.0"`); `a response without the header changes nothing` (Review Focus 5); `checkVersion swallows a network error`.
- [ ] **Step 2: Run** `pnpm -C web test src/update` — expected: FAIL (modules missing).
- [ ] **Step 3: Implement** `version.ts`, `store.ts`, `checkVersion`, the `define` in `vite.config.ts` (read `process.env.KUMBU_VERSION` / `KUMBU_GIT_SHA`; for Vitest the define must exist too, so put it at the top level of the config) and the middleware `versionWatch` (`onResponse({ response }) { useUpdate.getState().report(response.headers.get("X-Kumbu-Version")); }`), registered with `api.use(versionWatch)`.
- [ ] **Step 4: Run** `pnpm -C web test` and `pnpm -C web build` — expected: PASS, build succeeds; `KUMBU_VERSION=0.2.0-test KUMBU_GIT_SHA=abc1234 pnpm -C web build && grep -l "0.2.0-test+abc1234" web/dist/assets/*.js` prints a path.
- [ ] **Step 5: Commit** `Let the web app notice a newer server`.

### Task 3: Apply the update safely, on the same page

**Files:**
- Create: `web/src/update/apply.ts`
- Create: `web/src/update/UpdateBar.tsx`
- Modify: `web/src/router.tsx` (`Root`: mount the watcher and the bar)
- Modify: `web/src/note/NoteView.tsx` (take the restored `scrollTop` once the note has rendered; restore Write mode)
- Test: `web/src/update/apply.test.tsx`

**Interfaces:**
- Consumes: `useUpdate`, `checkVersion` (Task 2); `useEditor` (`mode`, `beforeRead`, `setMode`) from `web/src/state/editor.ts`.
- Produces:
  - `export function useApplyUpdates(): void` — used once in `Root`. Calls `checkVersion()` on `visibilitychange` to visible; on visible and on every location change (`useLocation().key`) while `available` is set, calls `tryReload("auto")`.
  - `export async function tryReload(reason: "auto" | "button", reload = () => window.location.reload()): Promise<boolean>`:
    1. `available` null → false.
    2. Loop guard: if `sessionStorage["kumbu.update.reloadedFor"]` equals `available`'s full string → `fail(available)`, false.
    3. Safe? Read mode → yes. Write mode → `await beforeRead?.({ reread: false })` must be `true`; otherwise false (the bar stays). In `"auto"` mode, Write mode with a pending editor guard also counts as not safe when the guard is missing.
    4. Store `kumbu.update.reloadedFor` and `kumbu.update.restore = { mode, scrollTop }` (the note view's scroller position, read through a getter the note view registers), then `reload()`, true.
  - `export function takeRestore(): { mode: "read" | "write"; scrollTop: number } | null` — reads and removes `kumbu.update.restore`.
  - On start (module init in `Root`): if `kumbu.update.reloadedFor` is set and `appVersion` equals it, `markUpdated(semver)` and remove the key; if it is set and differs, keep it (the guard of step 2 then fires on the next report).
  - `UpdateBar`: `available` and Write mode → bar `Kumbu² <semver> is available` + `Update` (`tryReload("button")`); `failed` → the loop-guard sentence; `updated` → status `Updated to <semver>`, cleared after 4 s. `role="status"` for the notice, a `region` named `Update` for the bar.

- [ ] **Step 1: Write the failing tests** (`renderApp`, `fakeApi` answering `GET /api/version` and the screen's own requests; a `reload` spy passed in or stubbed on `window.location`):
  - `reloads at the next navigation when nothing is unsaved` — server reports a newer version in Read mode; navigating to another note calls `reload` once and stores `{ mode: "read", scrollTop }`.
  - `reloads when the page becomes visible again` (Review Focus 1) — no navigation; dispatching `visibilitychange` with `document.visibilityState` stubbed `"visible"` calls `GET /api/version`, then `reload`.
  - `asks the editor to save before reloading in Write mode` (Review Focus 2) — `beforeRead` resolving `true` is called with `{ reread: false }` before `reload`; restore stores `mode: "write"`.
  - `never reloads while the editor can't save` (Review Focus 2) — `beforeRead` resolving `false`: no `reload`, the bar `Kumbu² 0.2.0 is available` with `Update` is shown.
  - `reloads once per version, then says it couldn't` (Review Focus 3) — with `kumbu.update.reloadedFor = "0.2.0+a6362ec"` already set and the app still older: no `reload`, the text `Kumbu² couldn't load version 0.2.0. Reload the page, or ask your admin.` appears.
  - `says it updated, on the same note, in the same mode` — after a simulated reload (`appVersion` equal to `reloadedFor`, restore `{ mode: "write", scrollTop: 240 }`): `Updated to 0.2.0` shows, the note opens in Write mode, and the scroller's `scrollTop` is 240.
  - `updates on the sign-in screen too` (Review Focus 4) — at `/signin`, a newer version → `reload`.
  - `stores no path or content` — after `tryReload`, every `sessionStorage` value matches `/^(\d[\w.\-+]*|\{"mode":"(read|write)","scrollTop":\d+\})$/`.
- [ ] **Step 2: Run** `pnpm -C web test src/update/apply` — expected: FAIL.
- [ ] **Step 3: Implement** `apply.ts`, `UpdateBar.tsx`, the `Root` wiring and the restore in `NoteView` (register a `scrollTop` getter for `tryReload`; after the first render with content, apply `takeRestore()` once: `setMode("write")` if asked, then `scroller.current.scrollTop`). A reload that follows a successful `beforeRead` must not trigger the editor's "unsaved" `beforeunload` prompt; if it does, clear the guard's unsaved state first rather than suppressing the prompt globally.
- [ ] **Step 4: Run** `pnpm -C web test` — expected: PASS (all existing tests too).
- [ ] **Step 5: Commit** `Reload into a new release on the same page`.

### Task 4: About and installing, in Settings and once on a phone

**Files:**
- Modify: `web/src/routes/Settings.tsx` (two sections: `About` and `Install as an app`; add them after the existing ones)
- Create: `web/src/update/InstallHint.tsx`
- Modify: `web/src/shell/MobileShell.tsx` (render `InstallHint` above the content)
- Test: `web/src/routes/Settings.test.tsx` (extend), `web/src/update/InstallHint.test.tsx`

**Interfaces:**
- Consumes: `appVersion`, `parseVersion`, `useUpdate.server` (Task 2).
- Produces: `export function isStandalone(): boolean` (`matchMedia("(display-mode: standalone)").matches || navigator.standalone === true`) and `export function isPhone(): boolean` (`matchMedia("(pointer: coarse)").matches`) in `InstallHint.tsx`.

- [ ] **Step 1: Write the failing tests:**
  - Settings `shows the app's version, and the server's when it differs` — `About` region reads `Kumbu² 0.2.0 (a6362ec)`; with the store's `server` `0.2.1+b0b0b0b` also `Server: 0.2.1 (b0b0b0b)`; with `appVersion` `0.0.0-dev`: `Kumbu² development build`.
  - Settings `explains installing on Android, iPhone and the desktop` — the `Install as an app` region has three short step lists: Android Chrome (`⋮` → `Install app` or `Add to Home screen`), iPhone/iPad Safari (Share → `Add to Home Screen`), desktop Chrome or Edge (the install icon in the address bar, or the menu → `Install Kumbu²`). Already installed (`isStandalone()`): one line `Kumbu² is installed as an app on this device.` instead.
  - InstallHint `shows once on a phone, not when installed, not after Not now` — shown with `isPhone()` true and `isStandalone()` false; `Not now` stores `kumbu.installHint = "dismissed"` and hides it; not shown on a desktop pointer, nor when standalone, nor when dismissed; `How` navigates to `/settings#install`.
- [ ] **Step 2: Run** `pnpm -C web test src/routes/Settings src/update/InstallHint` — expected: FAIL.
- [ ] **Step 3: Implement** the sections (the existing `Section` component; the install section gets `id="install"`) and `InstallHint` (guarded `localStorage` like `autoRefresh.ts`).
- [ ] **Step 4: Run** `pnpm -C web test` — expected: PASS.
- [ ] **Step 5: Commit** `Show the version and how to install Kumbu² as an app`.

### Task 5: Manifest and the placeholder "K²" icon

**Files:**
- Create: `web/icons/k2.svg` (the one source)
- Create: `web/scripts/icons.mjs`; add `"icons": "node scripts/icons.mjs"` to `web/package.json` scripts and `@resvg/resvg-js` to devDependencies (`pnpm -C web add -D @resvg/resvg-js`)
- Create (generated, committed): `web/public/favicon.svg`, `web/public/icons/icon-192.png`, `web/public/icons/icon-512.png`, `web/public/icons/icon-maskable-512.png`, `web/public/apple-touch-icon.png`
- Create: `web/public/manifest.webmanifest`
- Modify: `web/index.html` (title `Kumbu²`; `<link rel="manifest">`, `<link rel="icon" href="/favicon.svg" type="image/svg+xml">`, `<link rel="apple-touch-icon" href="/apple-touch-icon.png">`, `<meta name="theme-color" content="#111113">`, `<meta name="apple-mobile-web-app-capable" content="yes">`, `<meta name="apple-mobile-web-app-title" content="Kumbu²">`, `<meta name="apple-mobile-web-app-status-bar-style" content="black">`)
- Modify (only if Step 4's server test fails): `src/Kumbu.Server/Program.cs` (`webApp` options: a `FileExtensionContentTypeProvider` with `.webmanifest` → `application/manifest+json`)
- Test: `web/src/update/manifest.test.ts`, `tests/Kumbu.Server.Tests/Api/StaticFilesTests.cs` (extend)

**Interfaces:**
- Produces: `pnpm -C web icons` regenerates every file under `web/public` listed above from `web/icons/k2.svg`. Sizes: 192 and 512 square (the glyph filling about 70 % of the square); maskable 512 with the glyph inside the central 60 % (the safe zone is the inner 80 % circle) on a full `#111113` background; `apple-touch-icon.png` 180, opaque; `favicon.svg` a copy of the source.

- [ ] **Step 1: Write the failing tests:**
  - `manifest.test.ts` (Node fs, no DOM): `the manifest says what the spec says` — parses `web/public/manifest.webmanifest`, asserts `name`, `short_name`, `display`, `start_url`, `scope`, `theme_color`, `background_color` exactly as in Global Constraints; `every icon exists at its size` — for each `icons[]` entry the file under `web/public` exists, is a PNG, and its IHDR width and height (bytes 16–23) equal `sizes`; one entry has `purpose: "maskable"`; `the icon source draws without fonts` — `web/icons/k2.svg` contains no `<text` and no `font`.
  - `StaticFilesTests`: `The_manifest_is_served_as_a_manifest_and_revalidated` — a `manifest.webmanifest` in the fixture web root answers 200, `Content-Type: application/manifest+json`, `Cache-Control` containing `no-cache`.
- [ ] **Step 2: Run** `pnpm -C web test src/update/manifest` and `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~StaticFilesTests"` — expected: FAIL.
- [ ] **Step 3: Draw** `k2.svg`: a 512×512 viewBox, background `#111113` rounded square (radius 96), "K" and a superscript "2" as filled paths in `#f0a848`, geometric (straight strokes for the K, a simple arc and base for the 2). Write `icons.mjs` with `Resvg` (`fitTo: { mode: "width", value }`), rendering the maskable variant from the same paths placed in a `<g transform>` scaled into the safe zone. Run `pnpm -C web icons`; write the manifest and `index.html` tags.
- [ ] **Step 4: Run** both test commands again — expected: PASS (add the content type mapping only if the server test failed on `Content-Type`). `pnpm -C web build` — expected: `web/dist` contains `manifest.webmanifest`, `favicon.svg`, `apple-touch-icon.png` and `icons/`.
- [ ] **Step 5: Manual check** (record the result in the PR description): `docker compose -f compose.yaml -f compose.build.yaml up -d --build`, open `http://localhost:8080` in Chrome, DevTools → Application → Manifest shows no installability errors and the icons; on a phone (or Chrome's device mode) the menu offers `Install app`.
- [ ] **Step 6: Commit** `Make Kumbu² installable with a placeholder K² icon`.

### Task 6: Documentation and the pull request

**Files:**
- Modify: `README.md` (a section "Updates and installing as an app": no service worker, why; the app reloads itself after a release when nothing is unsaved, on the same page; `X-Kumbu-Version` and `GET /api/version`; how to install per platform; replacing the icon: edit `web/icons/k2.svg`, run `pnpm -C web icons`. In the development part: `KUMBU_VERSION` / `KUMBU_GIT_SHA` for a web build, and that local builds report `0.0.0-dev` with detection off.)
- Modify: `docs/superpowers/specs/2026-10-26-demo-round-design.md` only if the implementation had to differ from §1 (say where and why).

- [ ] **Step 1: Write** the README section; check every statement against the code just written.
- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Server.Tests` and `pnpm -C web test` and `pnpm -C web build` — expected: all PASS.
- [ ] **Step 3: Commit** `Document updates and installing as an app`, rebase onto `master`, push the branch, open the PR "App updates, version display and PWA" with: what changed, the manual check of Task 5, and a note that the update path is first proven by the next deploy after this one (an image with this code must already be running for a tab to notice the one after it).

## Self-review notes

- Spec §1 coverage: no service worker and the install hint (Tasks 4, 5); caching unchanged (Global Constraints, existing `StaticFilesTests`); one build version for app and server, dev builds off (Tasks 1, 2); header and visibility check (Tasks 1–3); same page, scroll and mode, notice, bar, loop guard (Task 3); version display (Task 4); manifest and icon script (Task 5).
- *Builder's choice made here:* `GET /api/version` exists so the visibility check has something cheap and anonymous to ask (the spec names only the header); the restore keeps no path because the URL already holds it.
