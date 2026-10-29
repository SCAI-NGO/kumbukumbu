// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Kumbu.Core.Sh.Commands;

/// <summary>Kumbu² as the system it plays: Kumbukumbu as the kernel, the running server's version as its release.</summary>
/// <param name="version">The build's version, commit included (<c>0.3.0+a6362ec</c>).</param>
public sealed class Uname(string version) : ICommand
{
    public const string KernelName = "Kumbukumbu";

    public const string OperatingSystem = "Kumbu²";

    public string Name => "uname";

    public string Usage => "uname [-asrvmo]";

    public string Summary => "the system: Kumbukumbu, and the version of the running server";

    public string Help => """
        uname [-asrvmo]
          Print what this system is: without options its name, Kumbukumbu.
          -s  the kernel name: Kumbukumbu
          -r  the release: the running server's version, e.g. 0.3.0
          -v  the build: the version with its commit, e.g. 0.3.0+a6362ec
          -m  the server's machine architecture, e.g. x86_64
          -o  the operating system: Kumbu²
          -a  all of them, in this order (also --all)

          uname -r
          uname -a
        """;

    public bool ReadsInput => false;

    public Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        var wanted = new HashSet<char>();
        foreach (var arg in args)
        {
            if (arg.Length < 2 || arg[0] != '-') return Task.FromResult(ShellResult.Error($"uname: extra operand '{arg}'"));
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg != "--all") return Task.FromResult(ShellResult.Error($"uname: unrecognized option '{arg}'"));
            foreach (var flag in arg == "--all" ? "a" : arg[1..])
            {
                if (!"asrvmo".Contains(flag)) return Task.FromResult(ShellResult.Error($"uname: invalid option -- '{flag}'"));
                wanted.Add(flag);
            }
        }

        if (wanted.Count == 0) wanted.Add('s');
        var all = wanted.Contains('a');
        string[] parts =
        [
            .. all || wanted.Contains('s') ? [KernelName] : Array.Empty<string>(),
            .. all || wanted.Contains('r') ? [version.Split('+')[0]] : Array.Empty<string>(),
            .. all || wanted.Contains('v') ? [version] : Array.Empty<string>(),
            .. all || wanted.Contains('m') ? [Machine] : Array.Empty<string>(),
            .. all || wanted.Contains('o') ? [OperatingSystem] : Array.Empty<string>(),
        ];
        return Task.FromResult(ShellResult.Ok(string.Join(' ', parts)));
    }

    // The names uname prints on Linux, not .NET's.
    private static string Machine => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "i686",
        var other => other.ToString().ToLowerInvariant(),
    };
}
