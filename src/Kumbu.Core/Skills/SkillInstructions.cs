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

    private const string More = "more: sh `skills find \"<the user's request>\"`.";

    private const string UsersToDo = "New skills, sharing, moving between vaults and updates are the user's to do, in Kumbu²'s Skills screen.";

    private const string Hint = "Skills: the user keeps skills, instructions for tasks they do again, in the vault Skills, in a vault's _skills/ folder, and built in (sh: help skills). Before a task, run sh `skills find \"<the user's request>\"` and follow a skill whose description matches; read it with `skills cat <name>`. " + UsersToDo;

    /// <summary>The section for <paramref name="skills"/> in <see cref="SkillDirectory.ListAsync"/>'s order (system, the
    /// vault Skills, other vaults): the first <see cref="MaxEntries"/>, then how many more there are.</summary>
    public static string Section(InstructionsMode mode, IReadOnlyList<Skill> skills)
    {
        if (mode == InstructionsMode.HintOnly) return Hint;
        var text = new StringBuilder(ListHead);
        foreach (var skill in skills.Take(MaxEntries)) text.Append("\n- ").Append(skill.Line(MaxDescription));
        if (skills.Count > MaxEntries) text.Append($"\nand {skills.Count - MaxEntries} ").Append(More);
        return text.Append('\n').Append(UsersToDo).ToString();
    }
}
