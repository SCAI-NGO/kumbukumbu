// SPDX-License-Identifier: AGPL-3.0-or-later
import { FileText, FileType, Image, type LucideIcon } from "lucide-react";
import type { FileKind } from "../api/paths";

/** The icon of a file by what it holds, wherever files are listed: tree, chips, trash, activity. */
export const fileKindIcon: Record<FileKind, LucideIcon> = { note: FileText, image: Image, pdf: FileType };
