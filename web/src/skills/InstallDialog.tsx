// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import { useMarketplaceSkill } from "../api/skills";
import { FormError, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import SkillContents from "./SkillContents";
import TargetDialog from "./TargetDialog";

export { scriptNotice } from "./SkillContents";

type Props = { repo: string; commit: string; path: string; name: string; onClose(): void; onInstalled(path: string): void };

/**
 * What installing a marketplace skill would bring (skills design §3.9), before she chooses: repository, path and the commit
 * first, then the scripts notice, the files, those skipped and the skill's SKILL.md. Install then asks for the target.
 */
export default function InstallDialog({ repo, commit, path, name, onClose, onInstalled }: Props) {
  const id = useId();
  const skill = useMarketplaceSkill(repo, commit, path);
  const vaults = useVaults();
  const [choosing, setChoosing] = useState(false);
  const dialog = useDialog<HTMLDivElement>(onClose);

  if (choosing) {
    return <TargetDialog mode="install" repo={repo} commit={commit} path={path} name={name} vaults={vaults.data} onClose={onClose} onAdded={onInstalled} />;
  }
  const data = skill.data;
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="flex max-h-full w-full max-w-xl flex-col gap-4 overflow-y-auto rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <h2 id={`${id}-title`} className="text-lg font-bold break-words text-text">
          Install {name}
        </h2>
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
          <dt className={labelClass}>Repository</dt>
          <dd className="font-mono text-sm break-all text-text">{repo}</dd>
          <dt className={labelClass}>Path</dt>
          <dd className="font-mono text-sm break-all text-text">{path}</dd>
          <dt className={labelClass}>Commit</dt>
          <dd className="font-mono text-sm text-text">{commit.slice(0, 12)}</dd>
        </dl>
        {skill.isPending ? (
          <p className="text-text-body">Loading…</p>
        ) : skill.isError ? (
          <FormError>{errorText(skill.error)}</FormError>
        ) : (
          data && <SkillContents hasScripts={data.hasScripts} files={data.files} skipped={data.skipped} manifest={data.manifest} />
        )}
        <div className="flex gap-3">
          <button type="button" data-autofocus onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Cancel
          </button>
          <button type="button" disabled={!data} onClick={() => setChoosing(true)} className={`${primaryButtonClass} flex-1`}>
            Install
          </button>
        </div>
      </div>
    </div>
  );
}
