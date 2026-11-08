// SPDX-License-Identifier: AGPL-3.0-or-later

// Files dropped where nothing takes them (the tree, a header, a page with no note open) would make the browser open
// them in place of the app, unsaved text and all. Only file drags: text dragged into a field still lands there.
export function keepStrayDrops(target: Window = window): () => void {
  const swallow = (e: DragEvent) => {
    if (e.defaultPrevented || !e.dataTransfer?.types.includes("Files")) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "none";
  };
  target.addEventListener("dragover", swallow);
  target.addEventListener("drop", swallow);
  return () => {
    target.removeEventListener("dragover", swallow);
    target.removeEventListener("drop", swallow);
  };
}
