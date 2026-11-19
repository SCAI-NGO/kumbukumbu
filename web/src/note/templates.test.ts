// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { fillPlaceholders, formatMoment, templateEdit, templatePaths } from "./templates";

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

  it("takes an empty format for the default", () => {
    expect(fillPlaceholders("{{date:}} {{time:}}", at)).toBe("2026-11-19 14:05");
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

  it.each([
    [1, "1st"], [2, "2nd"], [3, "3rd"], [4, "4th"], [11, "11th"], [12, "12th"], [13, "13th"], [21, "21st"], [22, "22nd"], [23, "23rd"], [31, "31st"],
  ])("Do gives the day %i as %s", (day, expected) => {
    const month = day > 28 ? "01" : "03";
    expect(formatMoment(new Date(`2026-${month}-${String(day).padStart(2, "0")}T10:00:00Z`), "Europe/Vienna", "Do")).toBe(expected);
  });

  it("fills Obsidian's dddd, MMMM Do YYYY whole", () => {
    expect(formatMoment(saturday, "Europe/Vienna", "dddd, MMMM Do YYYY")).toBe("Saturday, March 7th 2026");
  });

  it.each([["DDDD"], ["DDD"], ["YYYYY"], ["Wo"], ["Qo"], ["dd"], ["hh:mm A"]])("leaves a token it doesn't fill, %s, as written", (format) => {
    const out = formatMoment(saturday, "Europe/Vienna", format);
    expect(out).toBe(format === "hh:mm A" ? "hh:04 A" : format);
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

  it.each([
    ["---\ntags: [x]\n---", "---\ntags: [x]\n---\n## Agenda\n"],
    ["---\n---", "---\n---\n## Agenda\n"],
    ["---\r\ntags: [x]\r\n---", "---\r\ntags: [x]\r\n---\r\n## Agenda\r\n"],
  ])("starts the body on a line of its own after a closing --- without a line break, %j", (note, expected) => {
    const { text, caret } = inserted(note, "## Agenda\n");
    expect(text).toBe(expected);
    expect(caret).toEqual([text.length, text.length]);
  });

  it("starts the body on a line of its own also when the template adds properties", () => {
    const { text, caret } = inserted("---\ntags: [x]\n---", "---\ntype: meeting\n---\n## Agenda\n");
    expect(text).toBe("---\ntags: [x]\ntype: meeting\n---\n## Agenda\n");
    expect(caret[0]).toBe(text.length);
  });

  it("writes the body with the note's CRLF line endings", () => {
    const note = "---\r\nstatus: open\r\n---\r\n# Notes\r\n";
    const { text, caret } = inserted(note, "---\ntype: meeting\n---\n## Agenda\n\n- one\n");
    expect(text).toBe("---\r\nstatus: open\r\ntype: meeting\r\n---\r\n# Notes\r\n## Agenda\r\n\r\n- one\r\n");
    expect(caret[0]).toBe(text.length);
  });

  it.each([
    ["title: '{{title}}'", "Bob's plan", "title: \"Bob's plan\""],
    ["title: {{title}}", "Meeting: Q3", 'title: "Meeting: Q3"'],
    ["title: {{title}}", "@home", 'title: "@home"'],
    ['title: "{{title}}"', 'Say "hi"', 'title: "Say \\"hi\\""'],
    ["title: {{title}}", "Müller", 'title: "Müller"'],
  ])("writes %s for the title %j as valid YAML", (line, title, written) => {
    const { edit } = templateEdit("", 0, 0, `---\n${line}\n---\n`, { ...at, title });
    expect(edit.text).toBe(`---\n${written}\n---\n`);
  });

  it("keeps a date or a number as the template writes it", () => {
    const { edit } = templateEdit("", 0, 0, "---\ncreated: {{date}}\nyear: {{date:YYYY}}\nat: '{{time}}'\n---\n", at);
    expect(edit.text).toBe("---\ncreated: 2026-11-19\nyear: 2026\nat: '14:05'\n---\n");
  });

  it("keeps a key the note writes in quotes, adding no second one", () => {
    const note = '---\n"status": a\n---\n';
    const { text, hint } = inserted(note, "---\nstatus: b\n---\nbody");
    expect(text).toBe(`${note}body`);
    expect(hint).toBeNull();
  });

  it("inserts only the body, with its own hint, where the properties can't be merged safely", () => {
    const note = "---\nstatus: open\n---\n";
    const { text, hint } = inserted(note, "---\ntype: a\ntype: b\n---\nbody");
    expect(text).toBe(`${note}body`);
    expect(hint).toBe("The template's properties weren't added: they couldn't be merged safely into this note's.");
  });
});

describe("templatePaths", () => {
  const notes = (folder: string, names: string[]) => names.map((n) => ({ name: n, path: `${folder}/${n}`, size: 1, kind: "note", mediaType: "text/markdown" }));
  const root = {
    name: "Work",
    path: "/Work",
    notes: [],
    folders: [{ name: "Templates", path: "/Work/Templates", notes: notes("/Work/Templates", ["Meeting.md", "Review.md"]), folders: [] }],
  };

  it("lists the notes under Templates, not the note being written", () => {
    expect(templatePaths(root, "Work", "/Work/projects/plan.md")).toEqual(["/Work/Templates/Meeting.md", "/Work/Templates/Review.md"]);
    expect(templatePaths(root, "Work", "/Work/Templates/Meeting.md")).toEqual(["/Work/Templates/Review.md"]);
  });
});
