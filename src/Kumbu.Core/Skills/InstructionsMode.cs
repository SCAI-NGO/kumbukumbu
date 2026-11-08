// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Skills;

/// <summary>How the skills reach the server instructions a credential is shown: the list of the skills it may read,
/// with a hint on how to read them, or only the hint. A preference about the text, never a right.</summary>
public enum InstructionsMode
{
    ListAndHint,
    HintOnly,
}
