// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Board;

public static class BoardTemplate
{
    /// <summary>
    /// The <c>Board.md</c> a new board starts from. It has no H1: the name a caller knows a shared vault by may be her
    /// alias, which is hers alone (collab design §2.3), and the owner and every member read this note. Without one the
    /// board shows each caller her own name for the vault (<see cref="BoardDefinition.TitleFor"/>).
    /// </summary>
    public static string Text() => """
        ---
        kumbu-board: 1
        columns:
          - Ideas
          - Backlog
          - Today
          - Doing
          - Done: [archivable]
          - Rejected: [archivable, reason]
        archive: { after: 30, minDays: 14 }
        ---
        Kanban board of this vault.

        ## Instructions

        Don't let ideas go stale: when Today and Backlog run short, look at Ideas. Reject a card only with a reason.
        Today is what comes next, not literally today.

        """;
}
