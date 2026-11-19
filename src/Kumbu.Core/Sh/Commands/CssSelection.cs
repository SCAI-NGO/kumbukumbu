// SPDX-License-Identifier: AGPL-3.0-or-later
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using System.Runtime.CompilerServices;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// The elements a CSS selector picks, in document order. AngleSharp's own matching tries every ancestor for each space
/// in turn, so 'p div div div' on a page of 1,000 nested divs takes minutes and each further space multiplies that:
/// here a chain of combinators is matched one compound at a time over the whole page, in time linear in the page, and
/// AngleSharp matches only the compounds. A few pseudo-classes cost more (:nth-child() counts the siblings before each
/// element); the token, checked at every element, stops them at the call's budget. Inside a pseudo-class's argument
/// AngleSharp matches on its own again, where the token can't stop it, so <see cref="Refusal"/> keeps out what would
/// be quadratic or worse there.
/// </summary>
internal static class CssSelection
{
    private static readonly string[] Combinators = [" ", ">", "+", "~"];

    /// <summary>The pseudo-classes whose argument is a selector.</summary>
    private static readonly string[] Selecting = ["not", "is", "where", "matches", "has"];

    /// <summary>The pseudo-classes :has() takes: :scope is what AngleSharp writes for its leading >.</summary>
    private static readonly string[] InHas = ["not", "is", "where", "matches", "scope"];

    /// <summary>The longest selector matched: every alternative and every pseudo-class's argument adds to one match, and
    /// a selector of a thousand characters is already far longer than one written for a page.</summary>
    public const int MaxLength = 1000;

    /// <summary>The most alternatives (a, b) a selector has, its arguments' included: inside :has() each one is tried on
    /// every element below, so a hundred cost a hundred walks of the page in one match the token can't stop.</summary>
    public const int MaxAlternatives = 100;

    /// <summary>Why the selector isn't matched, or null when it is; <paramref name="text"/> is the selector as given.</summary>
    public static string? Refusal(string text, ISelector selector)
    {
        if (text.Length > MaxLength) return $"a selector is at most {MaxLength:N0} characters long";
        // AngleSharp drops a combinator that comes first: '> a' would pick every a.
        if (text.TrimStart() is [('>' or '+' or '~') and var first, ..]) return $"a selector can't start with {first}; name the element it relates to first (body > a)";
        var check = new Check(nested: false, inHas: false, context: "", new StrongBox<int>());
        selector.Accept(check);
        return check.Refusal;
    }

    public static List<IElement> Select(IDocument document, ISelector selector, CancellationToken ct)
    {
        // The page in document order, and each element's previous sibling: AngleSharp finds that by a walk through the parent's children.
        var order = new List<IElement>();
        var previous = new Dictionary<IElement, IElement>();
        var stack = new Stack<IElement>();
        if (document.DocumentElement is { } root) stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var element = stack.Pop();
            order.Add(element);
            var children = element.Children.ToList();
            for (var i = 1; i < children.Count; i++) previous[children[i]] = children[i - 1];
            for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }

