// SPDX-License-Identifier: AGPL-3.0-or-later
import { useIsMutating, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call } from "./client";
import type { components } from "./schema";

export type BoardView = components["schemas"]["BoardView"];
export type BoardColumn = components["schemas"]["ColumnView"];
export type BoardCard = components["schemas"]["CardView"];
export type AddCard = components["schemas"]["AddCardRequest"];
export type MoveCard = components["schemas"]["MoveCardRequest"];
/** `clearDue` and `clearTags` default to false on the server; the generated type has them required. */
export type UpdateCard = Pick<components["schemas"]["UpdateCardRequest"], "card"> & Partial<components["schemas"]["UpdateCardRequest"]>;
export type RenameColumn = components["schemas"]["RenameColumnRequest"];

const boardKey = (vault: string, checkpoint: string | null = null) => ["board", vault, checkpoint];

/**
 * The vault's board, or a checkpoint's as it was. Today's board is asked again every few seconds while the page is
 * visible, as the open note's version is: a move by an agent, Obsidian or another tab shows by itself. A
 * checkpoint's never changes, and is kept only while shown (one removed and made again under the name must not
 * show the old board). The poll pauses while a change is under way: one that started before the change's answer
 * could come back after it, with the board from before.
 */
export function useBoard(vault: string, checkpoint: string | null = null, interval = 5_000) {
  const changing = useIsMutating({ mutationKey: ["board", vault] }) > 0;
  return useQuery({
    queryKey: boardKey(vault, checkpoint),
    queryFn: () => call(api.GET("/api/vaults/{vault}/board", { params: { path: { vault }, query: checkpoint ? { checkpoint } : {} } })),
    retry: false,
    ...(checkpoint
      ? { refetchOnWindowFocus: false, gcTime: 0 }
      : { refetchInterval: changing ? false : interval, refetchIntervalInBackground: false, refetchOnWindowFocus: changing ? false : ("always" as const) }),
  });
}

export type BoardAction =
  | { kind: "create" }
  | ({ kind: "add" } & AddCard)
  | ({ kind: "move" } & MoveCard)
  | ({ kind: "update" } & UpdateCard)
  | ({ kind: "rename-column" } & RenameColumn);

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
      return call(api.POST("/api/vaults/{vault}/board/update", { params, body: body as components["schemas"]["UpdateCardRequest"] }));
    }
    case "rename-column": {
      const { kind: _, ...body } = action;
      return call(api.POST("/api/vaults/{vault}/board/rename-column", { params, body }));
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
    // Known to useBoard, whose poll waits meanwhile; a poll already under way would bring back the board from before.
    mutationKey: ["board", vault],
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
