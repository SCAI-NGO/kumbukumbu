// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import { Link } from "react-router-dom";
import { useVaults } from "../api/notes";
import { errorText } from "../api/error-text";
import { useSkills, type SkillView } from "../api/skills";
import { FormError, secondaryButtonClass } from "../components/form";
import TargetDialog from "./TargetDialog";

/** The groups in the order shown: System, Skills, then the other vaults as they come. */
function groups(skills: SkillView[]) {
  const by = new Map<string, SkillView[]>();
  for (const s of skills) by.set(s.location, [...(by.get(s.location) ?? []), s]);
  const rank = (l: string) => (l === "system" ? 0 : l === "Skills" ? 1 : 2);
  return [...by].sort(([a], [b]) => rank(a) - rank(b)).map(([location, list]) => ({ location, label: location === "system" ? "System" : location, list }));
}

function origin(s: SkillView): string[] {
  return [
    ...(s.path ? [s.path] : []),
    ...(s.lastEditor ? [`last changed by ${s.lastEditor}`] : []),
    ...(s.byOther ? ["another user"] : []),
    ...(s.source ? [`from ${s.source}`] : []),
    ...(s.originEdited ? ["origin edited"] : []),
  ];
}

/** The skills she sees, grouped by where they are; the text of a skill is never shown here, and a description only as plain text. */
export default function Installed() {
  const id = useId();
  const skills = useSkills();
  const vaults = useVaults();
  const [acting, setActing] = useState<{ skill: SkillView; mode: "copy" | "move" } | null>(null);

  if (skills.isPending) return <p className="text-text-body">Loading…</p>;
  if (skills.isError) return <FormError>{errorText(skills.error)}</FormError>;
  const { withoutSkills } = skills.data;
  const list = groups(skills.data.skills);

  return (
    <>
      {withoutSkills.length > 0 && (
        <p role="status" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-text-body">
          {withoutSkills.join(", ")} can't use your skills yet: give them the vault Skills in{" "}
          <Link to="/access/keys" className="text-accent-text underline">
            Access
          </Link>
          .
        </p>
      )}
      {list.length === 0 && <p className="text-text-body">No skills yet.</p>}
      {list.map((g, i) => (
        <section key={g.location} aria-labelledby={`${id}-group-${i}`} className="flex min-w-0 flex-col gap-3">
          <h2 id={`${id}-group-${i}`} className="text-[22px] font-bold break-words text-text">
            {g.label}
          </h2>
          <ul className="flex flex-col gap-3">
            {g.list.map((s) => (
              <li key={`${s.location}/${s.path ?? s.name}`} className="flex min-w-0 flex-col gap-2 rounded-[12px] border border-line p-3">
                <h3 className="font-bold break-words text-text">{s.name}</h3>
                <p className="break-words whitespace-pre-wrap text-text-body">{s.description}</p>
                <p className="font-mono text-xs break-words text-muted">{origin(s).join(" · ")}</p>
                {s.warnings.length > 0 && (
                  <ul className="flex flex-col gap-1 text-sm text-text">
                    {s.warnings.map((w) => (
                      <li key={w} className="break-words">
                        {w}
                      </li>
                    ))}
                  </ul>
                )}
                <div className="flex flex-wrap gap-2">
                  <button type="button" onClick={() => setActing({ skill: s, mode: "copy" })} className={secondaryButtonClass}>
                    Copy to…
                  </button>
                  {s.location !== "system" && s.canWrite && (
                    <button type="button" onClick={() => setActing({ skill: s, mode: "move" })} className={secondaryButtonClass}>
                      Move to…
                    </button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        </section>
      ))}
      {acting && <TargetDialog skill={acting.skill} mode={acting.mode} vaults={vaults.data ?? []} onClose={() => setActing(null)} />}
    </>
  );
}
