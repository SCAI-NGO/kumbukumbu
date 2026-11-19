// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { fillPlaceholders, formatMoment, templateEdit } from "./templates";

// Thursday 2026-11-19, 14:05:09 in Vienna (UTC+1).
const thursday = new Date("2026-11-19T13:05:09Z");
const at = { title: "Weekly", now: thursday, zone: "Europe/Vienna" };

describe("fillPlaceholders", () => {
  it("puts in the note's title, the date and the time", () => {
    expect(fillPlaceholders("# {{title}}\n{{date}} {{time}}", at)).toBe("# Weekly\n2026-11-19 14:05");
  });

  it("takes a Moment format after the colon", () => {
    expect(fillPlaceholders("{{date:dddd, D MMMM YYYY}} at {{time:HH:mm:ss}}", at)).toBe("Thursday, 19 November 2026 at 14:05:09");
  });

  it("goes by the zone it is given: near midnight it is another day elsewhere", () => {
    const late = new Date("2026-11-19T23:30:00Z");
    expect(fillPlaceholders("{{date}} {{time}}", { ...at, now: late, zone: "Europe/Vienna" })).toBe("2026-11-20 00:30");
    expect(fillPlaceholders("{{date}} {{time}}", { ...at, now: late, zone: "America/New_York" })).toBe("2026-11-19 18:30");
    expect(fillPlaceholders("{{date:ddd}}", { ...at, now: late, zone: "Pacific/Kiritimati" })).toBe("Fri");
  });

  it("falls back to the browser's zone for one it doesn't know", () => {
    const browser = fillPlaceholders("{{date}} {{time}}", { ...at, zone: Intl.DateTimeFormat().resolvedOptions().timeZone });
    expect(fillPlaceholders("{{date}} {{time}}", { ...at, zone: "Nowhere/Atlantis" })).toBe(browser);
  });

  it.each([
    ["{{other}}"],
    ["{{title:upper}}"],
    ["{{ date }}"],
    ["{{Date}}"],
    ["<% tp.date.now() %>"],
    ["<%* tR += tp.file.title %>"],
    ["{{date"],
  ])("leaves %s as written", (text) => {
    expect(fillPlaceholders(text, at)).toBe(text);
  });

  it("fills every placeholder of a text, also several on a line", () => {
    expect(fillPlaceholders("{{date:YYYY}}-{{date:MM}} {{title}} {{title}}", at)).toBe("2026-11 Weekly Weekly");
  });
});

describe("formatMoment", () => {
  // Saturday 2026-03-07, 09:04:03 in Vienna: single digits show what M, D and H leave out.
  const saturday = new Date("2026-03-07T08:04:03Z");
  it.each([
    ["YYYY", "2026"],
    ["YY", "26"],
    ["MMMM", "March"],
    ["MMM", "Mar"],
    ["MM", "03"],
    ["M", "3"],
    ["DD", "07"],
    ["D", "7"],
    ["dddd", "Saturday"],
    ["ddd", "Sat"],
    ["HH", "09"],
    ["H", "9"],
    ["mm", "04"],
    ["ss", "03"],
  ])("%s gives %s", (format, expected) => {
    expect(formatMoment(saturday, "Europe/Vienna", format)).toBe(expected);
  });

  it("reads the longest token first, and leaves the text between them", () => {
    expect(formatMoment(saturday, "Europe/Vienna", "MMMM/MMM/MM/M")).toBe("March/Mar/03/3");
    expect(formatMoment(saturday, "Europe/Vienna", "YYYYMMDD-HHmm")).toBe("20260307-0904");
  });

  it("keeps what is in square brackets as written, as Moment does", () => {
    expect(formatMoment(saturday, "Europe/Vienna", "[Week of] D MMM [at] HH[h]")).toBe("Week of 7 Mar at 09h");
  });

  it("shows midnight as 00, not 24", () => {
    expect(formatMoment(new Date("2026-03-06T23:00:00Z"), "Europe/Vienna", "HH:mm H")).toBe("00:00 0");
  });

  it("names days and months in English whatever the browser's language", () => {
    expect(formatMoment(new Date("2026-09-01T10:00:00Z"), "Europe/Vienna", "ddd dddd MMM MMMM")).toBe("Tue Tuesday Sep September");
  });
});

/** The note's text after the edit, and where the caret is. */
function inserted(note: string, template: string, start = note.length, end = start) {
  const { edit, hint } = templateEdit(note, start, end, template, at);
  const text = note.slice(0, edit.start) + edit.text + note.slice(edit.end);
  return { text, caret: [edit.selStart, edit.selEnd], hint, edit };
}

