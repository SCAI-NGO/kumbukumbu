// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { remarkFind } from "./findInNote";

type Node = { type: string; value?: string; children?: Node[] };

/** The marked texts of a paragraph holding `text`, after the plugin. */
function marked(text: string, query: string) {
  const tree: Node = { type: "root", children: [{ type: "paragraph", children: [{ type: "text", value: text }] }] };
  remarkFind({ query, current: 0 })(tree as never);
  return tree.children![0].children!.filter((c) => c.type === "findMatch").map((c) => c.children![0].value);
}

describe("remarkFind", () => {
  it("marks every match, ignoring case", () => {
    expect(marked("Road and road and ROAD", "road")).toEqual(["Road", "road", "ROAD"]);
  });

  it("marks the right text where lowercasing changes the length", () => {
    // `İ` lowercases to two code units: offsets in the lowercased text would be one off after it.
    expect(marked("İstanbul road, then Road", "road")).toEqual(["road", "Road"]);
  });
});
