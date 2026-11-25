# Eidolon core

The client-side data layer of Kumbu²: what a client keeps and does with notes, apart from any user interface. It holds the sync engine and the sealed store; offline mode (0.7.0) adds the local index and the tools over the mirror. It is built to be taken out of Kumbu² as a library (1.0.0), so it keeps to a boundary, described in the offline mode design (`docs/superpowers/specs/2026-11-22-offline-mode-design.md`, §2a).

## What is in it

| Folder | What |
|---|---|
| `src/engine/` | The sync engine: one serial queue per vault, the decision table, conflict copies, the first sync; its ports (`ports.ts`) |
| `src/api/` | The Kumbu² REST client over an injected HTTP function, with a device's API key or a browser session's cookie |
| `src/live/` | The "vault changed" hints over a WebSocket, with backoff and polling |
| `src/host/` | One engine per mirrored vault for a host that holds many vaults (a browser session), keyed by vault id |
| `src/store/` | The sealed store: a browser's offline copy, its key and both wraps, each vault's records as the engine's `LocalFiles` and `StateStore`, contact and purge, the idle lock |
| `src/links/` | Links as the server finds and resolves them, ported from `Kumbu.Core/Links`: wiki and Markdown links, aliases, Obsidian's resolution rules, the API's `LinkView` |
| `src/search/` | Words search as the server's: a note's sections, addresses and snippets (`Kumbu.Core/Search/Chunker.Sections`, `SectionSearch`), its tags (`Kumbu.Core/Tags/TagReader`), and the matcher over notes in memory |
| `src/read/` | Reading the copy offline: a vault's tree from the inventory, notes and attachments, links resolved in the copy, words search across the held vaults, vaults by the name last seen online |

The engine, the client and the hints came from the Obsidian plugin (`kumbu-obsidian`), with their tests. The plugin now vendors them back: `src/engine`, `src/api`, `src/live` and `test/fakes` are copied into it unchanged by its `scripts/vendor-eidolon.mjs`, and its CI checks the copy against the commit its `eidolon-core.json` names. Change them here; the plugin follows when it takes a new commit.

## The boundary

- No React, nothing from `web/`, no Obsidian, no Node modules. Every import is either inside `src/` or a dependency in `package.json`.
- The DOM only through ports: storage, crypto keys, HTTP, the clock and the WebSocket are the host's. `tsconfig.json` gives the code no DOM or Node types; `src/globals.d.ts` declares the few globals every runtime it runs in has (`TextEncoder`, `URL`, `crypto.subtle.digest`, `console`).
- Dependencies under permissive licences only (MIT, BSD, ISC, Apache-2.0 and the like), transitive ones included, so that it can later be relicensed under the LGPL. Its licence until then is AGPL-3.0-or-later, as the rest of Kumbu².

`test/boundary.test.ts` checks the imports and the licences; `pnpm build` checks the types.

## Fetch policy

`EngineOptions.fetchPolicy` lets the host decide which attachments' bytes the engine downloads (`wantBytes(path, size)`, design §6). Without one, every attachment under the limit is downloaded, as in the plugin.

## The sealed store

A copy is one user's mirror in one browser (design §4.7). `MirrorCopies` finds, creates, unlocks (online with the mirror slot's key, offline with the password) and wipes copies; an unlocked `SealedMirror` holds a `VaultMirror` per vault, which is the engine's `LocalFiles` (`files()`), `StateStore` (`stateStore()`), `RemoteApi` wrapper that records the inventory (`inventory()`) and fetch policy (`downloadOrder()`). Its ports (`src/store/ports.ts`, `src/store/crypto.ts`): record storage, WebCrypto, the mirror slot, Web Locks and a channel between tabs; the web app's are in `web/src/offline/`.

## Reading offline

`read/tree.ts` answers what `GET /api/vaults/{vault}/tree` and `GET /api/notes` would, from an unlocked `SealedMirror`, with paths in the host's form (`/<vault name>/<path>`); a path the inventory lists whose text or bytes aren't here yet throws `NotOnDeviceError`. `read/links.ts` resolves a note's links in the copy (`MirrorLinks`), building one resolver per vault again only when a path or a version changed. `read/search.ts` searches every held vault's notes by words (`MirrorSearch`), opening a note's text once per version and keeping it in memory, never stored (ADR 0031): the sections that hold every word of the query or the whole query in a line, title hits first, with the tag and archive rules of `GET /api/search`. The web app's `OfflineSource` (`web/src/offline/source.ts`) stands them in for the API while the app is offline (design §4.8).

## Commands

```sh
pnpm -C packages/eidolon-core test
pnpm -C packages/eidolon-core build
```