describe("templateEdit", () => {
  it("puts the body at the caret, replacing a selection, the caret after it", () => {
    const { text, caret, hint } = inserted("# Notes\n\nold\n", "## {{title}} on {{date}}\n", 9, 12);
    expect(text).toBe("# Notes\n\n## Weekly on 2026-11-19\n\n");
    expect(caret).toEqual([9 + "## Weekly on 2026-11-19\n".length, 9 + "## Weekly on 2026-11-19\n".length]);
    expect(hint).toBeNull();
  });

  it("adds the keys the note lacks and keeps a scalar it has", () => {
    const note = "---\nstatus: open\n---\n# Notes\n";
    const { text } = inserted(note, "---\nstatus: draft\ntype: meeting\n---\n## Agenda\n");
    expect(text).toBe("---\nstatus: open\ntype: meeting\n---\n# Notes\n## Agenda\n");
  });

  it("unites lists in the note's order, appending what is new, inline and below the key", () => {
    const note = "---\ntags: [work, b]\naliases:\n  - Weekly\n---\n";
    const { text } = inserted(note, "---\ntags:\n  - b\n  - meeting\naliases: [Weekly, Jour fixe]\n---\n");
    expect(text).toBe("---\ntags: [work, b, meeting]\naliases:\n  - Weekly\n  - Jour fixe\n---\n");
  });

  it("unites any list, and takes a scalar of the template into the note's list", () => {
    const note = "---\nattendees: [Ada]\nprojects: []\n---\n";
    const { text } = inserted(note, "---\nattendees: [Ada, Liz]\nprojects: Kumbu\n---\n");
    expect(text).toBe("---\nattendees: [Ada, Liz]\nprojects: [Kumbu]\n---\n");
  });

  it("gives a key without a value the template's list", () => {
    const { text } = inserted("---\ntags:\n---\n", "---\ntags: [meeting]\n---\n");
    expect(text).toBe("---\ntags:\n  - meeting\n---\n");
  });

  it("writes a list below its key where a new item wouldn't read inline", () => {
    const { text } = inserted("---\ntags: [work]\n---\n", '---\ntags:\n  - "#x"\n---\n');
    expect(text).toBe('---\ntags:\n  - work\n  - "#x"\n---\n');
  });

  it("gives a note without properties the template's, at the top", () => {
    const { text, caret } = inserted("# Notes\n", "---\ntags: [meeting]\ncreated: {{date}}\n---\n## Agenda\n");
    expect(text).toBe("---\ntags:\n  - meeting\ncreated: 2026-11-19\n---\n# Notes\n## Agenda\n");
    expect(caret[0]).toBe(text.length);
  });

  it("fills placeholders in values, keeping how the template writes them where they still read", () => {
    const { text } = inserted("---\nstatus: open\n---\n", '---\nwhen: {{date}} {{time}}\ntopic: "{{title}}: notes"\nday: {{date:dddd}}\nkind: {{title}}\n---\n');
    expect(text).toBe('---\nstatus: open\nwhen: 2026-11-19 14:05\ntopic: "Weekly: notes"\nday: Thursday\nkind: Weekly\n---\n');
  });

  it("quotes a filled value that wouldn't read back as written", () => {
    const { edit } = templateEdit("", 0, 0, "---\nsubject: {{title}}\n---\n", { ...at, title: "a # b" });
    expect(edit.text).toBe('---\nsubject: "a # b"\n---\n');
  });

  it("inserts only the body, with a hint, where the template's properties can't be read", () => {
    const { text, hint } = inserted("---\nstatus: open\n---\n", "---\nlabel: x # a comment\n---\n## Agenda\n");
    expect(text).toBe("---\nstatus: open\n---\n## Agenda\n");
    expect(hint).toBe("The template's properties weren't added: the template's properties can't be read.");
  });

  it("inserts only the body, with a hint, where the note's properties can't be read", () => {
    const note = "---\nnested:\n  key: v\n---\n# Notes\n";
    const { text, hint } = inserted(note, "---\ntags: [meeting]\n---\n## Agenda\n");
    expect(text).toBe(`${note}## Agenda\n`);
    expect(hint).toBe("The template's properties weren't added: this note's properties can't be read.");
  });

  it("puts the body below the properties when the caret is in them", () => {
    const note = "---\nstatus: open\n---\n# Notes\n";
    const { text } = inserted(note, "---\ntype: meeting\n---\n## Agenda\n", 6, 8);
    expect(text).toBe("---\nstatus: open\ntype: meeting\n---\n## Agenda\n# Notes\n");
  });

  it("changes nothing above the caret for a template without properties: one edit of the selection", () => {
    const note = "---\nstatus: open\n---\n# Notes\n";
    const { edit } = inserted(note, "## Agenda\n");
    expect(edit).toMatchObject({ start: note.length, end: note.length, text: "## Agenda\n" });
  });

  it("is one edit from the first change in the properties to the caret", () => {
    const note = "---\nstatus: open\n---\n# Notes\n\nend";
    const { edit } = inserted(note, "---\ntype: meeting\n---\nbody", note.indexOf("end"));
    expect(edit.start).toBe("---\nstatus: open\n".length);
    expect(edit.end).toBe(note.indexOf("end"));
  });

  it("keeps a note's BOM and CRLF line endings", () => {
    const { text } = inserted("﻿---\r\nstatus: open\r\n---\r\n", "---\ntype: meeting\n---\n");
    expect(text).toBe("﻿---\r\nstatus: open\r\ntype: meeting\r\n---\r\n");
  });
});