        var shape = new Shape();
        selector.Accept(shape);
        var picked = new HashSet<IElement>();
        foreach (var alternative in shape.Alternatives ?? [selector]) picked.UnionWith(Chain(order, previous, alternative, ct));
        return [.. order.Where(picked.Contains)];
    }

    /// <summary>The elements that match the last compound of a chain: the elements matching the first, then step by step
    /// those of the next compound that stand in the combinator's relation to one of them.</summary>
    private static HashSet<IElement> Chain(List<IElement> order, Dictionary<IElement, IElement> previous, ISelector selector, CancellationToken ct)
    {
        var shape = new Shape();
        selector.Accept(shape);
        var parts = shape.Parts ?? [selector];
        var current = new HashSet<IElement>();
        foreach (var element in order)
        {
            ct.ThrowIfCancellationRequested();
            if (parts[0].Match(element, null)) current.Add(element);
        }

        for (var i = 1; i < parts.Count; i++)
        {
            var symbol = shape.Symbols![i - 1];
            // An element with an ancestor (a space) or an earlier sibling (~) among the current ones: parents and earlier
            // siblings come first in document order, so each element looks only at its parent or its previous sibling.
            var reached = new HashSet<IElement>();
            var next = new HashSet<IElement>();
            foreach (var element in order)
            {
                ct.ThrowIfCancellationRequested();
                var related = symbol switch
                {
                    ">" => element.ParentElement is { } parent && current.Contains(parent),
                    "+" => previous.TryGetValue(element, out var before) && current.Contains(before),
                    " " => element.ParentElement is { } parent && (current.Contains(parent) || reached.Contains(parent)),
                    _ => previous.TryGetValue(element, out var before) && (current.Contains(before) || reached.Contains(before)),
                };
                if (!related) continue;
                if (symbol is " " or "~") reached.Add(element);
                if (parts[i].Match(element, null)) next.Add(element);
            }

            current = next;
        }

        return current;
    }

    /// <summary>A selector's alternatives (a, b) or its chain of compounds and combinators; a compound alone has neither.</summary>
    private sealed class Shape : ISelectorVisitor
    {
        public List<ISelector>? Alternatives { get; private set; }

        public List<ISelector>? Parts { get; private set; }

        public List<string>? Symbols { get; private set; }

        public void List(IEnumerable<ISelector> selectors) => Alternatives = [.. selectors];

        public void Combinator(IEnumerable<ISelector> selectors, IEnumerable<string> symbols)
        {
            Parts = [.. selectors];
            Symbols = [.. symbols];
        }

        public void Attribute(string name, string op, string? value) { }

        public void Type(string name) { }

        public void Id(string value) { }

        public void Child(string name, int step, int offset, ISelector kind) { }

        public void Class(string name) { }

        public void PseudoClass(string name) { }

        public void PseudoElement(string name) { }

        public void Many(IEnumerable<ISelector> selectors) { }
    }

    /// <summary>
    /// What AngleSharp would match slowly inside a pseudo-class's argument, which it matches for every element the
    /// pseudo-class is tried on: a space or ~ tries every ancestor or earlier sibling. Inside :has(), which is tried on
    /// every element below in one match, only type, class, id and attribute selectors, combined by > or in :not(),
    /// :is() and :where(): + and ~, and the pseudo-classes that look through all siblings (:nth-child(), :valid on a
    /// group of radio buttons), all ancestors (:lang()) or all text (:contains()) would make that one match quadratic.
    /// <paramref name="context"/> names the pseudo-classes the selector is the argument of, for the messages.
    /// </summary>
    private sealed class Check(bool nested, bool inHas, string context, StrongBox<int> alternatives) : ISelectorVisitor
    {
        private const string Not = ":not(), :is() and :where()";

        public string? Refusal { get; private set; }

        private void Visit(ISelector selector, bool inArgument, bool inHasArgument, string argumentOf)
        {
            if (Refusal is not null) return;
            var check = new Check(inArgument, inHasArgument, argumentOf, alternatives);
            selector.Accept(check);
            Refusal = check.Refusal;
        }

        public void List(IEnumerable<ISelector> selectors)
        {
            var list = selectors.ToList();
            alternatives.Value += list.Count;
            if (alternatives.Value > MaxAlternatives) Refusal ??= $"a selector has at most {MaxAlternatives} alternatives; name fewer";
            foreach (var selector in list) Visit(selector, nested, inHas, context);
        }

        public void Many(IEnumerable<ISelector> selectors)
        {
            foreach (var selector in selectors) Visit(selector, nested, inHas, context);
        }

        public void Combinator(IEnumerable<ISelector> selectors, IEnumerable<string> symbols)
        {
            foreach (var symbol in symbols)
            {
                if (!Combinators.Contains(symbol)) Refusal ??= $"the combinator {symbol} isn't read here; name the element alone";
                else if (inHas && symbol != ">") Refusal ??= "inside :has() only > combines selectors, as a space, + or ~ there would look at every element below every element";
                else if (nested && symbol is " " or "~") Refusal ??= $"inside {context} only > and + combine selectors, as a space or ~ there would try every ancestor or earlier sibling of every element";
            }

            foreach (var selector in selectors) Visit(selector, nested, inHas, context);
        }

        public void Child(string name, int step, int offset, ISelector kind)
        {
            if (inHas) Refused($":{name}()");
            else Visit(kind, inArgument: true, inHas, $":{name}()");
        }

        public void PseudoClass(string name)
        {
            var open = name.IndexOf('(');
            var bare = open < 0 ? name : name[..open];
            if (inHas && bare == "has") Refusal ??= ":has() inside :has() isn't matched (CSS doesn't allow it either)";
            else if (inHas && !InHas.Contains(bare)) Refused(open < 0 ? $":{bare}" : $":{bare}()");
            else if (open > 0 && Selecting.Contains(bare) && new CssSelectorParser().ParseSelector(name[(open + 1)..^1]) is { } argument)
            {
                var shape = new Shape();
                argument.Accept(shape);
                // AngleSharp reads a > that starts a later alternative of :has() as a space: :has(> a, > b) would find b anywhere below.
                if (bare == "has" && shape.Alternatives is not null) Refusal ??= "inside :has(), a list (a, b) isn't matched, as AngleSharp misreads it; write :has(> :is(a, b))";
                else Visit(argument, inArgument: true, inHas || bare == "has", bare == "has" ? ":has()" : Not);
            }
        }

        public void PseudoElement(string name)
        {
            if (inHas) Refused($"::{name}");
        }

        private void Refused(string what) =>
            Refusal ??= $"inside :has(), {what} isn't matched: only type, class, id and attribute selectors are, combined by > or in {Not}, as anything else can take minutes there";

        public void Attribute(string name, string op, string? value) { }

        public void Type(string name) { }

        public void Id(string value) { }

        public void Class(string name) { }
    }
}
