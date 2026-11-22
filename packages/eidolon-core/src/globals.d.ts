// SPDX-License-Identifier: AGPL-3.0-or-later
// The only globals the core may use: those every JavaScript runtime it runs in has (browsers, Node, Obsidian's
// Electron), in the shape it uses them. Anything else of the host's comes in through a port (design §2a).

declare class TextEncoder { encode(input?: string): Uint8Array<ArrayBuffer>; }

declare class URL {
  constructor(url: string, base?: string);
  protocol: string;
  readonly hostname: string;
  pathname: string;
  search: string;
  toString(): string;
}

declare const crypto: { readonly subtle: { digest(algorithm: "SHA-256", data: ArrayBuffer | Uint8Array<ArrayBuffer>): Promise<ArrayBuffer> } };

declare const console: { warn(...data: unknown[]): void; error(...data: unknown[]): void };
