// SPDX-License-Identifier: AGPL-3.0-or-later
import { render } from "@testing-library/react";
import { createElement } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { describe, expect, it } from "vitest";
import { remarkHideBlockIds } from "./blockIds";

const shown = (markdown: string) => render(createElement(ReactMarkdown, { remarkPlugins: [remarkGfm, remarkHideBlockIds] }, markdown)).container;

describe("remarkHideBlockIds", () => {
  it("hides a paragraph's id", () => {
    const view = shown("The pump needs a new controller. ^controller");
    expect(view.querySelector("p")?.textContent).toBe("The pump needs a new controller.");
  });

  it("hides a list item's id", () => {
    const view = shown("- a ^x\n- b");
    expect(view.querySelector("li")?.textContent).toBe("a");
    expect(view.textContent).not.toContain("^x");
  });

  it("hides an id on its own line", () => {
    const view = shown("- a\n\n^list");
    expect(view.textContent).not.toContain("^list");
    expect(view.querySelectorAll("p")).toHaveLength(0);
  });

  it("hides an id on the line below a paragraph", () => {
    expect(shown("text\n^p").querySelector("p")?.textContent).toBe("text");
  });

  it("hides an id on the line below a hard break", () => {
    for (const markdown of ["text\\\n^id", "text  \n^id"]) {
      const p = shown(markdown).querySelector("p");
      expect(p?.textContent).toBe("text");
      expect(p?.querySelector("br")).toBeNull();
    }
  });

  it("hides a quote's id", () => {
    expect(shown("> The pump\n> needs a controller. ^quote").querySelector("blockquote")?.textContent?.trim()).toBe("The pump\nneeds a controller.");
  });

  it("keeps code as it is", () => {
    expect(shown("`x ^y`").querySelector("code")?.textContent).toBe("x ^y");
    expect(shown("```\nx ^y\n```").querySelector("pre")?.textContent).toBe("x ^y\n");
  });

  it("hides a heading's id", () => {
    expect(shown("## Decision ^d").querySelector("h2")?.textContent).toBe("Decision");
  });

  it("keeps a row below a table, which names nothing", () => {
    expect(shown("| a |\n|---|\n| 1 |\n^tab").querySelector("tbody")?.textContent).toContain("^tab");
  });

  it("keeps a caret without a space before it", () => {
    expect(shown("word^x").textContent).toBe("word^x");
  });
});
