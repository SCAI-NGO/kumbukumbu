// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { api, call } from "../api/client";
import { inVault, vaultOf } from "../api/paths";
import type { components } from "../api/schema";

export type BlameView = components["schemas"]["BlameView"];
export type BlameSection = components["schemas"]["BlameSectionView"];
export type BlameCount = components["schemas"]["BlameCountView"];

/** Who wrote what in a note (blame spec §7.2): its text and the record, of one state, by section. */
export const useBlame = (path: string) =>
  useQuery({
    queryKey: ["blame", path],
    queryFn: () => call(api.GET("/api/blame/{vault}/{path}", { params: { path: { vault: vaultOf(path), path: inVault(path) } } })),
    retry: false,
  });
