// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { api, call } from "./client";
import type { components } from "./schema";

export type SkillView = components["schemas"]["SkillView"];
export type SkillsView = components["schemas"]["SkillsView"];
export type SkillHitView = components["schemas"]["SkillHitView"];

/** The skills the user sees, and her agent keys and connections without the vault Skills. */
export const useSkills = () => useQuery({ queryKey: ["skills"], queryFn: () => call(api.GET("/api/skills")) });

/** What `skills find` returns for `q`, optionally as seen by the key or connection `as`. Asked only for a non-blank `q`. */
export const useFindSkills = (q: string, as?: string) =>
  useQuery({
    queryKey: ["skills", "find", q, as ?? null],
    enabled: q.trim() !== "",
    queryFn: () => call(api.GET("/api/skills/find", { params: { query: { q, ...(as ? { as } : {}) } } })),
  });

/** The folder `from` (or `system:<name>`) into the vault `to`; `name` when the folder's name is taken there. Gives the new folder. */
export const copySkill = (from: string, to: string, name?: string) =>
  call(api.POST("/api/skills/copy", { body: { from, to, ...(name ? { name } : {}) } }));

export const moveSkill = (from: string, to: string, name?: string) =>
  call(api.POST("/api/skills/move", { body: { from, to, ...(name ? { name } : {}) } }));

/** The skill behind a link's `code` into the vault `vault`; `name` when the folder's name is taken there. Gives the new folder. */
export const addSkill = (code: string, vault: string, name?: string) =>
  call(api.POST("/api/skills/add", { body: { code, vault, ...(name ? { name } : {}) } }));

export type SharedSkillView = components["schemas"]["SharedSkillView"];

/** Her skills shared by link: never a link, which only the answer that makes it holds. */
export const useSharedSkills = () => useQuery({ queryKey: ["skills", "shared"], queryFn: () => call(api.GET("/api/skills/shared")) });

/** Shares the skill's folder by link: the answer is the one place the link is ever shown, so it is kept in no cache. */
export const shareSkill = (path: string) => call(api.POST("/api/skills/shared", { body: { path } }));

/** A new copy under the same link. */
export const updateSharedSkill = (id: string) => call(api.POST("/api/skills/shared/{id}/update", { params: { path: { id } } }));

export const withdrawSharedSkill = (id: string) => call(api.DELETE("/api/skills/shared/{id}", { params: { path: { id } } }));

export type MarketplacesView = components["schemas"]["MarketplacesView"];
export type MarketplaceListingView = components["schemas"]["MarketplaceListingView"];
export type MarketplaceSkillView = components["schemas"]["MarketplaceSkillView"];

const marketplacesKey = ["skills", "marketplaces"];

/** Her marketplaces, and whether the admin has marketplaces on at all. */
export const useMarketplaces = () => useQuery({ queryKey: marketplacesKey, queryFn: () => call(api.GET("/api/skills/marketplaces")) });

const split = (repo: string) => {
  const [owner = "", name = ""] = repo.split("/");
  return { owner, repo: name };
};

export const addMarketplace = (repo: string) => call(api.POST("/api/skills/marketplaces", { body: { repo } }));

export const removeMarketplace = (repo: string) => call(api.DELETE("/api/skills/marketplaces/{owner}/{repo}", { params: { path: split(repo) } }));

/** What the marketplace offers at the commit its default branch points to now. Asked GitHub, so only on demand, and not again by itself. */
export const useMarketplaceListing = (repo: string) =>
  useQuery({
    queryKey: ["marketplace", repo],
    queryFn: () => call(api.GET("/api/skills/marketplaces/{owner}/{repo}", { params: { path: split(repo) } })),
    retry: false,
    // The server keeps a listing for an hour and the branch's commit for ten minutes: older than that, browsing asks again.
    staleTime: 10 * 60_000,
  });

/** The skill at `path` as of `commit`, for the install dialog. */
export const useMarketplaceSkill = (repo: string, commit: string, path: string) =>
  useQuery({
    queryKey: ["marketplace", repo, commit, path],
    queryFn: () => call(api.GET("/api/skills/marketplaces/{owner}/{repo}/skill", { params: { path: split(repo), query: { commit, path } } })),
    retry: false,
    // The server keeps a listing for an hour and the branch's commit for ten minutes: older than that, browsing asks again.
    staleTime: 10 * 60_000,
  });

/** The skill at `path` of `repo` as of the `commit` she was shown, into the vault `vault`; `name` when the folder's name is taken there. */
export const installSkill = (repo: string, commit: string, path: string, vault: string, name?: string) =>
  call(api.POST("/api/skills/install", { body: { repo, commit, path, vault, ...(name ? { name } : {}) } }));

export type SkillUpdateView = components["schemas"]["SkillUpdateView"];
export type SkillReviewView = components["schemas"]["SkillReviewView"];

/** Asks each source of her copies whether there is a newer version. Only on her opening the screen or pressing the button. */
export const checkSkillUpdates = () => call(api.POST("/api/skills/updates/check"));

/** Her copy at `path` against the version it would take: the repository, folder and commit, a hash to name, every file. Asked afresh each time. */
export const useSkillReview = (path: string) =>
  useQuery({
    queryKey: ["skills", "review", path],
    queryFn: () => call(api.GET("/api/skills/updates/review", { params: { query: { path } } })),
    retry: false,
    gcTime: 0,
    staleTime: 0,
  });

/** The version the review named (`hash`) into her copy. */
export const takeSkillUpdate = (path: string, hash: string) => call(api.POST("/api/skills/updates/take", { body: { path, hash } }));

/** That version as one she doesn't want: the next check no longer offers it. */
export const skipSkillUpdate = (path: string, hash: string) => call(api.POST("/api/skills/updates/skip", { body: { path, hash } }));
