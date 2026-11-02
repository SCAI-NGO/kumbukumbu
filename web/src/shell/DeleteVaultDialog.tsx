// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import type { VaultView } from "../api/notes";
import { noteUrl } from "../api/paths";
import ConfirmTyped from "../components/ConfirmTyped";
import { useEditor } from "../state/editor";

/**
 * "Delete <vault>?", with its name typed and the password: from Settings and from the vault's menu. Away from the
 * vault afterwards, if one of its notes was open.
 */
export default function DeleteVaultDialog({ vault, onClose }: { vault: string; onClose(): void }) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const location = useLocation();
  // Read through the closure, not passed as the mutation's variables, which the mutation cache would keep.
  const [password, setPassword] = useState("");
  const remove = useMutation({
    mutationFn: () => call(api.POST("/api/vaults/{vault}/delete", { params: { path: { vault } }, body: { password, confirm: vault } })),
    onSuccess: async () => {
      // Out of the list at once: the shell, finding no vault chosen, picks the first one it lists.
      queryClient.setQueryData<VaultView[]>(["vaults"], (list) => list?.filter((v) => v.name !== vault));
      useEditor.getState().forgetVault(vault);
      if (location.pathname.startsWith(noteUrl(`/${vault}/`))) navigate("/", { replace: true });
      // Nothing of the vault stays in the caches: its tree, and every note, link and history read from it.
      queryClient.removeQueries({ predicate: (q) => q.queryKey.some((k) => typeof k === "string" && (k === vault || k.startsWith(`/${vault}/`))) });
      await Promise.all(["vaults", "keys", "search", "changes"].map((key) => queryClient.invalidateQueries({ queryKey: [key] })));
      onClose();
    },
  });
  return (
    <ConfirmTyped
      title={`Delete ${vault}?`}
      text="The vault goes for good, with every note in it, their history, the trash and the checkpoints. Nobody can bring it back. An API key that had only this vault is revoked."
      word={vault}
      action="Delete vault"
      pending={remove.isPending}
      error={remove.isError ? errorText(remove.error) : null}
      onConfirm={() => remove.mutate()}
      onClose={onClose}
      password={password}
      onPasswordChange={setPassword}
    />
  );
}
