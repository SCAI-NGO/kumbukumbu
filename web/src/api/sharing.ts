// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call } from "./client";
import type { components } from "./schema";

export type MemberAccess = components["schemas"]["MemberAccessView"];
export type Membership = components["schemas"]["MembershipView"];

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
 * The owner's changes to a membership, each through `api`: confirming and raising a role need the password window, and
 * a closed one asks for the password and goes on. Lowering, declining, withdrawing and revoking never do.
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
