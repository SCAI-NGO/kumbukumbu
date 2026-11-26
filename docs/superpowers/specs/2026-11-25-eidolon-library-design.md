# Eidolon as a library: what taking it out decides

Date: 2026-11-25. Status: decisions for 0.8.0, written ahead while 0.7.0 is in post-production; the move itself waits until `packages/eidolon-core` is out of 0.7.0's fixes. Decision 1 is proposed and waits for the maintainer; the others hold whichever way it goes. Builds on the offline mode design (`2026-11-22-offline-mode-design.md`, §2a) and [ADR 0002](../../adr/0002-eidolon-core-as-a-host-agnostic-package.md).

## 1. Why

The roadmap moves the Eidolon core out of Kumbu² as a library under the LGPL in 0.8.0 (confirmed by the maintainer on 2026-11-23), so that Chatsundere can build on the same client data layer: sync, the sealed store, the local index, the tools over the mirror and, from 0.8.0, the query engine. Taking it out is a refactor that would collide with 0.7.0's fixes in the same files, so what can be settled ahead is what the move decides, not the move.

## 2. Where it stands

- `packages/eidolon-core` is a workspace package beside `web/`, `private`, version `0.0.0`, AGPL-3.0-or-later, exporting every file (`"./*": "./src/*.ts"`), consumed as TypeScript source.
- Its boundary is checked: no React, `web/`, Obsidian or Node in `src/`; no DOM types; only dependencies under permissive licences, transitive ones included (`test/boundary.test.ts`).
- The Obsidian plugin vendors its sync part (`src/engine`, `src/api`, `src/live`, `test/fakes`) and checks the copy against the commit its `eidolon-core.json` names.
- Every commit to the package is the maintainer's (with Claude as co-author); nothing in it came from a third party or from Chatsundere's AGPL-3.0-only apps.

## 3. Decisions

1. **It stays in this repository and is published from here** — *proposed, the maintainer decides* ([ADR 0037](../../adr/0037-eidolon-is-published-from-the-kumbu-repository.md)). The package keeps its folder, gets its own version and licence, and is published to npm from a tag `eidolon-vX.Y.Z` by a workflow of this repository. *Why:* the server's sync API and the engine change together (the reason it moved here from the plugin, offline design §2): in one repository a protocol change is one pull request with both sides tested; in two it is two releases in an order. Chatsundere needs a published package either way. *Against:* a repository of its own makes the library visibly independent and gives it its own issues; it costs every protocol change a cross-repository dance and a second CI. If the maintainer prefers its own repository, decisions 2 to 6 hold unchanged.
2. **Licence: LGPL-3.0-or-later**, in `package.json`, a `COPYING.LESSER` with the GPL's text beside it, and the SPDX header of every file in the package. The rest of Kumbu² stays AGPL-3.0-or-later. *Why:* "or later" as everything else of Kumbu²; the LGPL lets Chatsundere and others link it whatever their licence, while changes to the library itself stay free. The copyright holder (Second Circuit) relicenses; with every commit the maintainer's, no one else has to agree. The boundary test keeps third-party AGPL and GPL code out, as it does now.
3. **Name: `@second-circuit/eidolon`** on npm, under an organisation scope of the copyright holder. *Why:* a scope names who stands behind it and can't be squatted per package; "core" was the name inside Kumbu², outside it the library is Eidolon. Needs the npm organisation, which is the maintainer's to create.
4. **A public surface, everything else internal.** The exports map lists the entry points a host uses: `engine`, `api`, `live`, `host`, `store`, `links`, `read`, `query`, and the ports' types. Paths below them are not exported. *Why:* today every file is importable, so every rename would break a consumer; a short list is what semantic versioning can promise.
5. **Its own version, semantic, 0.x until it has a second host.** It starts at 0.1.0 and stays below 1.0 until Chatsundere runs on it; a breaking change bumps the minor while it is 0.x. Kumbu²'s version and Eidolon's are independent; Kumbu²'s `web/` uses the workspace copy, never the published one. *Why:* a library with one consumer doesn't know its stable surface yet; promising 1.0 before the second host would promise the wrong things.
6. **Published as JavaScript with type declarations**, built by `tsc` from the same sources (ES2022 modules), not as TypeScript sources. *Why:* consumers shouldn't need our compiler settings; the workspace keeps using the sources directly, so nothing changes for `web/`.
7. **The Obsidian plugin moves from vendoring to the published package** once it is published, and keeps vendoring until then. *Why:* vendoring was the stand-in for a package that wasn't public; one mechanism is enough. The plugin's version changes then, since its build does.

## 4. What the move is, when it comes

1. Exports map, build to `dist/` with declarations, `files` in `package.json`, `private` off (decisions 4 and 6).
2. Licence and headers (decision 2), in one pull request of its own, so the change of licence is one commit anyone can find.
3. A publish workflow on `eidolon-v*` tags with npm provenance, the token as a repository secret the maintainer adds.
4. The plugin on the package (decision 7), in `kumbu-obsidian`.

## 5. Open

- Decision 1 (this repository or its own) and decision 3's npm organisation: the maintainer's.
- Whether the tools over the mirror (offline design §2a, "The tools") are part of 0.1.0 or follow: they don't exist yet as a module of their own.
