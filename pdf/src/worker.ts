// SPDX-License-Identifier: AGPL-3.0-or-later

// The entry of a pool worker: runs one job and posts its answer. Only a PdfError's code and
// sentence leave the thread; anything else becomes "failed", since an exception's text may quote
// the file.

import { parentPort } from "node:worker_threads";
import { analyse } from "./analyse.js";
import { textBoxes } from "./boxes.js";
import { extractImage, listImages, quietly, renderRegion } from "./extract.js";
import { renderImage } from "./image.js";
import { PdfError } from "./pdfjs.js";
import type { Job, WorkerMessage } from "./pool.js";
import { renderPage } from "./render.js";

const port = parentPort!;
const post = (m: WorkerMessage, transfer: ArrayBuffer[] = []) => port.postMessage(m, transfer);

async function run(job: Job): Promise<{ value: unknown; transfer: ArrayBuffer[] }> {
  switch (job.kind) {
    case "analyse":
      return { value: await analyse(job.bytes, job.maxPages, (pages) => post({ type: "pages", pages })), transfer: [] };
    case "page":
    case "image":
      return own(
        job.kind === "page"
          ? await renderPage(job.bytes, job.page, job.target, job.crop, job.rotate)
          : await renderImage(job.bytes, job.target, job.crop, job.rotate, job.frame),
      );
    case "boxes":
      return { value: await textBoxes(job.bytes, job.pages), transfer: [] };
    // A backstop: whatever pdf.js prints during an extraction job, warnings on or not, may name details
    // of the file, and is swallowed.
    case "list":
      return quietly(async () => ({ value: await listImages(job.bytes, job.page), transfer: [] }));
    case "extract":
      return quietly(async () => own(await extractImage(job.bytes, job.page, job.n)));
    case "region":
      return quietly(async () => own(await renderRegion(job.bytes, job.page, job.crop, job.rotate, job.format)));
  }
}

// A copy in a buffer of its own: Skia's buffers cannot be transferred, and a Buffer may share one.
function own<T extends { data: Uint8Array }>(r: T): { value: T; transfer: ArrayBuffer[] } {
  const data = new Uint8Array(r.data);
  return { value: { ...r, data }, transfer: [data.buffer as ArrayBuffer] };
}

port.once("message", (job: Job) => {
  run(job).then(
    ({ value, transfer }) => post({ type: "done", value }, transfer),
    (e: unknown) =>
      post(e instanceof PdfError ? { type: "error", code: e.code, message: e.message } : { type: "error", code: "failed", message: "The job failed." }),
  );
});
