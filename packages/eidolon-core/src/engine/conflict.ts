// SPDX-License-Identifier: AGPL-3.0-or-later
// The text of a conflict copy: the local text with a callout naming both sides (spec §7).

const pad = (n: number) => String(n).padStart(2, "0");
const day = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const time = (d: Date) => `${pad(d.getHours())}:${pad(d.getMinutes())}`;

/** The writer label up to the first " (", as the server's Writers.Identity: "Laptop (mv)" is "Laptop". */
export function writerIdentity(label: string): string {
  const cut = label.indexOf(" (");
  return cut < 0 ? label : label.slice(0, cut);
}

export function conflictCopyText(
  local: string,
  o: { device: string | null; localAt: Date; remoteWriter: string; remoteAt: Date; originalPath: string },
): string {
  const target = o.originalPath.replace(/\.md$/i, "");
  const title = target.slice(target.lastIndexOf("/") + 1);
  const callout = [
    "> [!warning] Kumbu² conflict",
    `> Change from **${o.device ?? "this device"}** at ${day(o.localAt)} ${time(o.localAt)}.`,
    // The server shows a person's own other device by its name, and this copy reaches every member.
    o.device === null
      ? `> The other change, at ${time(o.remoteAt)}, is in [[${target}|${title}]].`
      : `> The change from **${writerIdentity(o.remoteWriter)}** at ${time(o.remoteAt)} is in [[${target}|${title}]].`,
    "> Merge what you need, then delete this copy.",
    "",
    "",
  ].join("\n");
  const end = frontmatterEnd(local);
  const head = local.slice(0, end);
  const gap = end > 0 && !head.endsWith("\n") ? "\n" : "";
  return head + gap + callout + local.slice(end);
}

/** Length of YAML frontmatter at the very start ("---" … "---" line), 0 when there is none. */
function frontmatterEnd(text: string): number {
  const open = /^---\r?\n/.exec(text);
  if (!open) return 0;
  let at = open[0].length;
  while (at <= text.length) {
    const next = text.indexOf("\n", at);
    const line = (next < 0 ? text.slice(at) : text.slice(at, next)).replace(/\r$/, "");
    if (/^---[ \t]*$/.test(line)) return next < 0 ? text.length : next + 1;
    if (next < 0) return 0;
    at = next + 1;
  }
  return 0;
}
