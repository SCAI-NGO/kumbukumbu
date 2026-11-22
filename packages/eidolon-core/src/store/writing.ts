// SPDX-License-Identifier: AGPL-3.0-or-later
// Writing into a vault's mirror while offline (offline mode design §3, work package 7): the editor's saves, new notes
// and trashing, each a change the engine uploads once online, as it does an edit made in Obsidian. A note changed in
// Kumbu² meanwhile ends as a conflict copy beside it, by the engine's rules. The editor's unsaved text is the copy's
// Drafts, so a download never replaces what the user is typing.
import type { Version } from "../engine/ports";
import { eligibility } from "../engine/paths";
import { versionOf } from "../engine/version";
import type { Drafts, Entry, VaultMirror } from "./vault";

/** The vault is read-only for this member (or closing): offline writes are refused as Kumbu² would refuse them. */
export class ReadOnlyError extends Error {
  readonly code = "read-only";
  constructor() { super("You can only read this vault."); }
}

/** Listed in the inventory, but its text isn't on this device yet: there is nothing here to write on or trash. */
export class NotHereError extends Error {
  constructor(path: string) { super(`${path} isn't on this device yet. It opens when you're online.`); }
}

/** The answer of a save, in the shape of `PUT /api/notes` and its 409. */
export type SaveAnswer = { saved: { version: Version } } | { conflict: { content: string; version: Version } };

const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** A note's path within its vault that Kumbu² takes: relative, no empty or dot segment, ending in `.md`. */
function assertNotePath(path: string): void {
  const ok = path !== "" && !path.split("/").some((s) => s === "") && eligibility(path, true) === "sync" && path.toLowerCase().endsWith(".md");
  if (!ok) throw new Error(`Not a note's path: ${JSON.stringify(path)}`);
}

/**
 * The editor's and the tree's writes into one vault's mirror. `written` runs after each change with its path: the
 * host tells the vault's engine (`localChanged`), since a tab's own writes don't come back to it over the channel.
 */
export class NoteWriter {
  constructor(private readonly vault: VaultMirror, private readonly written: (path: string) => void) {}

  get canWrite(): boolean { return this.vault.canWrite; }

  /**
   * Writes `content` on top of `base`, the version the editor's text was read or last saved at. Another version
   * there now (written by another tab, or downloaded) answers its text, as a 409 does, and writes nothing: the
   * editor then offers the same choice as online. Compared with what is stored, never with an editor's draft, which
   * is this very text.
   */
  async save(path: string, content: string, base: Version): Promise<SaveAnswer> {
    this.assertWritable();
    const version = await versionOf(content);
    const bytes = encoder.encode(content);
    let answer: SaveAnswer | null = null;
    const [done] = await this.vault.change([path], async (p, entry) => {
      const now = await this.textOf(p, entry);
      if (now === null) throw new NotHereError(path);
      if (now.version !== base) {
        answer = { conflict: now };
        return false;
      }
      return { entry: { ...entry!, local: { version, size: bytes.byteLength, mtime: this.vault.nextMtime(), text: true } }, body: bytes };
    });
    if (done) this.written(path);
    return answer ?? { saved: { version } };
  }

  /** Makes a new note; null if the path is taken, on this device or in Kumbu² as far as the copy knows. */
  async create(path: string, content: string): Promise<{ version: Version } | null> {
    this.assertWritable();
    assertNotePath(path);
    const version = await versionOf(content);
    const bytes = encoder.encode(content);
    const [done] = await this.vault.change([path], (p, entry) => Promise.resolve(entry !== undefined ? false
      : { entry: { path: p, local: { version, size: bytes.byteLength, mtime: this.vault.nextMtime(), text: true } }, body: bytes }));
    if (!done) return null;
    this.written(path);
    return { version };
  }

  /**
   * Takes the note out of the copy; online the engine trashes it in Kumbu², where the trash is (the copy keeps
   * none). False if there is no such note.
   */
  async trash(path: string): Promise<boolean> {
    this.assertWritable();
    const [done] = await this.vault.change([path], (p, entry) => {
      if (entry === undefined) return Promise.resolve(false);
      if (entry.local === undefined) throw new NotHereError(path);
      return Promise.resolve({ entry: { path: p, ...(entry.remote ? { remote: entry.remote } : {}) }, body: null });
    });
    if (done) this.written(path);
    return done!;
  }

  private assertWritable(): void {
    if (!this.vault.canWrite) throw new ReadOnlyError();
  }

  /** The stored text and its version, null if this device holds none. */
  private async textOf(path: string, entry: Entry | undefined): Promise<{ content: string; version: Version } | null> {
    if (entry?.local === undefined) return null;
    const bytes = await this.vault.bytes(path);
    if (bytes === null) return null;
    const content = decoder.decode(bytes);
    return { content, version: entry.local.text ? entry.local.version : await versionOf(content) };
  }
}

/** An open editor, as the copy sees it. */
export interface OpenEditor {
  /** Its text while some of it is unsaved, else null. */
  text(): string | null;
  /** A download replaced the file under it (null: removed). */
  replaced(content: string | null): void;
}

/**
 * The copy's Drafts port filled by the open editors of one vault (vault-relative paths): what the engine reads for
 * a path is the text the user sees, so a download that finds unsaved typing makes a conflict copy instead of
 * replacing it. Only this tab's editors: another tab's are seen through their saves, which check the version.
 */
export class EditorDrafts implements Drafts {
  private readonly open_ = new Map<string, OpenEditor>();

  /** Registers the editor of `path`; the returned function unregisters it, unless another took its place. */
  open(path: string, editor: OpenEditor): () => void {
    this.open_.set(path, editor);
    return () => {
      if (this.open_.get(path) === editor) this.open_.delete(path);
    };
  }

  text(path: string): string | null {
    return this.open_.get(path)?.text() ?? null;
  }

  replaced(path: string, content: string | null): void {
    this.open_.get(path)?.replaced(content);
  }
}
