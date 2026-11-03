// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient, type Query, type QueryClient } from "@tanstack/react-query";
import { useNavigate, type NavigateFunction } from "react-router-dom";
import { api, call } from "./client";
import { noteUrl } from "./paths";
import { movedPath, useEditor } from "../state/editor";
import type { components } from "./schema";

export type MemberAccess = components["schemas"]["MemberAccessView"];
export type Membership = components["schemas"]["MembershipView"];
export type Departure = components["schemas"]["DepartureView"];

/** A member's role, in rising order: what an invitation offers and the owner can change. */
export type Role = "read" | "write" | "maintain";
export const roles: readonly Role[] = ["read", "write", "maintain"];

/** How often the open access list asks again: the sync socket's hints are about notes and don't say a link was opened. */
const accessPoll = 10_000;

/**
 * Who has access to the owner's vault: her keys and apps, and the people it is shared with or who are invited.
 * `poll`: while the list is open, so a redeemer who arrives shows without a reload.
 */
export const useVaultAccess = (vault: string, poll = false) =>
  useQuery({
    queryKey: ["vault-access", vault],
    queryFn: () => call(api.GET("/api/vaults/{vault}/access", { params: { path: { vault } } })),
    refetchInterval: poll ? accessPoll : false,
    refetchIntervalInBackground: false,
  });

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

/** What a re-encryption withdrew (collab design §7.1), as the end of the sentence that reports it; empty if nothing. */
export function withdrawnSentence(view: { withdrawnInvitations?: number | string } | null) {
  const count = Number(view?.withdrawnInvitations ?? 0);
  if (count === 0) return "";
  return ` ${count === 1 ? "1 open invitation was" : `${count} open invitations were`} withdrawn; send a new link if you still want them in.`;
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

/**
 * Drops the queries `matches` names, each once nothing observes it. One still shown (the note, before the router has
 * moved on) would otherwise be made again at once and fetched under a name that no longer finds anything.
 */
function forgetOnceUnused(queryClient: QueryClient, matches: (query: Query) => boolean) {
  const cache = queryClient.getQueryCache();
  const forget = () => {
    const found = cache.findAll({ predicate: matches });
    for (const q of found) if (q.getObserversCount() === 0) cache.remove(q);
    return found.some((q) => q.getObserversCount() > 0);
  };
  if (!forget()) return;
  const stop = cache.subscribe((event) => {
    if (event.type === "observerRemoved" && !forget()) stop();
  });
}

/**
 * The owner's vault `vault`, now named `name`, in this tab: on her own rename's answer, or on the sync socket's
 * `renamed` frame for another tab's. Her open note, recent notes, drawer and unsaved text move with it. Moved and
 * navigated first, and the refetches not waited for, so that the note still shown and the dialog's access list don't
 * ask again under the old name. A tab that moved already matches nothing under the old name and stays.
 */
export function followRename(queryClient: QueryClient, navigate: NavigateFunction, vault: string, name: string) {
  const [from, to] = [`/${vault}/`, `/${name.normalize("NFC").trim()}/`];
  const editor = useEditor.getState();
  const open = editor.currentPath;
  editor.moved(from, to);
  if (editor.activeVault === vault) editor.setActiveVault(to.slice(1, -1));
  // The drawer shows only on `/` and on a note, so an open note is all there is to follow.
  if (open?.startsWith(from)) navigate(noteUrl(movedPath(open, from, to)), { replace: true });
  forgetOnceUnused(queryClient, (q) => q.queryKey.some((k) => typeof k === "string" && (k === vault || k.startsWith(from))));
  void Promise.all(["vaults", "keys", "connections", "search", "changes"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
}

/**
 * The owner's new name for her vault. Followed here, not in the dialog's `mutate`: a dialog closed meanwhile would
 * skip that, and the store would move without the URL.
 */
export function useRenameVault() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  return useMutation({
    mutationFn: async ({ vault, name }: { vault: string; name: string }) =>
      void (await call(api.PUT("/api/vaults/{vault}/name", { params: { path: { vault } }, body: { name } }))),
    onSuccess: (_, { vault, name }) => followRename(queryClient, navigate, vault, name),
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
