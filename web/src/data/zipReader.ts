// SPDX-License-Identifier: AGPL-3.0-or-later
import { inflateSync, strFromU8 } from "fflate";

/** An entry of a ZIP's central directory, as fflate's `unzipSync` names it, and where its local header is. */
export interface ZipEntry {
  name: string;
  /** Compressed size, as declared. */
  size: number;
  /** Uncompressed size, as declared. */
  originalSize: number;
  compression: number;
  offset: number;
}

const u16 = (b: Uint8Array, i: number) => b[i] | (b[i + 1] << 8);
const u32 = (b: Uint8Array, i: number) => (b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)) >>> 0;
const u64 = (b: Uint8Array, i: number) => u32(b, i) + u32(b, i + 4) * 2 ** 32;

const bytesOf = async (zip: Blob, start: number, end: number) => new Uint8Array(await zip.slice(start, end).arrayBuffer());

class NotAZip extends Error {
  constructor() {
    super("not a ZIP");
  }
}

/**
 * The central directory of a ZIP, read from its end by `slice`: the file itself is never read whole, so a ZIP of
 * gigabytes of photos costs its directory in memory, not its size. Reads as fflate's `unzipSync` does (its record
 * search, ZIP64, names in UTF-8 when flagged, else Latin-1). Rejects what isn't a ZIP.
 */
export async function readDirectory(zip: Blob): Promise<ZipEntry[]> {
  // The end record is 22 bytes and a comment of at most 65,535 more.
  const tailStart = Math.max(0, zip.size - 22 - 65_535 - 20);
  const tail = await bytesOf(zip, tailStart, zip.size);
  let e = tail.length - 22;
  while (e >= 0 && u32(tail, e) !== 0x06054b50) e--;
  if (e < 0) throw new NotAZip();
  let count = u16(tail, e + 8);
  let size = u32(tail, e + 12);
  let start = u32(tail, e + 16);
  let zip64 = false;
  if (e >= 20 && u32(tail, e - 20) === 0x07064b50) {
    const at = u64(tail, e - 12);
    const record = await bytesOf(zip, at, at + 56);
    if (record.length === 56 && u32(record, 0) === 0x06064b50) {
      zip64 = true;
      count = u64(record, 32);
      size = u64(record, 40);
      start = u64(record, 48);
    }
  }
  if (count === 0) return [];
  const dir = await bytesOf(zip, start, start + size);
  const entries: ZipEntry[] = [];
  for (let o = 0, i = 0; i < count; i++) {
    if (o + 46 > dir.length || u32(dir, o) !== 0x02014b50) throw new NotAZip();
    const nameLength = u16(dir, o + 28);
    const extraLength = u16(dir, o + 30);
    const name = strFromU8(dir.subarray(o + 46, o + 46 + nameLength), !(u16(dir, o + 8) & 0x0800));
    let [packed, unpacked, offset] = [u32(dir, o + 20), u32(dir, o + 24), u32(dir, o + 42)];
    const max = 0xffffffff;
    if (zip64 && (packed === max || unpacked === max || offset === max)) {
      // The ZIP64 extra field holds the ones that didn't fit, in this order.
      const end = o + 46 + nameLength + extraLength;
      for (let x = o + 46 + nameLength; x + 4 <= end; x += 4 + u16(dir, x + 2)) {
        if (u16(dir, x) !== 1) continue;
        let at = x + 4;
        if (unpacked === max) [unpacked, at] = [u64(dir, at), at + 8];
        if (packed === max) [packed, at] = [u64(dir, at), at + 8];
        if (offset === max) offset = u64(dir, at);
        break;
      }
    }
    entries.push({ name, size: packed, originalSize: unpacked, compression: u16(dir, o + 10), offset });
    o += 46 + nameLength + extraLength + u16(dir, o + 32);
  }
  return entries;
}

/**
 * One entry's bytes, read by `slice` (its local header, then its data) and unpacked as fflate's `unzipSync` does: a
 * stored entry is its declared compressed size, cut by the ZIP's end; a deflated one is inflated into a buffer of its
 * declared size, never grown, so a lying size truncates instead of allocating more.
 */
export async function readEntry(zip: Blob, entry: ZipEntry): Promise<Uint8Array> {
  const header = await bytesOf(zip, entry.offset, entry.offset + 30);
  if (header.length < 30 || u32(header, 0) !== 0x04034b50) throw new NotAZip();
  const at = entry.offset + 30 + u16(header, 26) + u16(header, 28);
  const data = await bytesOf(zip, at, at + entry.size);
  if (entry.compression === 0) return data;
  if (entry.compression === 8) return inflateSync(data, { out: new Uint8Array(entry.originalSize) });
  throw new Error(`unknown compression type ${entry.compression}`);
}
