// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, call } from "../api/client";
import { sessionQuery } from "../api/publications";

/** Said once when a vault or alias of hers was renamed to make room for the vault Skills, until she dismisses it. */
export default function SkillsNotice() {
  const queryClient = useQueryClient();
  const notice = useQuery({ ...sessionQuery, select: (s) => s.skillsNotice ?? null }).data;
  const dismiss = useMutation({
    mutationFn: () => call(api.DELETE("/api/account/skills-notice")),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: sessionQuery.queryKey }),
  });
  if (!notice) return null;
  return (
    <div role="status" className="mx-3 mb-2 flex items-start gap-3 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
      <p className="min-w-0 flex-1">
        {notice.alias
          ? `The vault shared with you that you called Skills is now “${notice.renamed}” for you: the name Skills belongs to your new vault for skills. Nothing in it changed.`
          : `Your vault named Skills is now “${notice.renamed}”: the name Skills belongs to your new vault for skills. Its notes are untouched, and keys and apps that had it keep it under the new name.`}
      </p>
      <button type="button" onClick={() => dismiss.mutate()} disabled={dismiss.isPending} className="min-h-11 flex-none px-2 text-accent underline">
        Dismiss
      </button>
    </div>
  );
}
