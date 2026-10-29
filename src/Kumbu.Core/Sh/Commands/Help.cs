// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Help(IReadOnlyDictionary<string, ICommand> commands) : ICommand
{
    public string Name => "help";

    public string Usage => "help [command]";

    public string Summary => "this list, or the details of one command";

    string ICommand.Help => """
        help [command]
          List the commands, or show the options and examples of one. 'cmd --help' does the same.
          'help checkpoints' explains reading, comparing and restoring named checkpoints;
          'help attachments' what Kumbu² keeps besides notes (images and PDFs) and what works on them.
        """;

    /// <summary>Topics that are not commands: explained once here, so each reader's help only points to them.</summary>
    private static readonly Dictionary<string, string> Topics = new(StringComparer.Ordinal)
    {
        ["checkpoints"] = $"""
            checkpoints
              A checkpoint is a named state of a note, a folder or a whole vault ('mkcp'), kept until
              'rmcp' removes it; 'lscp' lists them. Every reader works on it as on the current state:
                {string.Join(", ", Shell.Readers)}
              take --checkpoint <name> (or --checkpoint=<name>) with a path in the checkpoint's vault, and
              then see the notes as the checkpoint holds them; links resolve among the checkpoint's notes.
              A checkpoint holds one version of each note, so --checkpoint doesn't go with --version, and
              it reads notes itself, so it doesn't work right of a pipe (it can start one).
              diff --checkpoint <name> <path> shows what changed since; restore --checkpoint <name> <path>
              goes back to it (see 'help diff', 'help restore').

              lscp /Demo
              ls --checkpoint before-cleanup /Demo/projects/
              cat --checkpoint before-cleanup /Demo/plan.md | rg -n TODO
              links --checkpoint before-cleanup /Demo/welcome.md
            """,
        ["attachments"] = $"""
            attachments
              Besides notes, a vault keeps images and PDFs: .png, .jpg, .jpeg, .gif, .webp and .pdf, up to
              15 MB each. Nothing else is accepted. People upload them (the web app, Obsidian); the write,
              edit and replace tools refuse an attachment path, since they only write text.
              A note links to one by its name with the extension: ![[shot.png]], ![alt](/Screens/shot.png),
              [[report.pdf]]; the rules are those of 'help links'.

              What works on an attachment:
                ls         lists it with its kind and size, an image with its pixel size:
                             shot.png  image 1440×3120 412 KB
                cat        prints one line instead of bytes: shot.png: image (PNG), 1440×3120, 412 KB, and for a PDF
                             paper.pdf: PDF, 12 pages (1-3 text, 4 mixed, 5-12 text), 2.1 MB
                mv         moves or renames it (a folder move takes it along); links and embeds follow
                rm         moves it to the trash ({NoteWriter.TrashRetention.Days} days) and says which links now lead nowhere
                trash, restore, history   as for notes: an earlier version, or one from the trash, comes back
                links, backlinks          backlinks lists the notes that link to it or embed it
                img        shows an image to the model, whole or a part of it ('help img')
                pdf        reads a PDF page by page: its text, pages as images, a part of a page ('help pdf')
              rg         searches the text of PDFs (not scans) as well as notes: /Work/paper.pdf:p7: text
              semsearch  ranks the pages of PDFs (not scans) by meaning among the sections of notes:
                           1. /Work/paper.pdf p7  score 0.61
              What doesn't: semsearch --words and --tag find notes only, 'rg --files' lists notes only;
              neither rg nor semsearch searches images, and rg not PDFs in a checkpoint; head, tail,
              wc, sort and outline read notes, not attachments.
              A checkpoint holds the attachments too (see 'help checkpoints').

              ls /Work/Screens
              cat /Work/Screens/shot.png
              pdf info /Work/Papers/paper.pdf
              backlinks /Work/Screens/shot.png
              mv /Work/Screens/shot.png /Work/archive/
            """,
    };

    /// <summary>A command's help; a reader's ends with a pointer to the checkpoint topic.</summary>
    public static string Of(ICommand command) => Shell.Readers.Contains(command.Name)
        ? command.Help.TrimEnd() + "\n  --checkpoint <name>: as a checkpoint holds it; see 'help checkpoints'"
        : command.Help;

    public bool ReadsInput => false;

    public Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return Task.FromResult(optionError);
        if (args.Count > 1) return Task.FromResult(ShellResult.Error("help: too many arguments"));

        if (args.Count == 1)
        {
            if (Topics.TryGetValue(args[0], out var topic)) return Task.FromResult(ShellResult.Ok(topic));
            return Task.FromResult(commands.TryGetValue(args[0], out var command)
                ? ShellResult.Ok(Of(command))
                : ShellResult.Error($"help: no such command: {args[0]} — try 'help'"));
        }

        var filters = string.Join(", ", commands.Values.Where(c => c.ReadsInput).Select(c => c.Name));
        var width = commands.Values.Max(c => c.Usage.Length) + 2;
        var list = string.Join('\n', commands.Values.Select(c => "  " + c.Usage.PadRight(width) + c.Summary));
        return Task.FromResult(ShellResult.Ok($"""
            Commands:
            {list}

            '|' pipes the output of one command into the next; right of a pipe only {filters} work.
            All paths are absolute (there is no working directory). Run 'help <command>' for details,
            'help checkpoints' for reading a checkpoint, 'help attachments' for images and PDFs.
            """));
    }
}
