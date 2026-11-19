// SPDX-License-Identifier: AGPL-3.0-or-later
// The shell's new options of 0.6.0, through MCP: `ls -l`/`-a` (PR #233), `calendar --from/--to` (PR #243) and
// `rg --regions` with `match=` (PR #245). No feature spec of its own: the changelog and the PR descriptions say what is
// promised (spec §2), `help ls`, `help calendar` and `help rg` the exact forms.
//
// Promises in scope (PR → test):
//  #233 ls -l adds what each entry weighs and when it last changed (UTC, as history shows it): a note its size, a file
//       its summary, a folder the newest change below it; columns aligned          → "ls -l: sizes, summaries and times …"
//       (all writes of a test fall within a minute or two, and the time has minutes only, so "the newest below it" is
//       checked as the folder's time equal to the newest time `ls -l` shows inside it, not with two stamps that differ)
//  #233 ls -a / --archive lists the vault's _archive/ too; -la and -al combine     → "ls -a and --archive: the vault's archive …"
//  #243 calendar <vault> --from D --to D: those days as weeks, each week cut to the range, only the days with entries,
//       both ends included                                                         → "calendar --from/--to: the days of a range …"
//       both options needed, at most 366 days, not with --week or --month          → "calendar --from/--to: refuses …"
//       --tag narrows it: every calendar call here carries a random tag of the test's own, as calendar takes the whole
//       vault and other tests write into Work at the same time
//  #245 rg --regions: crop= around the piece of text, :match= with the match's own box where it is narrower, crop= alone
//       where they are the same; not with -l and the like                          → "rg --regions: match= …"
// Left out:
//  #233 the console's l, la, lsa and peek: the web console, @console (browser).
//  #245 a PDF crop within a photo at the photo's resolution, and img's scaling factor: they need a PDF with an embedded
//       photo and a picture large enough to be reduced, and the plan scopes @shell's #245 to rg --regions.
import { randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { encodePath } from "../support/api";
import { fixturesDir } from "../support/env";
import { expect, test as base } from "./fixtures";

const test = base.extend<{ put: (path: string, content: string | Buffer) => Promise<string> }>({
  // Writes a note (`.md`) or a file and removes it at the test's end, whatever happened; every path is tried.
  put: async ({ aliceApi }, use) => {
    const written: string[] = [];
    await use(async (path, content) => {
      written.push(path);
      if (path.endsWith(".md")) await aliceApi.putNote(path, content.toString());
      else await aliceApi.putFile(path, typeof content === "string" ? Buffer.from(content, "utf8") : content);
      return path;
    });
    const failures: string[] = [];
    for (const path of written.reverse()) {
      const response = await aliceApi.send("DELETE", `/api/${path.endsWith(".md") ? "notes" : "files"}/${encodePath(path)}`);
      if (!response.ok() && response.status() !== 404) failures.push(`DELETE ${path}: ${response.status()}`);
    }
    if (failures.length) throw new Error(failures.join("; "));
  },
});

/** `ls`'s and `history`'s time: minutes, UTC. */
const stamp = /\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC/;
const minute = (at: Date) => at.toISOString().slice(0, 16).replace("T", " ") + " UTC";

test("ls -l: sizes, summaries and times, a folder's the newest below it, in aligned columns @shell", async ({ mcp, put, folder }) => {
  const dir = `/Work/${folder}`;
  const from = minute(new Date());
  await put(`${dir}/a.md`, "# A\n\nhello\n"); // 11 bytes
  await put(`${dir}/c.txt`, "hello"); // a text file of 5 bytes
  await put(`${dir}/sub/b.md`, "# B\n");
  await put(`${dir}/sub/deep/d.md`, "# D\n\nlast\n");

  const plain = await mcp.sh(`ls ${dir}`);
  expect(plain).toEqual({ text: "a.md\nc.txt  text 5 B\nsub/", isError: false });

  const long = await mcp.sh(`ls -l ${dir}`);
  expect(long.isError).toBe(false);
  const lines = long.text.split("\n");
  // Name, then size or summary, then the time: each column as wide as its widest entry ("c.txt", "text 5 B").
  expect(lines.map((l) => l.replace(stamp, "<time>"))).toEqual([
    "a.md   11 B      <time>",
    "c.txt  text 5 B  <time>",
    "sub/             <time>",
  ]);
  const times = lines.map((l) => l.match(stamp)![0]);
  for (const time of times) {
    expect(time >= from && time <= minute(new Date()), `${time} lies between ${from} and now`).toBe(true);
  }

  // A note's time is its last change, as history shows it.
  const history = await mcp.sh(`history ${dir}/a.md`);
  expect(history.text.split("\n")[0]).toMatch(new RegExp(`^[0-9a-f]{12}  ${times[0]}  alice  3 lines  \\(current\\)$`));

  // The folder's time is the newest of what lies below it, deeper folders included.
  const sub = await mcp.sh(`ls -l ${dir}/sub`);
  expect(sub.text.split("\n").map((l) => l.replace(stamp, "<time>"))).toEqual(["b.md   4 B  <time>", "deep/       <time>"]);
  const deep = await mcp.sh(`ls -l ${dir}/sub/deep`);
  expect(deep.text.replace(stamp, "<time>")).toBe("d.md  10 B  <time>");
  const below = [...sub.text.matchAll(new RegExp(stamp, "g")), ...deep.text.matchAll(new RegExp(stamp, "g"))].map((m) => m[0]).sort();
  expect(times[2], "sub/'s time is the newest below it").toBe(below.at(-1));

  // A single note with -l: its path, size and time.
  const one = await mcp.sh(`ls -l ${dir}/a.md`);
  expect(one.text).toBe(`${dir}/a.md  11 B  ${times[0]}`);
});

test("ls -a and --archive: the vault's archive is listed only when asked, -la and -al combine @shell", async ({ mcp, put, folder }) => {
  // A note of this test's own in Work's archive, so the archive exists whatever else the vault holds.
  await put(`/Work/_archive/${folder}/old.md`, "# Old\n");
  await put(`/Work/${folder}/now.md`, "# Now\n");

  const listed = async (command: string) => {
    const answer = await mcp.sh(command);
    expect(answer.isError, command).toBe(false);
    return answer.text.split("\n");
  };

  const plain = await listed("ls /Work");
  expect(plain).toContain(`${folder}/`);
  expect(plain, "ls leaves out the archive").not.toContain("_archive/");

  for (const command of ["ls -a /Work", "ls --archive /Work"]) {
    const lines = await listed(command);
    expect(lines, command).toContain("_archive/");
    expect(lines, command).toContain(`${folder}/`);
  }

  for (const command of ["ls -la /Work", "ls -al /Work"]) {
    const lines = await listed(command);
    // A folder's line: its name, an empty size column, the newest change below it.
    expect(lines.find((l) => l.startsWith("_archive/")), command).toMatch(new RegExp(`^_archive/ +${stamp.source}$`));
    expect(lines.find((l) => l.startsWith(`${folder}/`)), command).toMatch(new RegExp(`^${folder}/ +${stamp.source}$`));
  }

  // The archive's own listing shows what it holds without -a.
  expect(await listed("ls /Work/_archive")).toContain(`${folder}/`);
  expect(await mcp.sh(`ls /Work/_archive/${folder}`)).toEqual({ text: "old.md", isError: false });
});

test("calendar --from/--to: the days of a range as their weeks, only days with entries, both ends included @shell", async ({ mcp, put, folder }) => {
  const dir = `/Work/${folder}`;
  const tag = `e2e-shell-${randomBytes(4).toString("hex")}`;
  const entry = (when: string) => `---\nwhen: ${when}\ntags: [${tag}]\n---\n# Entry\n`;
  // 2030, far enough ahead that no entry is upcoming, ongoing or overdue on the day the suite runs.
  await put(`${dir}/before.md`, entry("2030-02-05"));
  await put(`${dir}/first.md`, entry("2030-02-06"));
  await put(`${dir}/last.md`, entry("2030-02-13T14:00"));
  await put(`${dir}/after.md`, entry("2030-02-14"));
  await put(`${dir}/later.md`, entry("2030-12-31"));

  // Wednesday 6 to Wednesday 13 February 2030: weeks 6 and 7, each cut to the range; the days in between have none.
  expect(await mcp.sh(`calendar /Work --from 2030-02-06 --to 2030-02-13 --tag ${tag}`)).toEqual({
    text: [
      "6–13 February 2030 · /Work",
      "Week 6 · 6–10 Feb",
      "  Wed 6 Feb",
      `    first  ${dir}/first.md`,
      "Week 7 · 11–13 Feb",
      "  Wed 13 Feb",
      `    14:00  last  ${dir}/last.md`,
    ].join("\n"),
    isError: false,
  });

  // 366 days, the most a range may have: a whole year and one day, each of its weeks, the entries in theirs.
  const year = await mcp.sh(`calendar /Work --from 2030-01-01 --to 2031-01-01 --tag ${tag}`);
  expect(year.isError).toBe(false);
  const lines = year.text.split("\n");
  expect(lines[0]).toBe("1 January 2030 – 1 January 2031 · /Work");
  expect(lines[1]).toBe("Week 1 · 1–6 Jan");
  expect(year.text).toContain(["Week 6 · 4–10 Feb", "  Tue 5 Feb", `    before  ${dir}/before.md`, "  Wed 6 Feb", `    first  ${dir}/first.md`, "Week 7 · 11–17 Feb", "  Wed 13 Feb", `    14:00  last  ${dir}/last.md`, "  Thu 14 Feb", `    after  ${dir}/after.md`, "Week 8 · "].join("\n"));
  // The last week, cut to the range's last day, holds the year's last entry.
  expect(year.text.endsWith(`\nWeek 1 · 30 Dec – 1 Jan\n  Tue 31 Dec\n    later  ${dir}/later.md`)).toBe(true);
  expect(lines.filter((l) => / {4}.*\/Work\//.test(l))).toHaveLength(5);

  // A range without entries still names its weeks.
  expect(await mcp.sh(`calendar /Work --from 2030-03-04 --to 2030-03-05 --tag ${tag}`)).toEqual({
    text: "4–5 March 2030 · /Work\nWeek 10 · 4–5 Mar\n  nothing yet",
    isError: false,
  });
});

test("calendar --from/--to: refuses a range over 366 days, one end alone, and --week or --month beside it @shell", async ({ mcp, put, folder }) => {
  const tag = `e2e-shell-${randomBytes(4).toString("hex")}`;
  await put(`/Work/${folder}/entry.md`, `---\nwhen: 2030-02-06\ntags: [${tag}]\n---\n# Entry\n`);
  const refusals: [string, string][] = [
    [`--from 2030-01-01 --to 2031-01-02`, "calendar: a range has at most 366 days, not 367"],
    [`--from 2030-01-01`, "calendar: --from needs --to; give both"],
    [`--to 2030-01-01`, "calendar: --to needs --from; give both"],
    [`--from 2030-02-10 --to 2030-02-06`, "calendar: --to 2030-02-06 is before --from 2030-02-10"],
    [`--from 2030-02-06 --to 2030-02-10 --week 2030-W06`, "calendar: --from and --to can't go together with --week or --month; give one"],
    [`--from 2030-02-06 --to 2030-02-10 --month 2030-02`, "calendar: --from and --to can't go together with --week or --month; give one"],
  ];
  for (const [options, refusal] of refusals) {
    expect(await mcp.sh(`calendar /Work ${options} --tag ${tag}`), options).toEqual({ text: refusal, isError: true });
  }
});

test("rg --regions: match= frames the match where it is narrower than its piece of text @shell", async ({ mcp, put, folder }) => {
  const dir = `/Work/${folder}`;
  // One page, 200 × 200 pt, the word "Manual" in Helvetica 18 at (20, 100).
  await put(`${dir}/manual.pdf`, readFileSync(join(fixturesDir, "tags/recent/Pump/manual.pdf")));

  // "nua" is a part of "Manual": crop= frames the word, match= the three letters inside it.
  const part = await mcp.sh(`rg --regions nua ${dir}`);
  expect(part).toEqual({ text: `${dir}/manual.pdf:p1:crop=14,76,71,30:match=34,76,41,30: Manual`, isError: false });
  const [crop, match] = [...part.text.matchAll(/(?:crop|match)=(\d+),(\d+),(\d+),(\d+)/g)].map((m) => m.slice(1).map(Number));
  const [x, y, w, h] = crop;
  const [mx, my, mw, mh] = match;
  expect(mx > x && mx + mw < x + w, "match= lies within crop=, narrower").toBe(true);
  expect([my, mh], "match= keeps the piece's height").toEqual([y, h]);

  // The whole word: the two boxes are the same, so crop= alone.
  expect(await mcp.sh(`rg --regions Manual ${dir}`)).toEqual({ text: `${dir}/manual.pdf:p1:crop=14,76,71,30: Manual`, isError: false });
  // Without --regions, the page alone.
  expect(await mcp.sh(`rg nua ${dir}`)).toEqual({ text: `${dir}/manual.pdf:p1: Manual`, isError: false });
  expect(await mcp.sh(`rg --regions -l nua ${dir}`)).toEqual({
    text: "rg: --regions says where matches lie in PDFs; it doesn't go with -l, -c, -v, --files, --tag or --version, nor right of a pipe",
    isError: true,
  });
});
