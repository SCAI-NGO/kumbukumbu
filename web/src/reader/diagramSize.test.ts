// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { diagramKind, diagramLimits, diagramSize, drawing, numberLimits, textLimits } from "./diagramSize";

/** A flowchart of `size` by `diagramSize`: the keyword line and chained links of two each. */
const flowchart = (size: number) => ["flowchart LR", ...Array.from({ length: size - 1 >> 1 }, (_, i) => `  n${i} --> n${i + 1}`), ...((size - 1) % 2 ? ["  x"] : [])].join("\n");

describe("diagramSize", () => {
  it("counts a flowchart's statements and links", () => {
    expect(diagramSize("flowchart LR\n  A --> B --> C\n  D")).toBe(5);
    expect(diagramSize("graph TD\n  A & B --> C")).toBe(5);
  });

  it("counts a link once for every pair of nodes it joins", () => {
    expect(diagramSize("flowchart LR\n  a & b & c --> d & e")).toBe(11);
    const n = Math.ceil(Math.sqrt(diagramLimits.flowchart.auto)) + 1;
    const nodes = (name: string) => Array.from({ length: n }, (_, i) => `${name}${i}`).join(" & ");
    const joined = `flowchart LR\n  ${nodes("a")} --> ${nodes("b")}`;
    expect(diagramSize(joined)).toBeGreaterThan(n * n);
    expect(drawing(joined)).not.toBe("itself");
  });

  it("counts a mindmap's lines, not its blank lines or comments", () => {
    expect(diagramSize("mindmap\n  root\n\n    Home\n  %% later\n    Work -- done\n")).toBe(4);
  });

  it("counts a block row's blocks and a chart's values", () => {
    expect(diagramSize("block-beta\n  columns 3\n  a b c")).toBe(6);
    expect(diagramSize("xychart-beta\n  x-axis [a, b, c]\n  bar [1, 2, 3]")).toBe(7);
    expect(diagramSize("mindmap\n  root\n    Home, Work")).toBe(3);
  });

  it("ends a line at a carriage return as Mermaid does", () => {
    expect(diagramSize("mindmap\r  root\r    Home\r\n    Work\n    Garden")).toBe(5);
  });

  it("counts what a number in a statement stands for", () => {
    expect(diagramSize("block-beta\n  space:30")).toBe(32);
    expect(diagramSize("packet-beta\n  0-15999: \"x\"\n  +3200: \"y\"")).toBe(15);
  });

  it("splits statements at semicolons", () => {
    expect(diagramSize("sequenceDiagram\n  Alice->>Bob: Hi; Bob->>Alice: Hello;")).toBe(3);
    expect(diagramSize("flowchart LR; A --> B; C")).toBe(4);
  });

  it("reads the type past front matter, and graph as a flowchart", () => {
    expect(diagramKind("---\ntitle: Work\n---\nclassDiagram\n  class Pump")).toBe("class");
    expect(diagramKind("graph TD\n  A --> B")).toBe("flowchart");
    expect(diagramKind("%% a comment\nstateDiagram-v2\n  [*] --> Home")).toBe("state");
    expect(diagramKind("flowchart-elk LR\n  A --> B")).toBe("flowchart");
    expect(diagramKind("block-beta\n  a b")).toBe("block");
    expect(diagramKind("pie\n  \"Home\" : 3")).toBe("other");
    expect(diagramKind("")).toBe("other");
  });

  it("reads the type past a directive over several lines and past front matter as Mermaid closes it", () => {
    expect(diagramKind("%%{init: {\nsequenceDiagram\n}}%%\nmindmap\n  root")).toBe("mindmap");
    expect(diagramKind("---\ntitle: Work\n ---\nsequenceDiagram: x\n---\nmindmap\n  root")).toBe("mindmap");
    expect(diagramKind("  ---\n  title: Work\n  ---\nclassDiagram\n  class Pump")).toBe("class");
    expect(diagramKind("%%{init: {\nclassDiagram")).toBe("other");
  });

  it("a diagram with a directive or with config in its front matter takes the other types' limits", () => {
    const size = diagramLimits.other.auto + 1;
    expect(drawing(flowchart(size))).toBe("itself");
    expect(drawing(`%%{init: {"theme": "forest"}}%%\n${flowchart(size)}`)).toBe("asked");
    expect(drawing(`---\nconfig:\n  theme: forest\n---\n${flowchart(size)}`)).toBe("asked");
    expect(drawing(`---\ntitle: Work\n---\n${flowchart(size)}`)).toBe("itself");
  });

  it("a short diagram with a long label or a long text waits, and the longest are never drawn", () => {
    const label = (text: string) => `flowchart LR\n  A["${text}"] --> B`;
    const words = (chars: number) => Array.from({ length: Math.ceil(chars / 5) }, () => "word").join(" ");
    expect(drawing(label("x".repeat(textLimits.word.auto - 10)))).toBe("itself");
    expect(drawing(label("x".repeat(textLimits.word.auto + 1)))).toBe("asked");
    expect(drawing(label("x".repeat(textLimits.word.cap + 1)))).toBe("never");
    expect(drawing(label(words(textLimits.chars.auto)))).toBe("asked");
    expect(drawing(label(words(textLimits.chars.cap)))).toBe("never");
  });

  it("a word runs to a plain space, whatever other blank it holds", () => {
    const joined = (blank: string) => `mindmap\n  root\n    ${Array.from({ length: textLimits.word.cap / 2 + 1 }, () => "x").join(blank)}`;
    expect(drawing(joined("\u00a0"))).toBe("never");
    expect(drawing(joined("\t"))).toBe("never");
    expect(drawing(joined(" "))).toBe("asked");
  });

  it("a block of many spaces waits, and the largest are never drawn", () => {
    const { auto, cap } = diagramLimits.block;
    expect(drawing(`block-beta\n  space:${auto - 2}`)).toBe("itself");
    expect(drawing(`block-beta\n  space:${auto}`)).toBe("asked");
    expect(drawing(`block-beta\n  space:${cap}`)).toBe("never");
  });

  it("a gantt with excluded days is never drawn here, one without draws as before", () => {
    const tasks = Array.from({ length: 30 }, (_, i) => (i % 2 ? `  t${i} : after t${i - 1}, 3w` : `  t${i} : 2026-${String((i % 12) + 1).padStart(2, "0")}-01, 2027-03-31`)).join("\n");
    const project = (excludes: string) => `gantt\n  dateFormat YYYY-MM-DD\n${excludes}${tasks}`;
    expect(drawing(project(""))).toBe("itself");
    expect(drawing(project("  excludes weekends\n"))).toBe("never");
    expect(drawing(project("  EXCLUDES weekends\n"))).toBe("never");
    expect(drawing(project("  inclusiveEndDates excludes weekends\n"))).toBe("never");
  });

  it("a large number makes a diagram wait, a very large one is never drawn, except as a chart's value or quoted", () => {
    const flowchart = (n: number) => `flowchart LR\n  A[Batch ${n}] --> B`;
    expect(drawing(flowchart(numberLimits.auto))).toBe("itself");
    expect(drawing(flowchart(numberLimits.auto + 1))).toBe("asked");
    expect(drawing(flowchart(numberLimits.cap + 1))).toBe("never");
    expect(drawing(`flowchart LR\n  A["Batch ${numberLimits.cap + 1}"] --> B`)).toBe("itself");
    expect(drawing(`flowchart LR\n  n${numberLimits.cap + 1} --> B\n  style B fill:#999999`)).toBe("itself");
    expect(drawing(`pie\n  "Home" : ${numberLimits.cap + 1}\n  "Work" : 3`)).toBe("itself");
    expect(drawing("timeline\n  2026-11-15 : Release")).toBe("itself");
  });

  it("a timeline holds fewer characters", () => {
    const { auto, cap } = diagramLimits.timeline.chars;
    const timeline = (chars: number) => `timeline\n  2026 : ${Array.from({ length: Math.ceil(chars / 5) }, () => "word").join(" ")}`;
    expect(drawing(timeline(auto - 30))).toBe("itself");
    expect(drawing(timeline(auto))).toBe("asked");
    expect(drawing(timeline(cap))).toBe("never");
  });

  it("a small diagram draws by itself, a larger one when asked, the largest never", () => {
    const { auto, cap } = diagramLimits.flowchart;
    for (const size of [auto, auto + 1, cap, cap + 1]) expect(diagramSize(flowchart(size))).toBe(size);
    expect(drawing(flowchart(auto))).toBe("itself");
    expect(drawing(flowchart(auto + 1))).toBe("asked");
    expect(drawing(flowchart(cap))).toBe("asked");
    expect(drawing(flowchart(cap + 1))).toBe("never");
  });
});
