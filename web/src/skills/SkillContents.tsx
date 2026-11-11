// SPDX-License-Identifier: AGPL-3.0-or-later
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { sizeText } from "../components/Sheet";
import { labelClass } from "../components/form";

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

type Contents = { hasScripts: boolean; files: { path: string; size: number | string }[]; skipped?: string[]; manifest: string };

/**
 * What a skill from outside would bring, before she takes it: the scripts notice, the files, those skipped and its
 * SKILL.md as a third party's text. The install dialog's, and Add to my skills' (`TargetDialog`).
 */
export default function SkillContents({ hasScripts, files, skipped = [], manifest }: Contents) {
  return (
    <>
      {hasScripts && <p className="text-text">{scriptNotice}</p>}
      <section className="flex flex-col gap-1">
        <h3 className={labelClass}>Files</h3>
        <ul className="flex flex-col">
          {files.map((f) => (
            <li key={f.path} className="flex justify-between gap-3 font-mono text-sm text-text-body">
              <span className="break-all">{f.path}</span>
              <span className="shrink-0">{sizeText(Number(f.size))}</span>
            </li>
          ))}
        </ul>
      </section>
      {skipped.length > 0 && (
        <section className="flex flex-col gap-1">
          <h3 className={labelClass}>Not installed</h3>
          <p className="text-text-body">These files are skipped: they are not text, or too large.</p>
          <ul className="flex flex-col">
            {skipped.map((s) => (
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
          <ReactMarkdown remarkPlugins={remarkPlugins} skipHtml components={components}>
            {manifest}
          </ReactMarkdown>
        </div>
      </section>
    </>
  );
}
