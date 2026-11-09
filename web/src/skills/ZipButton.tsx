// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import type { SkillView } from "../api/skills";
import { preferencesQuery } from "../attachments/downloadFiles";
import ConfirmTyped from "../components/ConfirmTyped";
import { secondaryButtonClass } from "../components/form";
import { download, withTicket } from "../data/exportUrl";
import { sourceOf } from "./TargetDialog";

/**
 * Download .zip: a system skill as a plain link; a vault's skill as the gallery's ZIP of several files, with the
 * password for a one-time ticket while the setting asks for it or a remembered session's password window has closed
 * (the server refuses it otherwise). Both are asked afresh, not from the caches; either unknown, it asks.
 */
export default function ZipButton({ skill }: { skill: SkillView }) {
  const href = `/api/skills/zip?path=${encodeURIComponent(sourceOf(skill))}`;
  const queryClient = useQueryClient();
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState<"setting" | "window" | null>(null);
  if (skill.path == null) {
    return (
      // A plain link: the browser downloads it with the session cookie, under the name the server gives.
      <a href={href} download className={`${secondaryButtonClass} inline-flex items-center`}>
        Download .zip
      </a>
    );
  }

  const start = async () => {
    setAsking(true);
    const [setting, windowClosed] = await Promise.all([
      queryClient
        .fetchQuery({ ...preferencesQuery, staleTime: 0 })
        .then((p) => p.askPasswordForDownloads)
        .catch(() => true),
      call(api.GET("/api/session"))
        .then((s) => !s.passwordWindowOpen)
        .catch(() => true),
    ]).finally(() => setAsking(false));
    if (setting || windowClosed) setReason(setting ? "setting" : "window");
    else download(href);
  };

  return (
    <>
      <button type="button" disabled={asking} onClick={() => void start()} className={secondaryButtonClass}>
        Download .zip
      </button>
      {reason && <ZipDialog name={skill.name} href={href} reason={reason} onClose={() => setReason(null)} />}
    </>
  );
}

function ZipDialog({ name, href, reason, onClose }: { name: string; href: string; reason: "setting" | "window"; onClose(): void }) {
  const [password, setPassword] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <ConfirmTyped
      title={`Download ${name}?`}
      text={
        reason === "window"
          ? "Its files come as one ZIP. It's a while since you last gave your password on this device, so it confirms the download."
          : "Its files come as one ZIP. Your password confirms it; Settings can turn this question off."
      }
      action="Download"
      pending={pending}
      error={error}
      password={password}
      onPasswordChange={setPassword}
      onConfirm={async () => {
        setPending(true);
        setError(null);
        try {
          const { ticket } = await call(api.POST("/api/export/ticket", { body: { password } }));
          download(withTicket(href, ticket));
          onClose();
        } catch (e) {
          setError(errorText(e));
        } finally {
          setPending(false);
        }
      }}
      onClose={onClose}
    />
  );
}
