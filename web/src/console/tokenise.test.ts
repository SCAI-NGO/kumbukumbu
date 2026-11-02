// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import fixture from "./console-fixture.json";
import { joinLine, quote, tokenise } from "./tokenise";

// The same file is read by ConsoleFixtureTests on the server, against Pipeline.TryParse.
describe("tokenise", () => {
  it.each(fixture.lines)("reads $line as the shell does", (entry) => {
    expect(tokenise(entry.line)).toEqual("stages" in entry ? entry.stages : null);
  });
});

describe("quote", () => {
  it.each(["plain", "a b", "it's", "it's \"x\" \\ y", 'say "x"', "back\\slash", "", "a|b", "Bewässerung", "-n", "/x/y.md#Goals", "$HOME", "a;b"])(
    "reads back %j",
    (word) => {
      expect(tokenise(quote(word))).toEqual([[word]]);
    },
  );

  it("leaves plain words alone", () => {
    expect(quote("Bewässerung")).toBe("Bewässerung");
    expect(quote("/x/y.md#Goals")).toBe("/x/y.md#Goals");
  });

  it("joins stages so that they tokenise back", () => {
    const stages = [["rg", "a|b"], ["head"]];
    expect(tokenise(joinLine(stages))).toEqual(stages);
  });
});
