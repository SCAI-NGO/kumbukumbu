// SPDX-License-Identifier: AGPL-3.0-or-later
// GET /api/links, answered from the mirror; and the helpers the local index shares with it.
import { expect, test } from "vitest";
import { inArchive } from "../src/links/archive";
import { isFolderMarker, kindOf, mediaTypeOf } from "../src/links/formats";
import { textOrder } from "../src/links/order";
import { LinkResolver } from "../src/links/resolve";
import { linkViews } from "../src/links/views";

test("a note's links, as the server answers them", () => {
  const resolver = new LinkResolver("W", ["/W/inbox.md", "/W/Zeta/plan.md", "/W/Über/plan.md", "/W/apple/plan.md"], ["/W/Screens/shot.png"]);
  expect(linkViews(resolver, "/W/inbox.md", "# Inbox\n![[shot.png]] ![s](Screens/shot.png) [[gone.png]]\n[[plan]] [[#Top]]\n`[[code]]`\n")).toEqual([
    { line: 2, raw: "![[shot.png]]", target: "shot.png", kind: "attachment", path: "/W/Screens/shot.png", candidates: [] },
    { line: 2, raw: "![s](Screens/shot.png)", target: "Screens/shot.png", kind: "attachment", path: "/W/Screens/shot.png", candidates: [] },
    { line: 2, raw: "[[gone.png]]", target: "gone.png", kind: "not_found", path: null, candidates: [] },
    // Ambiguous candidates sorted as people expect: Ü next to U, lower case before upper.
    { line: 3, raw: "[[plan]]", target: "plan", kind: "ambiguous", path: null, candidates: ["/W/apple/plan.md", "/W/Über/plan.md", "/W/Zeta/plan.md"] },
    { line: 3, raw: "[[#Top]]", target: "", kind: "self", path: "/W/inbox.md", candidates: [] },
  ]);
});

test("text order: the root collation, ties broken by code unit", () => {
  expect(["b", "Ü", "a", "V", "u", "U"].sort(textOrder)).toEqual(["a", "b", "u", "U", "Ü", "V"]);
  expect(textOrder("Ü", "Ü".normalize("NFD"))).not.toBe(0);
});

test("the archive is _archive in a vault's root", () => {
  expect(inArchive("/W/_archive")).toBe(true);
  expect(inArchive("/W/_archive/a/b.md")).toBe(true);
  expect(inArchive("/W/Inbox/_archive/b.md")).toBe(false);
  expect(inArchive("/W/_Archive/b.md")).toBe(false);
});

test("kinds and media types by name", () => {
  expect(kindOf("/W/a.md")).toBe("note");
  expect(kindOf("/W/a.MD")).toBe("file");
  expect(kindOf("/W/shot.PNG")).toBe("image");
  expect(kindOf("/W/logo.svg")).toBe("image");
  expect(kindOf("/W/report.pdf")).toBe("pdf");
  expect(kindOf("/W/Drafts/.vaultkeep")).toBe("folder");
  expect(kindOf("/W/data.csv")).toBe("file");
  expect(kindOf("/Skills/pdf/run.py")).toBe("text");
  expect(kindOf("/Skills/pdf/logo.svg")).toBe("text");
  expect(kindOf("/Skills/run.py")).toBe("file");
  expect(kindOf("/W/_skills/pdf/LICENSE")).toBe("text");
  expect(kindOf("/Skills/pdf/run.py", null)).toBe("file");
  expect(mediaTypeOf("/W/a.md")).toBe("text/markdown");
  expect(mediaTypeOf("/W/a.jpeg")).toBe("image/jpeg");
  expect(mediaTypeOf("/W/a.webp")).toBe("image/webp");
  expect(mediaTypeOf("/W/a.docx")).toBe("application/octet-stream");
  expect(mediaTypeOf("/Skills/pdf/run.py")).toBe("text/plain; charset=utf-8");
  expect(isFolderMarker("/W/Drafts/.vaultkeep")).toBe(true);
  expect(isFolderMarker("/W/Drafts/x.vaultkeep")).toBe(false);
});
