// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { vi } from "vitest";
import { routerFuture, routes } from "../router";

/** The app's routes in a memory router at `path`, with a fresh query cache. */
export function renderApp(path: string, queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  const router = createMemoryRouter(routes, { initialEntries: [path], future: routerFuture });
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} future={{ v7_startTransition: true }} />
    </QueryClientProvider>,
  );
  return router;
}

type Handler = (request: Request) => Response | Promise<Response>;

/** Answers `fetch` by "METHOD /path", else by the longest "METHOD /prefix*"; any other request fails the test. */
export function fakeApi(handlers: Record<string, Handler>) {
  const fetch = vi.fn(async (request: Request) => {
    const key = `${request.method} ${new URL(request.url).pathname}`;
    const prefix = Object.keys(handlers)
      .filter((k) => k.endsWith("*") && key.startsWith(k.slice(0, -1)))
      .sort((a, b) => b.length - a.length)[0];
    const handler = handlers[key] ?? (prefix && handlers[prefix]);
    if (!handler) throw new Error(`Unexpected request: ${key}`);
    return handler(request);
  });
  vi.stubGlobal("fetch", fetch);
  return fetch;
}

export const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

export const noContent = () => new Response(null, { status: 204 });

export const user = { id: "u1", username: "ada", display: "Ada", role: "user" };
