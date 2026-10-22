// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;

namespace Kumbu.Core.Sh.Commands;

/// <summary>An option a command accepts. <see cref="Long"/> is also the name it is looked up by.</summary>
internal sealed record Option(char? Short, string Long, bool TakesValue = false);

/// <summary>Parsed arguments: options by long name, the rest as operands in order.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public List<string> Operands { get; } = [];

    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>The last value given for the option, or null.</summary>
    public string? Value(string name) => _values.TryGetValue(name, out var v) ? v[^1] : null;

    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var v) ? v : [];

    private void Add(string name, string value)
    {
        if (!_values.TryGetValue(name, out var list)) _values[name] = list = [];
        list.Add(value);
    }

    /// <summary>
    /// GNU-style parsing: combined short flags (-in), attached or separate values (-n5, -n 5),
    /// --long and --long=value, options after operands, and "--" to end options.
    /// <paramref name="digitsMean"/> makes "-5" mean that option with value 5 (head -5).
    /// </summary>
    public static bool TryParse(
        string command,
        IReadOnlyList<string> args,
        IReadOnlyList<Option> spec,
        [NotNullWhen(true)] out Options? options,
        [NotNullWhen(false)] out ShellResult? error,
        string? digitsMean = null)
    {
        var result = new Options();
        options = null;
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                result.Operands.AddRange(args.Skip(i + 1));
                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=');
                var name = eq < 0 ? arg[2..] : arg[2..eq];
                var option = spec.FirstOrDefault(o => o.Long == name);
                if (option is null)
                {
                    error = ShellResult.Error($"{command}: unrecognized option '--{name}'");
                    return false;
                }

                if (!option.TakesValue)
                {
                    if (eq >= 0)
                    {
                        error = ShellResult.Error($"{command}: option '--{name}' doesn't allow an argument");
                        return false;
                    }

                    result.Add(name, "");
                }
                else if (eq >= 0) result.Add(name, arg[(eq + 1)..]);
                else if (i + 1 < args.Count) result.Add(name, args[++i]);
                else
                {
                    error = ShellResult.Error($"{command}: option '--{name}' requires an argument");
                    return false;
                }

                continue;
            }

            if (arg.Length < 2 || arg[0] != '-')
            {
                result.Operands.Add(arg);
                continue;
            }

            if (digitsMean is not null && arg.Skip(1).All(char.IsAsciiDigit))
            {
                result.Add(digitsMean, arg[1..]);
                continue;
            }

            for (var j = 1; j < arg.Length; j++)
            {
                var c = arg[j];
                var option = spec.FirstOrDefault(o => o.Short == c);
                if (option is null)
                {
                    error = ShellResult.Error($"{command}: invalid option -- '{c}'");
                    return false;
                }

                if (!option.TakesValue)
                {
                    result.Add(option.Long, "");
                    continue;
                }

                if (j + 1 < arg.Length) result.Add(option.Long, arg[(j + 1)..]);
                else if (i + 1 < args.Count) result.Add(option.Long, args[++i]);
                else
                {
                    error = ShellResult.Error($"{command}: option requires an argument -- '{c}'");
                    return false;
                }

                break;
            }
        }

        options = result;
        return true;
    }
}
