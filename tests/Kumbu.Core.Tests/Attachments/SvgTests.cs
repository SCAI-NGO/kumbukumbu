// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Attachments;

public sealed class SvgTests
{
    private static VaultPath P(string value) => VaultPath.TryParse(value, out var path) ? path : throw new ArgumentException(value);

    private static string? Problem(string content) => AttachmentFormat.Problem(P("/Work/x.svg"), Encoding.UTF8.GetBytes(content));

    private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    [Fact]
    public void An_svg_is_an_image_outside_skill_folders_and_text_inside()
    {
        Assert.Equal(new AttachmentFormat(".svg", "image/svg+xml", AttachmentKind.Image), AttachmentFormat.ForPath(P("/Work/logo.SVG")));
        Assert.Contains(AttachmentFormat.Svg, AttachmentFormat.All);
        Assert.Equal(AttachmentFormat.Text, AttachmentFormat.ForPath(P("/Skills/pdf/logo.svg")));
        Assert.Equal("image", AttachmentFormat.KindOf("/Work/logo.svg"));
        Assert.Equal(AttachmentFormat.File, AttachmentFormat.ForPath(P("/Work/logo.svg.txt")));
    }

    [Theory]
    [InlineData($"<svg {Ns}/>")]
    [InlineData("<svg width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>")]                    // no namespace, as hand-written
    [InlineData($"﻿<svg {Ns}/>")]                                                                             // BOM
    [InlineData($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n<svg {Ns}/>")]
    [InlineData($"\n  <!-- made by hand -->\n<svg {Ns}/>\n<!-- the end -->\n")]
    [InlineData("<s:svg xmlns:s=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData($"<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\"><svg {Ns}/>")]
    public void Well_formed_xml_with_an_svg_root_is_accepted(string content) => Assert.Null(Problem(content));

    [Fact]
    public void A_utf16_file_with_its_bom_is_accepted() =>
        Assert.Null(AttachmentFormat.Problem(P("/Work/x.svg"), Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes($"<svg {Ns}/>")).ToArray()));

    [Theory]
    [InlineData("<html><body>hi</body></html>", "x.svg is not an SVG: the root element is html")]
    [InlineData("<svg xmlns=\"http://www.w3.org/1999/xhtml\"/>", "x.svg is not an SVG: the root element svg is in the namespace http://www.w3.org/1999/xhtml")]
    [InlineData("<x:svg xmlns:x=\"urn:x\"/>", "x.svg is not an SVG: the root element svg is in the namespace urn:x")]
    public void Another_root_is_refused(string content, string expected) => Assert.Equal(expected, Problem(content));

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData($"<svg {Ns}>")]
    [InlineData($"<svg {Ns}/><svg {Ns}/>")]
    [InlineData("\x89PNG\r\n\x1a\n")]
    public void What_isnt_well_formed_xml_is_refused(string content) =>
        Assert.StartsWith("x.svg is not an SVG: it isn't well-formed XML (", Problem(content));

    [Fact]
    public void An_external_entity_is_never_resolved()
    {
        var secret = Path.GetTempFileName();
        File.WriteAllText(secret, "kumbu-secret");
        try
        {
            var problem = Problem($"<!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file://{secret}\">]><svg {Ns}><text>&xxe;</text></svg>");
            Assert.StartsWith("x.svg is not an SVG: it isn't well-formed XML (", problem);
            Assert.DoesNotContain("kumbu-secret", problem);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void An_external_dtd_is_never_fetched()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.Null(Problem($"<!DOCTYPE svg SYSTEM \"http://127.0.0.1:{port}/svg.dtd\"><svg {Ns}/>"));
            Assert.Null(Problem($"<!DOCTYPE svg [<!ENTITY % remote SYSTEM \"http://127.0.0.1:{port}/x.dtd\"> %remote;]><svg {Ns}/>"));
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Entities_are_never_expanded()
    {
        var dtd = new StringBuilder("<!DOCTYPE svg [<!ENTITY lol \"lol\">");
        for (var i = 1; i <= 9; i++) dtd.Append($"<!ENTITY lol{i} \"{string.Concat(Enumerable.Repeat($"&lol{(i == 1 ? "" : i - 1)};", 10))}\">");
        dtd.Append("]>");
        var started = Stopwatch.GetTimestamp();
        Assert.StartsWith("x.svg is not an SVG: it isn't well-formed XML (", Problem($"{dtd}<svg {Ns}><text>&lol9;</text></svg>"));
        // Declared but not used, the entities are ignored with the rest of the DTD.
        Assert.Null(Problem($"{dtd}<svg {Ns}/>"));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("width=\"100\" height=\"50\"", 100, 50)]
    [InlineData("width=\"100px\" height=\" 50px \"", 100, 50)]
    [InlineData("width=\"10.6\" height=\"0.4\"", 11, 1)]
    [InlineData("width=\"100000\" height=\"100000\"", 100000, 100000)]                      // the header guard is for raster images
    [InlineData("viewBox=\"0 0 200 100\"", 200, 100)]
    [InlineData("viewBox=\"-5,-5, 20.5,10\"", 21, 10)]
    [InlineData("width=\"210mm\" height=\"297mm\" viewBox=\"0 0 210 297\"", 210, 297)]  // a unit other than px: the viewBox
    [InlineData("width=\"100%\" height=\"100%\" viewBox=\"0 0 40 30\"", 40, 30)]
    [InlineData("width=\"800\" viewBox=\"0 0 100 50\"", 800, 400)]                       // one side, the other by the viewBox
    [InlineData("height=\"100\" viewBox=\"0 0 100 50\"", 200, 100)]
    public void The_size_comes_from_width_and_height_else_the_viewbox(string attributes, int width, int height) =>
        Assert.Equal((width, height), SvgImage.Size(Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?>\n<svg {Ns} {attributes}><rect/></svg>")));

    [Theory]
    [InlineData("")]
    [InlineData("width=\"210mm\" height=\"297mm\"")]
    [InlineData("width=\"100\"")]
    [InlineData("width=\"0\" height=\"0\"")]
    [InlineData("width=\"-1\" height=\"5\"")]
    [InlineData("width=\"1e300\" height=\"5\"")]
    [InlineData("viewBox=\"0 0 0 10\"")]
    [InlineData("viewBox=\"0 0 10\"")]
    [InlineData("width=\"NaN\" height=\"Infinity\"")]
    public void Neither_gives_no_size(string attributes) =>
        Assert.Null(SvgImage.Size(Encoding.UTF8.GetBytes($"<svg {Ns} {attributes}/>")));

    [Fact]
    public void An_svg_image_isnt_renamed_into_a_skills_folder_where_svg_is_text()
    {
        Assert.Null(AttachmentFormat.RenameProblem(P("/Work/a.svg"), "image/svg+xml", 10, P("/Work/Icons/b.SVG")));
        Assert.Equal("a.svg is an SVG image, and inside a skill's folder an .svg is a text file; upload it there instead",
            AttachmentFormat.RenameProblem(P("/Work/a.svg"), "image/svg+xml", 10, P("/Work/_skills/x/a.svg")));
    }
}
