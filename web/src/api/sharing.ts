// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call } from "./client";
import { useEditor } from "../state/editor";
import type { components } from "./schema";

export type MemberAccess = components["schemas"]["MemberAccessView"];
export type Membership = components["schemas"]["MembershipView"];
export type Departure = components["schemas"]["DepartureView"];

/** A member's role, in rising order: what an invitation offers and the owner can change. */
export type Role = "read" | "write" | "maintain";
export const roles: readonly Role[] = ["read", "write", "maintain"];

/** Who has access to the owner's vault: her keys and apps, and the people it is shared with or who are invited. */
export const useVaultAccess = (vault: string) =>
  useQuery({ queryKey: ["vault-access", vault], queryFn: () => call(api.GET("/api/vaults/{vault}/access", { params: { path: { vault } } })) });

/** One membership as its owner sees it: with `redeemer` only while it waits for her confirmation. */
export const useMembership = (vault: string, id: string, enabled: boolean) =>
  useQuery({
    queryKey: ["membership", vault, id],
    queryFn: () => call(api.GET("/api/vaults/{vault}/memberships/{id}", { params: { path: { vault, id } } })),
    enabled,
  });

export type MembershipChange =
  | { kind: "confirm" | "decline" | "withdraw" | "revoke"; id: string }
  | { kind: "role"; id: string; role: Role };

/**
 * The owner's changes to a membership, each through `api`: confirming and raising a role to write need the password
 * window, and a closed one asks for the password and goes on. Raising to maintain takes the password each time, through
 * `useRaiseToMaintain`. Lowering, declining, withdrawing and revoking never need it.
 */
export function useMembershipChange(vault: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (change: MembershipChange) => {
      const params = { params: { path: { vault, id: change.id } } };
      switch (change.kind) {
        case "confirm":
          return void (await call(api.POST("/api/vaults/{vault}/memberships/{id}/confirm", params)));
        case "decline":
          return void (await call(api.POST("/api/vaults/{vault}/memberships/{id}/decline", params)));
        case "withdraw":
          return void (await call(api.DELETE("/api/vaults/{vault}/memberships/{id}", params)));
        case "revoke":
          return void (await call(api.POST("/api/vaults/{vault}/memberships/{id}/revoke", params)));
        case "role":
          return void (await call(api.PUT("/api/vaults/{vault}/memberships/{id}/role", { ...params, body: { role: change.role } })));
      }
    },
    onSettled: async (_, __, change) => {
      queryClient.removeQueries({ queryKey: ["membership", vault, change.id] });
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["vault-access", vault] }), queryClient.invalidateQueries({ queryKey: ["vaults"] })]);
    },
  });
}

/**
 * Raising a member to maintain: the owner's password goes with the request, window open or not, and opens the window.
 * `password` is read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
 */
export function useRaiseToMaintain(vault: string, id: string, password: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => call(api.PUT("/api/vaults/{vault}/memberships/{id}/role", { params: { path: { vault, id } }, body: { role: "maintain", password } })),
    onSettled: async () => {
      queryClient.removeQueries({ queryKey: ["membership", vault, id] });
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["vault-access", vault] }), queryClient.invalidateQueries({ queryKey: ["vaults"] })]);
    },
  });
}

/**
 * Re-encrypting the owner's vault, after a revoke or a departure (collab design §6.4): the password is checked first, so
 * that a wrong one changes nothing, then the answer waits for the vault. `password` is read through the closure, not
 * passed as the mutation's variables, which the mutation cache would keep.
 */
export function useRekeyVault(vault: string, password: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      await call(api.POST("/api/session/confirm", { body: { password } }));
      return call(api.POST("/api/vaults/{vault}/rekey", { params: { path: { vault } }, body: { password } }));
    },
    onSettled: () => Promise.all(["vault-access", "vaults", "keys", "connections"].map((key) => queryClient.invalidateQueries({ queryKey: [key] }))),
  });
}

export type MemberNotice = components["schemas"]["MemberNoticeView"];

/** The caller leaves a vault shared with her: from the next request on, none of her credentials reaches it. */
export function useLeave() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ membership }: { membership: string; vault: string }) =>
      void (await call(api.POST("/api/memberships/{id}/leave", { params: { path: { id: membership } } }))),
    onSuccess: async (_, { vault }) => {
      useEditor.getState().forgetVault(vault);
      // Nothing of the vault stays in the caches: its tree, and every note, link and history read from it.
      queryClient.removeQueries({ predicate: (q) => q.queryKey.some((k) => typeof k === "string" && (k === vault || k.startsWith(`/${vault}/`))) });
      await Promise.all(["vaults", "keys", "connections", "search", "changes"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
    },
  });
}

/** The caller's new name for a vault shared with her; her open note, recent notes and drawer move with it. */
export function useRenameAlias() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ membership, alias }: { membership: string; vault: string; alias: string }) =>
      void (await call(api.PUT("/api/memberships/{id}/alias", { params: { path: { id: membership } }, body: { alias } }))),
    onSuccess: async (_, { vault, alias }) => {
      const to = alias.normalize("NFC").trim();
      const editor = useEditor.getState();
      editor.moved(`/${vault}/`, `/${to}/`);
      if (useEditor.getState().activeVault === vault) editor.setActiveVault(to);
      queryClient.removeQueries({ predicate: (q) => q.queryKey.some((k) => typeof k === "string" && (k === vault || k.startsWith(`/${vault}/`))) });
      await Promise.all(["vaults", "keys", "connections"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
    },
  });
}

/** The member's OK for the provider the owner chose: her searches take the vault in again. */
export function useProviderSeen() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ membership, provider }: { membership: string; provider: string }) =>
      void (await call(api.POST("/api/memberships/{id}/provider-seen", { params: { path: { id: membership } }, body: { provider } }))),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ["vaults"] }),
  });
}

/** The caller's notices of shared vaults deleted for everyone, each naming the vault by her own name for it. */
export const useMemberNotices = () => useQuery({ queryKey: ["member-notices"], queryFn: () => call(api.GET("/api/member-notices")) });

export function useDismissNotice() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => void (await call(api.DELETE("/api/member-notices/{id}", { params: { path: { id } } }))),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ["member-notices"] }),
  });
}

/** The caller's own events as a member across the vaults shared with her, newest first; `vault` is null for one she has lost. */
export const useVaultEvents = () =>
  useQuery({
    queryKey: ["vault-events"],
    queryFn: async () => (await call(api.GET("/api/vault-events"))).sort((a, b) => Date.parse(b.at) - Date.parse(a.at)),
  });
