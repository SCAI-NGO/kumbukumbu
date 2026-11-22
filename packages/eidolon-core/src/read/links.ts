// SPDX-License-Identifier: AGPL-3.0-or-later
// A note's links resolved in the mirror, as `GET /api/links` answers them (offline mode design §3: wiki links and
// embeds resolved locally, by the server's rules ported to links/).
import type { SealedMirror } from "../store/mirror";
import type { VaultMirror } from "../store/vault";
import { aliasesOf } from "../links/aliases";
import { isFolderMarker, kindOf } from "../links/formats";
import { LinkResolver } from "../links/resolve";
import { linkViews, type LinkView } from "../links/views";
import { locate } from "./tree";

interface Built { vault: VaultMirror; name: string; signature: string; resolver: LinkResolver }

/**
 * One resolver per vault, built again only when a path or a held version changed: the aliases need every note's text,
 * which is opened from the copy then.
 */
export class MirrorLinks {
  private readonly built = new Map<string, Built>();

  constructor(private readonly mirror: SealedMirror) {}

  /** The links of `content`, the note at `path`; empty where the path names no held vault. */
  async of(path: string, content: string): Promise<LinkView[]> {
    const found = locate(this.mirror, path);
    if (found === undefined) return [];
    const name = path.split("/")[1]!;
    return linkViews(await this.resolver(found.vault, name), path, content);
  }

  private async resolver(vault: VaultMirror, name: string): Promise<LinkResolver> {
    const entries = vault.list().filter((e) => !vault.removedHere(e.path) && !e.path.split("/").some((s) => s.startsWith(".")));
    const signature = entries.map((e) => `${e.path}\0${e.local?.version ?? ""}\0${e.remote?.version ?? ""}`).sort().join("\n");
    const held = this.built.get(vault.id);
    if (held !== undefined && held.vault === vault && held.name === name && held.signature === signature) return held.resolver;

    const notes: string[] = [];
    const attachments: string[] = [];
    const aliases = new Map<string, string[]>();
    for (const e of entries) {
      const full = `/${name}/${e.path}`;
      if (isFolderMarker(full)) continue;
      if (kindOf(full) !== "note") {
        attachments.push(full);
        continue;
      }
      notes.push(full);
      if (e.local === undefined) continue;
      const bytes = await vault.bytes(e.path);
      if (bytes === null) continue;
      // The server reads aliases from every note, archived ones too: an alias finds a note wherever it is.
      const own = aliasesOf(new TextDecoder().decode(bytes));
      if (own.length > 0) aliases.set(full, own);
    }
    const resolver = new LinkResolver(name, notes, attachments, aliases);
    this.built.set(vault.id, { vault, name, signature, resolver });
    return resolver;
  }
}
