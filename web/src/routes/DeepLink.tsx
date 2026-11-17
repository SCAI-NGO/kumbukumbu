// SPDX-License-Identifier: AGPL-3.0-or-later
import { Navigate, useNavigate, useParams } from "react-router-dom";
import { findFolder, useTree, useVaults } from "../api/notes";
import { fileKindOf, folderOf, noteUrl, titleOf, viewUrl } from "../api/paths";
import { AuthScreen, primaryButtonClass } from "../components/form";
import { useEditor } from "../state/editor";

/**
 * The page of a note's link, `/n/:vaultId/<path inside the vault>`. The id is the same for everyone with access to the
 * vault, each of whom knows it by their own name; the link becomes the reader's own address (`/notes/…`, or `/view/…` for a
 * file). Whatever fails, nothing says whether the vault or the note exists: the reader may not be allowed to know.
 */
export default function DeepLink() {
  const { vaultId = "", "*": rest = "" } = useParams();
  const vaults = useVaults();
  const vault = vaults.data?.find((v) => v.id === vaultId);
  const tree = useTree(vault?.name ?? null);

  if (vaults.isPending || (vault && tree.isPending)) return <AuthScreen title="Opening the note…" />;
  const path = vault && `/${vault.name}/${rest}`;
  const folder = path && tree.data ? findFolder(tree.data.root, folderOf(path)) : undefined;
  if (!path || !folder?.notes.some((n) => n.path === path)) return <NotHere name={titleOf(path ?? `/${rest}`)} />;
  return <Navigate to={fileKindOf(path) === "note" ? noteUrl(path) : viewUrl(path)} replace />;
}

function NotHere({ name }: { name: string }) {
  const { setSearchQuery, setOverlay } = useEditor();
  const navigate = useNavigate();
  return (
    <AuthScreen title="Not here" lead="This link leads to a note that isn't here: it was moved or removed, or it's in a vault you can't see right now.">
      <button
        type="button"
        className={primaryButtonClass}
        onClick={() => {
          setSearchQuery(name);
          setOverlay("search");
          // The search sheet lives in the shell: this page is outside it, so the shell is opened to show it.
          navigate("/", { replace: true });
        }}
      >
        Search for it
      </button>
    </AuthScreen>
  );
}
