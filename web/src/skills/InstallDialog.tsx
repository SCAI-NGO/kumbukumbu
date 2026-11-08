// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import { useMarketplaceSkill } from "../api/skills";
import { FormError, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";
import TargetDialog from "./TargetDialog";

/** The sentence of a skill with scripts (skills design §3.9). */
export const scriptNotice = "This skill contains scripts. Kumbu² never runs them; a harness you give the skill to may.";

const remarkPlugins = [remarkGfm];

// A third party's text: no raw HTML (react-markdown drops it), no image fetched (its alt text instead), links as their text.
const components: Components = {
  h1: ({ node: _, ...props }) => <h3 className="mt-3 mb-1 text-base font-bold text-text" {...props} />,
  h2: ({ node: _, ...props }) => <h4 className="mt-3 mb-1 text-base font-bold text-text" {...props} />,
  h3: ({ node: _, ...props }) => <h5 className="mt-3 mb-1 text-sm font-bold text-text" {...props} />,
  h4: ({ node: _, ...props }) => <h6 className="mt-3 mb-1 text-sm font-bold text-text" {...props} />,
  h5: ({ node: _, ...props }) => <h6 className="mt-3 mb-1 text-sm font-bold text-text" {...props} />,
  h6: ({ node: _, ...props }) => <h6 className="mt-3 mb-1 text-sm font-semibold text-muted" {...props} />,
  p: ({ node: _, ...props }) => <p className="my-2" {...props} />,
  ul: ({ node: _, ...props }) => <ul className="my-2 list-disc pl-6" {...props} />,
  ol: ({ node: _, ...props }) => <ol className="my-2 list-decimal pl-6" {...props} />,
  pre: ({ node: _, ...props }) => <pre className="my-2 overflow-x-auto rounded-[10px] border border-line bg-surface px-3 py-2 font-mono text-xs" {...props} />,
  code: ({ node: _, ...props }) => <code className="font-mono text-[0.85em]" {...props} />,
  a: ({ children }) => <>{children}</>,
  img: ({ alt }) => (alt ? <span className="font-mono text-[0.85em] text-muted">{alt}</span> : null),
};

const sizeText = (bytes: number) => (bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`);

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
          data && (
            <>
              {data.hasScripts && <p className="text-text">{scriptNotice}</p>}
              <section className="flex flex-col gap-1">
                <h3 className={labelClass}>Files</h3>
                <ul className="flex flex-col">
                  {data.files.map((f) => (
                    <li key={f.path} className="flex justify-between gap-3 font-mono text-sm text-text-body">
                      <span className="break-all">{f.path}</span>
                      <span className="shrink-0">{sizeText(Number(f.size))}</span>
                    </li>
                  ))}
                </ul>
              </section>
              {data.skipped.length > 0 && (
                <section className="flex flex-col gap-1">
                  <h3 className={labelClass}>Not installed</h3>
                  <p className="text-text-body">These files are skipped: they are not text, or too large.</p>
                  <ul className="flex flex-col">
                    {data.skipped.map((s) => (
                      <li key={s} className="font-mono text-sm break-all text-text-body">
                        {s}
                      </li>
                    ))}
                  </ul>
                </section>
              )}
              <section className="flex flex-col gap-1">
                <h3 className={labelClass}>SKILL.md</h3>
                <div className="max-h-72 overflow-y-auto rounded-[10px] border border-line p-3 text-sm text-text-body">
                  <ReactMarkdown remarkPlugins={remarkPlugins} components={components}>
                    {data.manifest}
                  </ReactMarkdown>
                </div>
              </section>
            </>
          )
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
