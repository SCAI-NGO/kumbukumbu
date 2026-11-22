// SPDX-License-Identifier: AGPL-3.0-or-later
// The sealed store's records in IndexedDB (offline mode design §5): one database, one object store of string keys.
// Every value but a copy's header comes sealed from the Eidolon core; nothing here sees a path or a note.
import type { RecordStorage } from "eidolon-core/store/ports";

const STORE = "records";

/** The key range of every key that starts with `prefix`: "￿" sorts after every character a key holds. */
const below = (prefix: string) => IDBKeyRange.bound(prefix, `${prefix}￿`);

function requested<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function committed(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onabort = tx.onerror = () => reject(tx.error ?? new Error("The offline copy couldn't be written."));
  });
}

export async function openRecordStorage(name = "kumbu-mirror", factory: IDBFactory = indexedDB): Promise<RecordStorage> {
  const opening = factory.open(name, 1);
  opening.onupgradeneeded = () => opening.result.createObjectStore(STORE);
  const db = await requested(opening);
  const read = <T>(fn: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> => requested(fn(db.transaction(STORE, "readonly").objectStore(STORE)));
  const bytes = (value: unknown) => new Uint8Array(value as ArrayBuffer);

  return {
    async get(key) {
      const value = await read((s) => s.get(key));
      return value === undefined ? null : bytes(value);
    },
    async scan(prefix) {
      const tx = db.transaction(STORE, "readonly").objectStore(STORE);
      const [keys, values] = await Promise.all([requested(tx.getAllKeys(below(prefix))), requested(tx.getAll(below(prefix)))]);
      return keys.map((k, i) => [k as string, bytes(values[i])]);
    },
    async write(change) {
      const tx = db.transaction(STORE, "readwrite");
      const written = committed(tx);
      const store = tx.objectStore(STORE);
      try {
        for (const prefix of change.removePrefix ?? []) store.delete(below(prefix));
        for (const key of change.remove ?? []) store.delete(key);
        // An ArrayBuffer of its own, not a view, so a later change to the caller's array can't reach the record.
        for (const [key, value] of change.put ?? []) store.put(value.slice().buffer, key);
      } catch (e) {
        // A request refused at once (a bad key): nothing of the transaction stays.
        tx.abort();
        await written.catch(() => {});
        throw e;
      }
      await written;
    },
  };
}
