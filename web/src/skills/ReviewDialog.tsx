// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId } from "react";
import { errorText } from "../api/error-text";
import { HttpError } from "../api/client";
import { skipSkillUpdate, takeSkillUpdate, useSkillReview } from "../api/skills";
import { FormError, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

type Props = { path: string; name: string; onClose(): void };

/**
 * What an update would change (skills spec §3.10), before she decides: where it comes from first, then every file with its
 * change and hunks, as plain text, never rendered. Take and Skip name the hash the review gave, so a source that moved on
 * since is refused and reviewed again rather than taken unseen.
 */
export default function ReviewDialog({ path, name, onClose }: Props) {
  const id = useId();
  const dialog = useDialog<HTMLDivElement>(onClose);
  const queryClient = useQueryClient();
  const review = useSkillReview(path);
  const data = review.data;
  const done = () => {
    void queryClient.invalidateQueries({ queryKey: ["skills"] });
    onClose();
  };
  const take = useMutation({ mutationFn: (hash: string) => takeSkillUpdate(path, hash), onSuccess: done });
  const skip = useMutation({ mutationFn: (hash: string) => skipSkillUpdate(path, hash), onSuccess: done });
  const failed = take.error ?? skip.error;
  const movedOn = failed instanceof HttpError && failed.status === 409 && failed.type === "moved-on";
  // Only a review in hand and settled may be acted on.
  const blocked = !data || review.isFetching || review.isError || take.isPending || skip.isPending || movedOn;
  const reviewAgain = () => {
    take.reset();
    skip.reset();
    void review.refetch();
  };

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
          Review the update of {name}
        </h2>
        {review.isPending || review.isFetching ? (
          <p className="text-text-body">Loading…</p>
        ) : review.isError ? (
          <FormError>{errorText(review.error)}</FormError>
        ) : (
          data && (
            <>
              <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
                {data.repo ? (
                  <>
                    <dt className={labelClass}>Repository</dt>
                    <dd className="font-mono text-sm break-all text-text">{data.repo}</dd>
                    <dt className={labelClass}>Path</dt>
                    <dd className="font-mono text-sm break-all text-text">{data.path}</dd>
                    <dt className={labelClass}>Commit</dt>
                    <dd className="font-mono text-sm text-text">{data.commit?.slice(0, 12)}</dd>
                  </>
                ) : (
                  <>
                    <dt className={labelClass}>Source</dt>
                    <dd className="text-sm text-text">Shared link</dd>
                  </>
                )}
              </dl>
              <section className="flex flex-col gap-3">
                <h3 className={labelClass}>Files</h3>
                <ul className="flex flex-col gap-3">
                  {data.files.map((f) => (
                    <li key={f.path} className="flex min-w-0 flex-col gap-1">
                      <div className="flex justify-between gap-3 font-mono text-sm text-text">
                        <span className="break-all">{f.path}</span>
                        <span className="shrink-0 text-muted">{f.change}</span>
                      </div>
                      {f.hunks.map((h, i) => (
                        <pre key={i} className="overflow-x-auto rounded-[10px] border border-line bg-surface px-3 py-2 font-mono text-xs whitespace-pre-wrap break-words text-text-body">
                          {h}
                        </pre>
                      ))}
                    </li>
                  ))}
                </ul>
              </section>
            </>
          )
        )}
        {failed && <FormError>{errorText(failed)}</FormError>}
        {movedOn && (
          <button type="button" onClick={reviewAgain} className={secondaryButtonClass}>
            Review again
          </button>
        )}
        <div className="flex flex-wrap gap-3">
          <button type="button" data-autofocus onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Close
          </button>
          <button type="button" disabled={blocked} onClick={() => { if (data) { take.reset(); skip.mutate(data.hash); } }} className={`${secondaryButtonClass} flex-1`}>
            Skip
          </button>
          <button type="button" disabled={blocked} onClick={() => { if (data) { skip.reset(); take.mutate(data.hash); } }} className={`${primaryButtonClass} flex-1`}>
            Take update
          </button>
        </div>
      </div>
    </div>
  );
}
