// SPDX-License-Identifier: AGPL-3.0-or-later
// The types a property can be declared with, as Obsidian names them in .obsidian/types.json (Kumbu.Core Queries/PropertyTypes).
// Obsidian declares them for the whole vault, not per note, so a note's `birthday` is a date because the vault says so,
// not because of how it is written.
import { foldCase } from "../links/text";

export type PropertyType = "text" | "multitext" | "number" | "checkbox" | "date" | "datetime";

/** Declared types by property name, ignoring case: the keys are `foldCase(name)`. */
export type PropertyTypeMap = ReadonlyMap<string, PropertyType>;

// Obsidian writes "aliases" and "tags" as types of their own; both are lists of text.
const NAMES: ReadonlyMap<string, PropertyType> = new Map<string, PropertyType>([
  ["text", "text"],
  ["multitext", "multitext"],
  ["aliases", "multitext"],
  ["tags", "multitext"],
  ["number", "number"],
  ["checkbox", "checkbox"],
  ["date", "date"],
  ["datetime", "datetime"],
]);

const NONE: PropertyTypeMap = new Map();

// Lists of text in every vault, declared or not.
const BUILT_IN: PropertyTypeMap = new Map<string, PropertyType>(
  ["tags", "aliases", "cssclasses"].map((name) => [foldCase(name), "multitext"]),
);

export const PropertyTypes = {
  /** Nothing declared. */
  none: NONE,

  /** The type a property has in a vault with these declarations: its own, else tags, aliases and cssclasses as lists;
   * null for one that is typed by how its values are written. */
  of(name: string, types: PropertyTypeMap): PropertyType | null {
    const folded = foldCase(name);
    return types.get(folded) ?? BUILT_IN.get(folded) ?? null;
  },

  /** The type of an Obsidian type name ("date"), matched exactly as types.json writes it; null for another name. */
  named(name: string): PropertyType | null {
    return NAMES.get(name) ?? null;
  },

  /** The `types` of a types.json; a file that isn't one, or a type name Obsidian doesn't have, declares nothing. */
  read(json: string): PropertyTypeMap {
    let root: unknown;
    try {
      root = JSON.parse(json);
    } catch {
      return NONE;
    }
    const types = isObject(root) ? root.types : undefined;
    if (!isObject(types)) return NONE;

    const declared = new Map<string, PropertyType>();
    for (const [name, value] of Object.entries(types)) {
      const type = typeof value === "string" ? PropertyTypes.named(value) : null;
      if (type !== null) declared.set(foldCase(name), type);
    }
    return declared;
  },
};

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
