// SPDX-License-Identifier: AGPL-3.0-or-later

// Paths as the API gives them: from the root of the vault tree, the vault first ("/Work/projects/plan.md").

/** The app's URL of a note, each segment encoded: vault and note names are content, not URL syntax. */
export const noteUrl = (path: string) => `/notes/${path.split("/").slice(1).map(encodeURIComponent).join("/")}`;

/** The note path of `/notes/:vault/*`, from React Router's (decoded) params. */
export const pathFromRoute = (vault: string, rest: string) => `/${vault}/${rest}`;

export const vaultOf = (path: string) => path.split("/")[1];

export const folderOf = (path: string) => path.slice(0, path.lastIndexOf("/"));

/** The path within its vault, as `{path}` of `/api/notes/{vault}/{path}` wants it. */
export const inVault = (path: string) => path.split("/").slice(2).join("/");

export const titleOf = (path: string) => path.slice(path.lastIndexOf("/") + 1).replace(/\.md$/, "");

export const crumbOf = (path: string) => folderOf(path).split("/").slice(1).join(" / ");

/** `Untitled.md`, else `Untitled 2.md`, `Untitled 3.md` …: the first name no note in the folder has, ignoring case. */
export function untitledName(taken: string[]) {
  const names = new Set(taken.map((n) => n.toLowerCase()));
  let name = "Untitled.md";
  for (let i = 2; names.has(name.toLowerCase()); i++) name = `Untitled ${i}.md`;
  return name;
}
