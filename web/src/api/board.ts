// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call } from "./client";
import type { components } from "./schema";

export type BoardView = components["schemas"]["BoardView"];
export type BoardColumn = components["schemas"]["ColumnView"];
export type BoardCard = components["schemas"]["CardView"];
export type AddCard = components["schemas"]["AddCardRequest"];
export type MoveCard = components["schemas"]["MoveCardRequest"];
export type UpdateCard = components["schemas"]["UpdateCardRequest"];

const boardKey = (vault: string, checkpoint: string | null = null) => ["board", vault, checkpoint];

/**
 * The vault's board, or a checkpoint's as it was. Today's board is asked again every few seconds while the page is
 * visible, as the open note's version is: a move by an agent, Obsidian or another tab shows by itself. A
 * checkpoint's never changes, and is kept only while shown (one removed and made again under the name must not
 * show the old board).
 */
export const useBoard = (vault: string, checkpoint: string | null = null, interval = 5_000) =>
  useQuery({
    queryKey: boardKey(vault, checkpoint),
    queryFn: () => call(api.GET("/api/vaults/{vault}/board", { params: { path: { vault }, query: checkpoint ? { checkpoint } : {} } })),
    retry: false,
    ...(checkpoint
      ? { refetchOnWindowFocus: false, gcTime: 0 }
      : { refetchInterval: interval, refetchIntervalInBackground: false, refetchOnWindowFocus: "always" as const }),
  });

export type BoardAction =
  | { kind: "create" }
  | ({ kind: "add" } & AddCard)
  | ({ kind: "move" } & MoveCard)
  | ({ kind: "update" } & UpdateCard);

function send(vault: string, action: BoardAction) {
  const params = { path: { vault } };
  switch (action.kind) {
    case "create":
      return call(api.POST("/api/vaults/{vault}/board", { params }));
    case "add": {
      const { kind: _, ...body } = action;
      return call(api.POST("/api/vaults/{vault}/board/cards", { params, body }));
    }
    case "move": {
      const { kind: _, ...body } = action;
      return call(api.POST("/api/vaults/{vault}/board/move", { params, body }));
    }
    case "update": {
      const { kind: _, ...body } = action;
      return call(api.POST("/api/vaults/{vault}/board/update", { params, body }));
    }
  }
}

/**
 * A change to the board: every action answers the board as it is afterwards, which replaces the one shown. A new
 * board or card is a new note, so the vault's tree and counts are asked again.
 */
export function useBoardAction(vault: string) {
  const queryClient = useQueryClient();
  return useMutation({
    // A poll under way would bring back the board from before the change.
    onMutate: () => queryClient.cancelQueries({ queryKey: boardKey(vault) }),
    mutationFn: (action: BoardAction) => send(vault, action),
    onSuccess: (board, action) => {
      queryClient.setQueryData(boardKey(vault), board);
      if (action.kind === "create" || action.kind === "add") {
        void queryClient.invalidateQueries({ queryKey: ["tree", vault] });
        void queryClient.invalidateQueries({ queryKey: ["vaults"] });
      }
      void queryClient.invalidateQueries({ queryKey: ["changes"] });
    },
  });
}
