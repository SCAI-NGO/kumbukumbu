// SPDX-License-Identifier: AGPL-3.0-or-later
// What goes wrong with an expression (Kumbu.Core Queries/QueryError).

/** An expression that doesn't parse: what is wrong, and where (`position`, from 0). */
export class QuerySyntaxError extends Error {
  constructor(message: string, readonly position: number) {
    super(message);
    this.name = "QuerySyntaxError";
  }
}

/**
 * A query that can't run: an expression that doesn't parse, nests too deeply or builds too much for a note, a base that
 * can't be read, a call that doesn't fit the query. `code` is the same for every engine (the conformance cases compare
 * it); the message is for people. `expression` is where `position` counts in, once known.
 */
export class QueryError extends Error {
  constructor(readonly code: string, message: string, readonly position: number, readonly expression?: string) {
    super(message);
    this.name = "QueryError";
  }
}

/**
 * What a query names that doesn't exist (a function, method or formula) or formulas that use each other in a circle: the
 * query still runs, with an error value in the cells it touches, and `query check` tells it. `position` counts in
 * `expression`.
 */
export type QueryWarning = { readonly code: string; readonly message: string; readonly expression: string; readonly position: number };

/** What `compile` gives, or the error it throws told in which expression it is; one already told keeps its own. */
export function inExpression<T>(expression: string, compile: () => T): T {
  try {
    return compile();
  } catch (e) {
    if (e instanceof QuerySyntaxError) throw new QueryError("syntax", e.message, e.position, expression);
    if (e instanceof QueryError && e.expression === undefined) throw new QueryError(e.code, e.message, e.position, expression);
    throw e;
  }
}
