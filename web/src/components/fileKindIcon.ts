// SPDX-License-Identifier: AGPL-3.0-or-later
import { File, FileCode, FileText, FileType, Folder, Image, type LucideIcon } from "lucide-react";
import type { FileKind } from "../api/paths";

/** The icon of a file by what it holds, wherever files are listed: tree, chips, trash, activity. */
/** What stands for a file of kind `file` where another would be drawn (files spec, decision 5): the lightbox, a comparison. */
export const noPreviewText = "No preview for this file";

export const fileKindIcon: Record<FileKind, LucideIcon> = { note: FileText, image: Image, pdf: FileType, folder: Folder, text: FileCode, file: File };
