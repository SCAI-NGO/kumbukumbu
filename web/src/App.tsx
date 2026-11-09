// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { HttpError } from "./api/client";
import { forgetReload } from "./components/AppError";
import { neutralWindow, wrapCreateHref } from "./discreet/neutralWindow";
import { routerFuture, routes } from "./router";

// A data router: useBlocker (the recovery key's "leave without it?") needs one. On a window of its own, which keeps
// discreet paths out of the address bar, the history and every link (spec §3.5).
const router = wrapCreateHref(createBrowserRouter(routes, { future: routerFuture, window: neutralWindow }));
// A navigation that worked: the next update may reload by itself once again.
router.subscribe((state) => {
  if (state.navigation.state === "idle" && !state.errors) forgetReload();
});

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // A refusal (4xx) won't change on its own; a fault or a dropped connection may.
      retry: (failures, error) => !(error instanceof HttpError && error.status < 500) && failures < 2,
    },
  },
});

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} future={{ v7_startTransition: true }} />
    </QueryClientProvider>
  );
}
