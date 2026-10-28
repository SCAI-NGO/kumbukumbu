// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

/// <summary>One piece of a result that mixes text and images; the client gets them in order.</summary>
public abstract record ShellPart;

public sealed record TextPart(string Text) : ShellPart;

public sealed record ImagePart(byte[] Data, string MediaType) : ShellPart;
