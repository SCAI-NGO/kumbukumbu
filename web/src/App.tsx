// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { HttpError } from "./api/client";
import { forgetReload } from "./components/AppError";
import { neutralWindow, wrapCreateHref } from "./discreet/neutralWindow";
import { routes } from "./router";

// A data router: useBlocker (the recovery key's "leave without it?") needs one. On a window of its own, which keeps
// discreet paths out of the address bar, the history and every link (spec §3.5).
const router = wrapCreateHref(createBrowserRouter(routes, { window: neutralWindow }));
// A navigation that worked: the next update may reload by itself once again.
router.subscribe((state) => {
  if (state.navigation.state === "idle" && !state.errors) forgetReload();
});

export const queryClient = new QueryClient({
  defaultOptions: {
    // The offline mode decides where reads and writes go (offline/host.ts): a query or mutation held back while the
    // browser says it is offline would never reach the copy on this device.
    mutations: { networkMode: "always" },
    queries: {
      networkMode: "always",
      // A refusal (4xx) won't change on its own; a fault or a dropped connection may.
      retry: (failures, error) => !(error instanceof HttpError && error.status < 500) && failures < 2,
    },
  },
});

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}
