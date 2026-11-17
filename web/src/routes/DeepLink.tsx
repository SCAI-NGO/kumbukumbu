// SPDX-License-Identifier: AGPL-3.0-or-later
import { Navigate, useNavigate, useParams } from "react-router-dom";
import { useArchivedFiles } from "../api/archive";
import { findFolder, useNote, useTree, useVaults } from "../api/notes";
import { fileKindOf, folderOf, noteUrl, titleOf, viewUrl, type LightboxState } from "../api/paths";
import { AuthScreen, primaryButtonClass } from "../components/form";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { settleStart } from "../shell/lastNote";

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
  // The tree leaves `_archive/` out, so an archived note is looked for as the archive sheet opens it, by reading it.
  if (path && rest.split("/")[0] === "_archive") return fileKindOf(path) === "note" ? <ArchivedNote path={path} /> : <ArchivedFile vault={vault.name} path={path} />;
  const folder = path && tree.data ? findFolder(tree.data.root, folderOf(path)) : undefined;
  if (!path || !folder?.notes.some((n) => n.path === path)) return <NotHere name={titleOf(path ?? `/${rest}`)} />;
  return <Navigate to={fileKindOf(path) === "note" ? noteUrl(path) : viewUrl(path)} replace />;
}

function ArchivedNote({ path }: { path: string }) {
  const note = useNote(path);
  if (note.isPending) return <AuthScreen title="Opening the note…" />;
  if (note.isError) return <NotHere name={titleOf(path)} />;
  return <Navigate to={noteUrl(path)} replace />;
}

/** The archive sheet's list of the vault's archived files (`useArchivedFiles`), which holds attachments too. */
function ArchivedFile({ vault, path }: { vault: string; path: string }) {
  const files = useArchivedFiles(`/${vault}/_archive`, true);
  if (files.isPending) return <AuthScreen title="Opening the note…" />;
  if (!files.data?.includes(path)) return <NotHere name={titleOf(path)} />;
  return <Navigate to={viewUrl(path)} state={{ paths: [path] } satisfies LightboxState} replace />;
}

function NotHere({ name }: { name: string }) {
  const { setSearchQuery, setOverlay } = useEditor();
  const navigate = useNavigate();
  const userId = useSession((s) => s.user?.id ?? null);
  return (
    <AuthScreen title="Not here" lead="This link leads to a note that isn't here: it was moved or removed, or it's in a vault you can't see right now.">
      <button
        type="button"
        className={primaryButtonClass}
        onClick={() => {
          setSearchQuery(name);
          setOverlay("search");
          // The search sheet lives in the shell: this page is outside it, so the shell is opened to show it.
          // The start is decided: the note kept from the last visit must not take the shell from the search.
          settleStart(userId);
          navigate("/", { replace: true });
        }}
      >
        Search for it
      </button>
    </AuthScreen>
  );
}
