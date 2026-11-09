// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Dav;

/// <summary>The methods <c>/dav</c> answers, in one place: the route, the dispatch and the <c>Allow</c> header all
/// follow this list.</summary>
public static class DavMethod
{
    public const string Options = "OPTIONS";
    public const string Propfind = "PROPFIND";
    public const string Proppatch = "PROPPATCH";
    public const string Get = "GET";
    public const string Head = "HEAD";
    public const string Put = "PUT";
    public const string Delete = "DELETE";
    public const string Mkcol = "MKCOL";
    public const string Copy = "COPY";
    public const string Move = "MOVE";
    public const string Lock = "LOCK";
    public const string Unlock = "UNLOCK";

    public static IReadOnlyList<string> All { get; } = [Options, Propfind, Proppatch, Get, Head, Put, Delete, Mkcol, Copy, Move, Lock, Unlock];
}
