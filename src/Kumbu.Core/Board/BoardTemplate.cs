// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Board;

public static class BoardTemplate
{
    /// <summary>The <c>Board.md</c> a new board starts from.</summary>
    public static string Text(string vault) => $$"""
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
        # {{vault}}

        Kanban board for {{vault}}.

        ## Instructions

        Don't let ideas go stale: when Today and Backlog run short, look at Ideas. Reject a card only with a reason.
        Today is what comes next, not literally today.

        """;
}
