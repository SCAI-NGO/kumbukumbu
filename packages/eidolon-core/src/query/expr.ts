// SPDX-License-Identifier: AGPL-3.0-or-later
// A parsed expression of the Bases language (Kumbu.Core Queries/Expr); `position` is where it starts in the text, for messages.
import type { Value } from "./value";

export type Operator =
  | "Or" | "And" | "Not" | "Equal" | "NotEqual" | "Less" | "LessOrEqual" | "Greater" | "GreaterOrEqual"
  | "Add" | "Subtract" | "Multiply" | "Divide" | "Remainder" | "Negate";

export type Expr =
  | { readonly kind: "literal"; readonly value: Value; readonly position: number }
  /** A bare name: a property (`status`), or one of `file`, `note`, `formula`, `this`, or a function's. */
  | { readonly kind: "name"; readonly name: string; readonly position: number }
  | { readonly kind: "member"; readonly target: Expr; readonly name: string; readonly position: number }
  | { readonly kind: "index"; readonly target: Expr; readonly index: Expr; readonly position: number }
  /** A call: of a function (`now()`, callee a name) or a method (`file.hasTag("a")`, callee a member). */
  | { readonly kind: "call"; readonly callee: Expr; readonly args: readonly Expr[]; readonly position: number }
  | { readonly kind: "unary"; readonly operator: Operator; readonly operand: Expr; readonly position: number }
  | { readonly kind: "binary"; readonly operator: Operator; readonly left: Expr; readonly right: Expr; readonly position: number }
  | { readonly kind: "list"; readonly items: readonly Expr[]; readonly position: number }
  | { readonly kind: "object"; readonly entries: ReadonlyArray<readonly [string, Expr]>; readonly position: number }
  | { readonly kind: "regex"; readonly pattern: string; readonly flags: string; readonly position: number };
