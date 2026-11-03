// SPDX-License-Identifier: AGPL-3.0-or-later
import { api, call } from "../api/client";
import type { components } from "../api/schema";

export type ConsoleRequest = components["schemas"]["ConsoleRequest"];
export type ConsoleView = components["schemas"]["ConsoleView"];
export type ConsolePart = components["schemas"]["ConsolePartView"];

/** One call of the console: `sh` with a command line, or `cd` with an absolute path. A failed command is an answer too. */
export const consoleCall = (body: ConsoleRequest, signal: AbortSignal) => call(api.POST("/api/console", { body, signal }));
