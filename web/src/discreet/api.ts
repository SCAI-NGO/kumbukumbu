// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { api, call } from "../api/client";
import { useDiscreet } from "./state";

/** The session and the vault list are asked again: what the session shows changed. */
const shownChanged = (queryClient: QueryClient) =>
  Promise.all([queryClient.invalidateQueries({ queryKey: ["session"] }), queryClient.invalidateQueries({ queryKey: ["vaults"] })]);

/**
 * The PIN shows the discreet vaults for `minutes` (null: until the session ends or hides them). `pin` is read through
 * the closure, not passed as the mutation's variables, which the mutation cache would keep. A wrong PIN and no PIN at
 * all are the same 400, "You have no discreet vaults."
 */
export function useReveal(pin: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (minutes: number | null) => call(api.POST("/api/account/discreet/reveal", { body: { pin, minutes } })),
    // The mutation's closure holds the PIN: nothing of it stays in the cache once the dialog is gone.
    gcTime: 0,
    onSuccess: (view) => {
      useDiscreet.getState().setFromSession(view);
      return shownChanged(queryClient);
    },
  });
}

/** Hides them again at once. */
export function useHide() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => call(api.POST("/api/account/discreet/hide")),
    onSettled: () => shownChanged(queryClient),
  });
}

/** Sets or changes the PIN, with the current password every time; both read through the closure, as `useReveal`'s PIN. */
export function useSetPin(password: string, pin: string) {
  return useMutation({
    mutationFn: () => call(api.POST("/api/account/discreet/pin", { body: { password, pin } })),
    gcTime: 0,
  });
}

/** Marks a vault as discreet, or not, for her alone; only while the mode is on. */
export function useMarkDiscreet() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ vault, discreet }: { vault: string; discreet: boolean }) =>
      call(api.PUT("/api/account/discreet/vaults/{vault}", { params: { path: { vault } }, body: { discreet } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ["vaults"] }),
  });
}
