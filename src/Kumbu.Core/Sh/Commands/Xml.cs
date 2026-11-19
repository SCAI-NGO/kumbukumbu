// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// Reads an XML file (spec 2026-11-19, decision 9): its shape, or the nodes an XPath 1.0 expression picks. A document
/// with a DTD is refused rather than read without it: an entity left out would give wrong values. So no entity is
/// expanded and nothing outside the file is read (no XXE): the reader prohibits DTDs and has no resolver. An XPath is
/// evaluated over a navigator that checks the call's token at every step, as one can walk the document once for every
/// node ('//*[count(//*) > 0]'): evaluating and printing stop at <see cref="Budget"/>.
/// </summary>
public sealed partial class Xml(INoteReader notes) : ICommand
{
    private static readonly Option[] Spec = [new(null, "xml"), new(null, "count"), new(null, "ns", TakesValue: true)];

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        // 0 would mean no limit; with DTDs prohibited no entity can be declared, and the predefined ones (&amp;) don't count.
        MaxCharactersFromEntities = 1,
    };

    /// <summary>How long one call may evaluate and print; the steps of an ordinary XPath over a 15 MB file take well under a second.</summary>
    internal TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The prefix the document's default namespace is offered as.</summary>
    private const string DefaultPrefix = "d";

    public string Name => "xml";

    public string Usage => "xml [--xml|--count] [--ns prefix=uri]… <path> [xpath]";

    public string Summary => "read an XML file: its root and elements, or the nodes an XPath picks";

    public string Help => """
        xml [--xml|--count] [--ns prefix=uri]… <path> [xpath]
          Read an XML file (a text file, whatever its name). Without an XPath: the root element, its
          namespaces, and the names of the elements two levels below it, with how often each occurs.
          With an XPath 1.0 expression: each node it picks, one per line, as its text with whitespace collapsed
          (an element's text, an attribute's value); a number, boolean or string result prints as it is.
          --xml              each node's XML instead, the element included
          --count            how many nodes the XPath picks
          --ns prefix=uri    a prefix for a namespace, for the XPath (repeat it for several). The prefixes the root
                             element declares work as they are, and the document's default namespace is the prefix d:
                             in an Atom feed, //d:entry/d:title. XPath names an element without a prefix only in no namespace.
          A document with a DTD (<!DOCTYPE …>) is refused: Kumbu² neither expands its entities nor reads anything
          outside the file. A call stops after 10 seconds.

          xml /Work/feed.xml
          xml /Work/feed.xml '//item/title'
          xml /Work/feed.xml 'count(//item)'
          xml --xml /Work/feed.xml '//item[1]'
          xml /Work/atom.xml '//d:entry/d:link/@href'
          xml --ns a=http://www.w3.org/2005/Atom /Work/atom.xml '//a:entry/a:title'
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var modes = new[] { "xml", "count" }.Where(options.Has).ToList();
        if (modes.Count > 1) return ShellResult.Error("xml: choose one of --xml and --count");
        if (options.Operands.Count == 0) return ShellResult.Error("xml: missing operand (give the path of an XML file)");
        if (options.Operands.Count > 2) return ShellResult.Error($"xml: too many arguments{CommandArgs.QuoteHint}");
        if (options.Operands.Count == 1 && modes.Count == 1) return ShellResult.Error($"xml: --{modes[0]} needs an XPath");
        var prefixes = new List<(string Prefix, string Uri)>();
        foreach (var ns in options.Values("ns"))
        {
            var eq = ns.IndexOf('=');
            if (eq <= 0 || eq == ns.Length - 1) return ShellResult.Error($"xml: --ns takes prefix=uri (got '{ns}')");
            if (ns[..eq] is "xml" or "xmlns") return ShellResult.Error($"xml: --ns can't define the prefix {ns[..eq]}, which XML reserves");
            prefixes.Add((ns[..eq], ns[(eq + 1)..]));
        }

        var file = await TextFiles.ReadAsync(Name, "XML", notes, options.Operands[0], ct);
        if (file.IsError) return file;
        var path = options.Operands[0];
        if (Parse(file.Output) is not { } root)
        {
            return ShellResult.Error(HasDtd(file.Output)
                ? $"xml: {Shown(path)} has a DTD, which Kumbu² doesn't process"
                : $"xml: {Shown(path)} isn't well-formed XML: {Problem(file.Output)}");
        }

        if (options.Operands.Count == 1) return Overview(root);

        var xpath = options.Operands[1];
        XPathExpression expression;
        try
        {
            expression = XPathExpression.Compile(xpath);
        }
        catch (XPathException)
        {
            return ShellResult.Error($"xml: not an XPath expression: '{xpath}'");
        }

        var manager = new XmlNamespaceManager(root.NameTable);
        var declared = root.GetNamespacesInScope(XmlNamespaceScope.Local);
        foreach (var (prefix, uri) in declared.Where(p => p.Key.Length > 0)) manager.AddNamespace(prefix, uri);
        // d is the default namespace unless the root declares a prefix d of its own; --ns comes last and wins.
        var defaultNamespace = root.GetNamespace("");
        if (defaultNamespace.Length > 0 && !declared.ContainsKey(DefaultPrefix)) manager.AddNamespace(DefaultPrefix, defaultNamespace);
        foreach (var (prefix, uri) in prefixes) manager.AddNamespace(prefix, uri);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        try
        {
            return Picked(new Guarded(root, budget.Token), expression, manager, xpath, modes, options.Has("xml"), defaultNamespace);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ShellResult.Error($"xml: '{xpath}' took longer than {Budget.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds on {Shown(path)}; narrow the XPath");
        }
    }

    private static ShellResult Picked(
        XPathNavigator root, XPathExpression expression, XmlNamespaceManager manager, string xpath, List<string> modes, bool asXml, string defaultNamespace)
    {
        object result;
        try
        {
            expression.SetContext(manager);
            result = root.Evaluate(expression);
        }
        catch (XPathException)
        {
            // Prefixes and functions are looked up only now: an unknown function is no XPath 1.0 either.
            return ShellResult.Error(HasPrefix(xpath)
                ? $"xml: '{xpath}' uses a prefix that isn't defined; define it with --ns prefix=uri"
                : $"xml: not an XPath expression: '{xpath}'");
        }

        if (result is not XPathNodeIterator nodes)
        {
            return modes.Count == 1
                ? ShellResult.Error($"xml: --{modes[0]} needs an XPath that picks nodes; '{xpath}' gives {Kind(result)}")
                : ShellResult.Ok(Value(result));
        }

        var picked = new List<XPathNavigator>();
        while (nodes.MoveNext()) picked.Add(nodes.Current!.Clone());
        if (modes.Contains("count")) return ShellResult.Ok(picked.Count.ToString(CultureInfo.InvariantCulture));
        if (picked.Count == 0)
        {
            var hint = defaultNamespace.Length > 0 && !HasPrefix(xpath)
                ? $"xml: nothing matched; the document's default namespace is {defaultNamespace}, so name its elements as d:<name> (//d:{FirstChildName(root)})"
                : $"xml: nothing matched '{xpath}'";
            return ShellResult.Ok("") with { NoMatchHint = hint };
        }

        // Made one by one, as the output takes them: an element's XML or text holds all below it.
        return TextFiles.Listed("xml", picked.Count, picked.Select(n => asXml ? Outer(n) : TextFiles.Collapse(n.Value)), "\n", "nodes", "narrow the XPath");
    }

    /// <summary>A navigator that checks the token at every step and every value it reads, so that whatever an XPath
    /// walks, it stops soon after the token is cancelled: a step costs at most one walk of the document.</summary>
    private sealed class Guarded(XPathNavigator inner, CancellationToken ct) : XPathNavigator
    {
        private XPathNavigator Step()
        {
            ct.ThrowIfCancellationRequested();
            return inner;
        }

        public override string BaseURI => inner.BaseURI;

        public override bool IsEmptyElement => inner.IsEmptyElement;

        public override string LocalName => inner.LocalName;

        public override string Name => inner.Name;

        public override string NamespaceURI => inner.NamespaceURI;

        public override XmlNameTable NameTable => inner.NameTable;

        public override XPathNodeType NodeType => inner.NodeType;

        public override string Prefix => inner.Prefix;

        public override string Value => Step().Value;

        public override XPathNavigator Clone() => new Guarded(Step().Clone(), ct);

        public override bool IsSamePosition(XPathNavigator other) => other is Guarded guarded && inner.IsSamePosition(guarded.Inner);

        // The inner navigator compares by its nodes' place in the document; the base class walks siblings to find out,
        // which made sorting a union quadratic.
        public override XmlNodeOrder ComparePosition(XPathNavigator? other) =>
            other is Guarded guarded ? Step().ComparePosition(guarded.Inner) : XmlNodeOrder.Unknown;

        public override bool IsDescendant(XPathNavigator? other) => other is Guarded guarded && Step().IsDescendant(guarded.Inner);

        public override bool MoveTo(XPathNavigator other) => other is Guarded guarded && Step().MoveTo(guarded.Inner);

        public override bool MoveToFirstAttribute() => Step().MoveToFirstAttribute();

        public override bool MoveToNextAttribute() => Step().MoveToNextAttribute();

        public override bool MoveToFirstNamespace(XPathNamespaceScope namespaceScope) => Step().MoveToFirstNamespace(namespaceScope);

        public override bool MoveToNextNamespace(XPathNamespaceScope namespaceScope) => Step().MoveToNextNamespace(namespaceScope);

        public override bool MoveToNext() => Step().MoveToNext();

        public override bool MoveToPrevious() => Step().MoveToPrevious();

        public override bool MoveToFirstChild() => Step().MoveToFirstChild();

        public override bool MoveToParent() => Step().MoveToParent();

        public override bool MoveToId(string id) => Step().MoveToId(id);

        private XPathNavigator Inner => inner;
    }

    private static readonly XmlWriterSettings Unindented = new() { OmitXmlDeclaration = true, Indent = false, ConformanceLevel = ConformanceLevel.Fragment };

    /// <summary>A node's XML as the file writes it (the outer XML .NET gives an element is indented anew).</summary>
    private static string Outer(XPathNavigator node)
    {
        // An attribute, a text or a comment is one line already: isPermaLink="false".
        if (node.NodeType != XPathNodeType.Element) return node.OuterXml;
        var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(text, Unindented)) writer.WriteNode(node, defattr: true);
        return text.ToString();
    }

    /// <summary>The root element, or null when the document has a DTD or isn't well-formed.</summary>
    private static XPathNavigator? Parse(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), Settings);
            // Whitespace kept: between two inline elements (<b>x</b> <i>y</i>) it is part of the text.
            var navigator = new XPathDocument(reader, XmlSpace.Preserve).CreateNavigator();
            navigator.MoveToChild(XPathNodeType.Element);
            return navigator;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Whether a document that didn't parse failed on its DTD: a DTD stands before the root element, where a
    /// reader that ignores DTDs gets past what one that prohibits them doesn't.</summary>
    private static bool HasDtd(string text)
    {
        if (Prolog(text, Settings) is null) return false;
        return Prolog(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }) is null;
    }

    /// <summary>The error reading up to the root element gives; null when it gets there.</summary>
    private static XmlException? Prolog(string text, XmlReaderSettings settings)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            reader.MoveToContent();
            return null;
        }
        catch (XmlException e)
        {
            return e;
        }
    }

    /// <summary>Why the document isn't well-formed, and on which line: the reader's message without its position.</summary>
    private static string Problem(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), Settings);
            while (reader.Read())
            {
            }

            return "it ends too early";
        }
        catch (XmlException e)
        {
            var message = Position().Replace(e.Message, "").TrimEnd('.');
            message = message.Length > 0 ? char.ToLowerInvariant(message[0]) + message[1..] : message;
            return e.LineNumber > 0 ? $"{message} (line {e.LineNumber})" : message;
        }
    }

    private static ShellResult Overview(XPathNavigator root)
    {
        var namespaces = new List<string>();
        foreach (var (prefix, uri) in root.GetNamespacesInScope(XmlNamespaceScope.Local).OrderBy(p => p.Key.Length > 0).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            namespaces.Add(prefix.Length == 0 ? $"{uri} (the default: name its elements as {DefaultPrefix}:<name>)" : $"{prefix}={uri}");
        }

        var lines = new List<string> { $"root: {root.Name}", $"namespaces: {(namespaces.Count == 0 ? "none" : string.Join(", ", namespaces))}" };
        foreach (var (name, children) in Counted(Children(root)))
        {
            lines.Add($"  {name} {children.Count}");
            foreach (var (below, grandchildren) in Counted(children.SelectMany(Children))) lines.Add($"    {below} {grandchildren.Count}");
        }

        return TextFiles.Listed("xml", lines, "\n", "lines", "pick elements with an XPath (xml <path> '/*/*')");
    }

    private static IEnumerable<XPathNavigator> Children(XPathNavigator element)
    {
        var child = element.Clone();
        if (!child.MoveToChild(XPathNodeType.Element)) yield break;
        do yield return child.Clone();
        while (child.MoveToNext(XPathNodeType.Element));
    }

    /// <summary>Elements grouped by their name as the document writes it, in the order each name first occurs.</summary>
    private static IEnumerable<(string Name, List<XPathNavigator> Elements)> Counted(IEnumerable<XPathNavigator> elements) =>
        elements.GroupBy(e => e.Name, StringComparer.Ordinal).Select(g => (g.Key, g.ToList()));

    private static string FirstChildName(XPathNavigator root) => Children(root).FirstOrDefault()?.LocalName ?? root.LocalName;

    /// <summary>Whether an XPath names a prefix: a single colon outside its string literals (an axis has two).</summary>
    private static bool HasPrefix(string xpath) => Prefixed().IsMatch(Literal().Replace(xpath, ""));

    private static string Value(object result) => result switch
    {
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => result.ToString() ?? "",
    };

    private static string Kind(object result) => result switch
    {
        double => "a number",
        bool => "a boolean",
        _ => "a string",
    };

    // The path as the other errors name it; ReadAsync has parsed it already.
    private static string Shown(string path) => Paths.VaultPath.TryParse(path, out var parsed) ? parsed.Value : path;

    [GeneratedRegex(@"\s*Line \d+, position \d+\.?$")]
    private static partial Regex Position();

    [GeneratedRegex("\"[^\"]*\"|'[^']*'")]
    private static partial Regex Literal();

    [GeneratedRegex("(?<!:):(?!:)")]
    private static partial Regex Prefixed();
}
