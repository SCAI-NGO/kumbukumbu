// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (apps/admin-client/src/routes/invitations/reveal-screen.tsx)
import QRCode from "qrcode";
import { useEffect, useId, useState } from "react";
import { labelClass, primaryButtonClass, secondaryButtonClass } from "../../components/form";
import { LeaveDialog, useLeaveGuard } from "../../components/shownOnce";

/**
 * A new invitation's link and QR code, shown once. Until the link was copied, leaving the page or closing asks
 * once. Link and QR live in component state only.
 */
export default function InvitationReveal({ link, onDone }: { link: string; onDone(): void }) {
  const id = useId();
  const [saved, setSaved] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const [closing, setClosing] = useState(false);
  const [qr, setQr] = useState<string | null>(null);
  const blocker = useLeaveGuard(saved);

  useEffect(() => {
    let current = true;
    // SVG needs no canvas; the data URL goes to an <img> and nowhere else.
    QRCode.toString(link, { type: "svg", margin: 2, errorCorrectionLevel: "M" })
      .then((svg) => current && setQr(`data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`))
      .catch(() => current && setQr(null));
    return () => {
      current = false;
    };
  }, [link]);

  async function copy() {
    try {
      await navigator.clipboard.writeText(link);
      setCopyFailed(false);
      setSaved(true);
    } catch {
      // Permission denied or no secure context: the user can still select the text by hand.
      setCopyFailed(true);
    }
  }

  const leave = {
    id: `${id}-leave`,
    title: "You haven't saved the invitation link",
    text: "You won't see this link again. Without it, you'll need to make a new invitation.",
  };

  return (
    <section aria-labelledby={`${id}-title`} className="flex flex-col gap-5">
      <h2 id={`${id}-title`} className="text-[22px] font-bold text-text">
        Your new invitation
      </h2>
      <p className="text-text-body">This is shown only now. Send the link, or let the person scan the code.</p>

      <div className="flex flex-col gap-1.5">
        <p className={labelClass}>Link</p>
        <pre
          aria-label="Invitation link"
          className="rounded-[12px] border border-line bg-surface p-4 font-mono text-base leading-relaxed break-all whitespace-pre-wrap text-text select-all"
        >
          {link}
        </pre>
        <button type="button" onClick={() => void copy()} className={secondaryButtonClass}>
          Copy link
        </button>
      </div>

      {qr && (
        // A white ground whatever the colour scheme: scanners need dark on light.
        <img src={qr} alt="QR code for the invitation link" className="size-56 self-center rounded-[12px] bg-white" />
      )}

      {copyFailed && (
        <p aria-live="polite" className="text-sm text-text">
          Copying didn't work. Select the link and copy it by hand.
        </p>
      )}

      <button type="button" onClick={() => (saved ? onDone() : setClosing(true))} className={primaryButtonClass}>
        Done
      </button>

      {blocker.state === "blocked" && <LeaveDialog {...leave} onStay={() => blocker.reset?.()} onLeave={() => blocker.proceed?.()} />}
      {closing && <LeaveDialog {...leave} onStay={() => setClosing(false)} onLeave={onDone} />}
    </section>
  );
}
