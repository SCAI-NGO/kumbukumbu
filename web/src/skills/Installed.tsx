// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useId, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { useVaults } from "../api/notes";
import { errorText } from "../api/error-text";
import { usePublishEnabled } from "../api/publications";
import { checkSkillUpdates, shareSkill, useSkills, type SkillView } from "../api/skills";
import { FormError, secondaryButtonClass } from "../components/form";
import ReviewDialog from "./ReviewDialog";
import { ConfirmShareAnew, SkillLinkReveal } from "./Shared";
import TargetDialog from "./TargetDialog";
import ZipButton from "./ZipButton";

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
    // The server names a source only where the copy's sealed origin agrees; a claim nothing backs is only a claim.
    ...(s.source ? [`from ${s.source}`] : s.sourceUnverified ? ["unverified origin"] : []),
    ...(s.shared ? ["shared by link"] : []),
  ];
}

/** The skills she sees, grouped by where they are; the text of a skill is never shown here, and a description only as plain text. */
export default function Installed() {
  const id = useId();
  const skills = useSkills();
  const vaults = useVaults();
  const [acting, setActing] = useState<{ skill: SkillView; mode: "copy" | "move" } | null>(null);
  const queryClient = useQueryClient();
  const publishEnabled = usePublishEnabled();
  const [asking, setAsking] = useState<SkillView | null>(null);
  // The new link, in this component's state alone: the mutation is reset once it is in, so no cache keeps it.
  const [revealed, setRevealed] = useState<{ name: string; link: string } | null>(null);
  const share = useMutation({
    mutationFn: (s: SkillView) => shareSkill(s.path!),
    onSuccess: ({ link }, s) => {
      setRevealed({ name: s.name, link });
      share.reset();
      void queryClient.invalidateQueries({ queryKey: ["skills"] });
    },
    onSettled: () => setAsking(null),
  });

  // What the last check found for each copy; the list itself tells of an update or an edited origin, only "gone" is the check's alone.
  const [gone, setGone] = useState<Set<string>>(new Set());
  const [reviewing, setReviewing] = useState<SkillView | null>(null);
  const check = useMutation({
    mutationFn: checkSkillUpdates,
    onSuccess: (states) => {
      setGone(new Set(states.filter((u) => u.state === "gone").map((u) => u.path)));
      void queryClient.invalidateQueries({ queryKey: ["skills"] });
    },
  });
  // Asked once when the tab opens (never in the background).
  const asked = useRef(false);
  useEffect(() => {
    if (asked.current) return;
    asked.current = true;
    check.mutate();
  }, [check]);

  if (revealed) return <SkillLinkReveal name={revealed.name} link={revealed.link} onDone={() => setRevealed(null)} />;
  if (skills.isPending) return <p className="text-text-body">Loading…</p>;
  if (skills.isError) return <FormError>{errorText(skills.error)}</FormError>;
  const { withoutSkills } = skills.data;
  const list = groups(skills.data.skills);
  // Her own vaults: a member never shares (skills design §3.7).
  const own = new Set((vaults.data ?? []).filter((v) => v.owner == null).map((v) => v.name));
  const mayShare = (s: SkillView) => publishEnabled === true && s.path != null && s.location !== "system" && own.has(s.location) && !s.shared;
  // A link of it was withdrawn since it was last shared: the copies made from that link lose their updates.
  const onShare = (s: SkillView) => (s.withdrawnBefore ? setAsking(s) : share.mutate(s));

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
      <div className="flex flex-wrap items-center gap-3">
        <button type="button" disabled={check.isPending} onClick={() => check.mutate()} className={secondaryButtonClass}>
          Check for updates
        </button>
        {check.isPending && <span className="text-sm text-muted">Checking…</span>}
      </div>
      {check.isError && <FormError>{errorText(check.error)}</FormError>}
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
                {s.originEdited ? (
                  <p className="text-sm text-text">Origin edited: no updates</p>
                ) : s.update === "available" ? (
                  <p className="text-sm font-medium text-text">Update available</p>
                ) : (
                  s.path != null && gone.has(s.path) && <p className="text-sm text-text">Source gone</p>
                )}
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
                  <ZipButton skill={s} />
                  {!s.originEdited && s.update === "available" && s.path != null && s.canWrite && (
                    <button type="button" onClick={() => setReviewing(s)} className={secondaryButtonClass}>
                      Review
                    </button>
                  )}
                  {mayShare(s) && (
                    <button type="button" disabled={share.isPending} onClick={() => onShare(s)} className={secondaryButtonClass}>
                      Share
                    </button>
                  )}
                </div>
                {share.isError && share.variables?.path === s.path && <FormError>{errorText(share.error)}</FormError>}
              </li>
            ))}
          </ul>
        </section>
      ))}
      {asking && <ConfirmShareAnew busy={share.isPending} onCancel={() => setAsking(null)} onShare={() => share.mutate(asking)} />}
      {reviewing?.path && <ReviewDialog path={reviewing.path} name={reviewing.name} onClose={() => setReviewing(null)} />}
      {acting && <TargetDialog skill={acting.skill} mode={acting.mode} vaults={vaults.data} onClose={() => setActing(null)} />}
    </>
  );
}
