// SPDX-License-Identifier: AGPL-3.0-or-later
// The offline query engine's public surface: what a host needs to read notes' properties and run bases and queries.
export { Base, type QueryResult, type QueryRow } from "./base";
export { Evaluator, FieldReference, FunctionReference, MethodReference, type LanguageEntry, type Scope } from "./evaluator";
export { QueryError, QuerySyntaxError, type QueryWarning } from "./errors";
export { QueryNote } from "./note";
export { Properties, type NoteProperties } from "./properties";
export { PropertyTypes, type PropertyType, type PropertyTypeMap } from "./propertyTypes";
export { MAX_MEANING_TEXT, Query, type MeaningVectors, type QueryMeaning, type QueryParam } from "./query";
export type { Value } from "./value";
