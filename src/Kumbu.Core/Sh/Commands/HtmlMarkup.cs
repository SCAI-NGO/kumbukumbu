// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Dom;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// An element's HTML as AngleSharp's <c>OuterHtml</c> writes it, made node by node only as far as the output reaches,
/// and stopped by the token.
/// </summary>
internal static class HtmlMarkup
{
    /// <summary>The element's HTML, at most its first <paramref name="limit"/> characters.</summary>
    public static string Of(IElement element, int limit, CancellationToken ct)
    {
        var formatter = HtmlMarkupFormatter.Instance;
        var output = new StringBuilder();
        var stack = new Stack<(INode Node, bool Close)>();
        stack.Push((element, false));
        while (stack.Count > 0 && output.Length < limit)
        {
            ct.ThrowIfCancellationRequested();
            var (node, close) = stack.Pop();
            switch (node)
            {
                case IElement e when close:
                    output.Append(formatter.CloseTag(e, false));
                    break;
                case IElement e:
                    output.Append(formatter.OpenTag(e, false));
                    if (e.Flags.HasFlag(NodeFlags.SelfClosing)) break;
                    stack.Push((e, true));
                    // By index: a node's siblings are found through its parent's list.
                    var children = e is IHtmlTemplateElement template ? template.Content.ChildNodes : e.ChildNodes;
                    // The parser drops a newline right after these opening tags, so a text that starts with one gets it back, as AngleSharp does.
                    if (e is IHtmlElement { LocalName: "pre" or "textarea" } && children.Length > 0 && children[0] is IText { Data: ['\n', ..] }) output.Append('\n');
                    for (var i = children.Length - 1; i >= 0; i--) stack.Push((children[i], false));
                    break;
                case IText text:
                    output.Append(text.Parent is { } parent && parent.Flags.HasFlag(NodeFlags.LiteralText) ? formatter.LiteralText(text) : formatter.Text(text));
                    break;
                case IComment comment:
                    output.Append(formatter.Comment(comment));
                    break;
                case IProcessingInstruction processing:
                    output.Append(formatter.Processing(processing));
                    break;
                case IDocumentType doctype:
                    output.Append(formatter.Doctype(doctype));
                    break;
            }
        }

        return output.Length > limit ? output.ToString(0, limit) : output.ToString();
    }
}
