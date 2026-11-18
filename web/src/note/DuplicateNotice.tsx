// SPDX-License-Identifier: AGPL-3.0-or-later
import { X } from "lucide-react";
import { useEffect, useRef, useState } from "react";

const SHOWN_MS = 8_000;

const choiceClass = "min-h-11 px-1.5 font-medium text-accent-text underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-accent";

/**
 * "Copy of plan.md created", with Open to go to the copy; the original stays open. It goes after 8 seconds, but not
 * while the focus is in it (then once the focus leaves), on Esc while focused, and on ✕.
 */
export default function DuplicateNotice({ name, onOpen, onClose }: { name: string; onOpen(): void; onClose(): void }) {
  const notice = useRef<HTMLDivElement>(null);
  const close = useRef(onClose);
  close.current = onClose;
  const [expired, setExpired] = useState(false);
  useEffect(() => {
    const timer = setTimeout(() => setExpired(true), SHOWN_MS);
    return () => clearTimeout(timer);
  }, []);
  useEffect(() => {
    if (expired && !notice.current?.contains(document.activeElement)) close.current();
  }, [expired]);

  return (
    <div
      ref={notice}
      role="status"
      aria-label="Copy created"
      onKeyDown={(e) => e.key === "Escape" && onClose()}
      onBlur={(e) => expired && !notice.current?.contains(e.relatedTarget) && onClose()}
      className="mx-3 mb-2 flex flex-none items-center rounded-[12px] border border-accent bg-surface-2 py-1 pr-1.5 pl-3.5 text-sm text-text"
    >
      <span className="min-w-0 truncate">{`${name} created`}</span>
      <button type="button" onClick={onOpen} className={`${choiceClass} ml-1.5 flex-none`}>
        Open
      </button>
      <button type="button" aria-label="Dismiss" onClick={onClose} className="ml-auto flex size-11 flex-none items-center justify-center rounded-[10px] text-muted focus-visible:outline-2 focus-visible:outline-accent">
        <X size={18} aria-hidden />
      </button>
    </div>
  );
}
