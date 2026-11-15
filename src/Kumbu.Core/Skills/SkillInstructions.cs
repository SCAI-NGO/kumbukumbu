// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Kumbu.Core.Skills;

/// <summary>
/// The skills section of the server instructions a connection is shown at its handshake: the skills it may read, one
/// line each (<see cref="Skill.Line"/>, so a description can't pose as another entry), with how to read them; or, in
/// <see cref="InstructionsMode.HintOnly"/>, only how to find them.
/// </summary>
public static class SkillInstructions
{
    public const int MaxEntries = 50, MaxDescription = 300;

    private const string ListHead = "Skills: instructions the user keeps for tasks they do again (sh: help skills). Before a task, look for a skill whose description matches it and follow it: read it with sh `skills cat <name>`, its files with `skills cat <name>/<file>`. Skills you may use:";

    public const string StartHead = "Skills for the start of every session: read each with sh `skills cat <name>` and follow it before your first answer, even to a greeting, alongside any start-up steps of your client or the project:";

    private const string More = "more: sh `skills find \"<the user's request>\"`.";

    private const string UsersToDo = "New skills, sharing, moving between vaults and updates are the user's to do, in Kumbu²'s Skills screen.";

    public const string Hint = "Skills: the user keeps skills, instructions for tasks they do again, in the vault Skills, in a vault's _skills/ folder, and built in (sh: help skills). Before a task, run sh `skills find \"<the user's request>\"` and follow a skill whose description matches; read it with `skills cat <name>`. " + UsersToDo;

    /// <summary>The section for <paramref name="skills"/> in <see cref="SkillDirectory.ListAsync"/>'s order (system, the
    /// vault Skills, other vaults): the start-up skills first under their own head, in list mode then the others, together
    /// at most <see cref="MaxEntries"/>, then how many more there are. Hint only names the start-up skills and the hint.</summary>
    public static string Section(InstructionsMode mode, IReadOnlyList<Skill> skills)
    {
        var start = skills.Where(s => s.AtStart).Take(MaxEntries).ToList();
        var hintOnly = mode == InstructionsMode.HintOnly;
        var others = hintOnly ? [] : skills.Where(s => !s.AtStart).Take(MaxEntries - start.Count).ToList();
        var text = new StringBuilder();
        if (start.Count > 0)
        {
            text.Append(StartHead);
            foreach (var skill in start) text.Append("\n- ").Append(skill.Line(MaxDescription));
            text.Append("\n\n");
        }

        if (hintOnly) return text.Append(Hint).ToString();
        text.Append(ListHead);
        foreach (var skill in others) text.Append("\n- ").Append(skill.Line(MaxDescription));
        var more = skills.Count - start.Count - others.Count;
        if (more > 0) text.Append($"\nand {more} ").Append(More);
        return text.Append('\n').Append(UsersToDo).ToString();
    }
}
