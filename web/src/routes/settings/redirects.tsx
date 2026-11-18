// SPDX-License-Identifier: AGPL-3.0-or-later
import { Navigate, useLocation, useParams } from "react-router-dom";

/** `/settings` before it had a page per entry: its anchors went to sections now on Appearance and Security. */
export function SettingsMoved() {
  const { hash } = useLocation();
  return <Navigate to={`/settings/${hash === "#discreet-vaults" ? "security" : "appearance"}${hash}`} replace />;
}

/** Access and Skills before they were in the settings: `/access/:tab`, `/skills/:tab`, in bookmarks and older links. */
export function Moved({ to }: { to: string }) {
  const { tab } = useParams();
  const { search, hash } = useLocation();
  return <Navigate to={`/settings/${to}/${tab}${search}${hash}`} replace />;
}
