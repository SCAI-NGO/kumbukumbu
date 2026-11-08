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
