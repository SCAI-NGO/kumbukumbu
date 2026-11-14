// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import { isFlag, useFlag } from "./api";
import FlagIcon, { flagName } from "./FlagIcon";
import { useFlagging } from "./FlagMenu";

/**
 * The note's flag in its header (spec §2.3), as its icon and name; tapping it opens the flag menu. Nothing without a
 * flag: ⋯ → Flag is the way in then. A member who may only read sees the flag, with nothing to set. Asked again with
 * each new version of the note, as the calendar's line.
 */
export default function FlagLine({ path, version }: { path: string; version: string }) {
  const queryClient = useQueryClient();
  const flag = useFlag(path);
  const flagging = useFlagging();
  const seen = useRef(version);
  useEffect(() => {
    if (seen.current === version) return;
    seen.current = version;
    void queryClient.invalidateQueries({ queryKey: ["flag", path] });
  }, [version, path, queryClient]);

  const view = flag.data;
  // The menu and its message stay in the same place whether the line is shown or not: No flag takes the line away.
  if (!view || !isFlag(view.flag)) return <>{null}{flagging.element}</>;
  // The icon names itself; the name beside it is for the eye.
  const shown = (
    <>
      <FlagIcon flag={view.flag} size={18} />
      <span aria-hidden>{flagName(view.flag)}</span>
    </>
  );
  return (
    <>
      <div role="group" aria-label="Flag" className="flex flex-none items-center border-b border-line bg-surface px-4 text-sm text-text">
        {view.canWrite ? (
          <button
            type="button"
            title="Change the flag"
            onClick={() => flagging.open(path)}
            className="-ml-1 inline-flex min-h-12 items-center gap-2 rounded-[8px] px-1 font-medium focus-visible:outline-2 focus-visible:outline-accent"
          >
            {shown}
          </button>
        ) : (
          <span className="inline-flex min-h-12 items-center gap-2">{shown}</span>
        )}
      </div>
      {flagging.element}
    </>
  );
}
