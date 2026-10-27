// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import ConfirmTyped from "../components/ConfirmTyped";
import { useExport } from "./useExport";

/** One export (a vault, a checkpoint) after the password: the ZIP is plain text, outside Kumbu²'s encryption. */
export default function ExportConfirm({ title, href, onClose }: { title: string; href: string; onClose(): void }) {
  const [password, setPassword] = useState("");
  const exporting = useExport();
  return (
    <ConfirmTyped
      title={title}
      text="The ZIP holds the notes as plain text, outside Kumbu²'s encryption: your password confirms it."
      action="Export"
      pending={exporting.pending}
      error={exporting.error}
      password={password}
      onPasswordChange={setPassword}
      onConfirm={async () => {
        if (await exporting.start(href, password)) onClose();
      }}
      onClose={onClose}
    />
  );
}
