// SPDX-License-Identifier: AGPL-3.0-or-later

// The entry of a pool worker: runs one job and posts its answer. Only a PdfError's code and
// sentence leave the thread; anything else becomes "failed", since an exception's text may quote
// the file.

import { parentPort } from "node:worker_threads";
import { analyse } from "./analyse.js";
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
    case "image": {
      const r = job.kind === "page" ? await renderPage(job.bytes, job.page, job.target, job.crop) : await renderImage(job.bytes, job.target, job.crop);
      // A copy in a buffer of its own: Skia's buffers cannot be transferred, and a Buffer may share one.
      const data = new Uint8Array(r.data);
      return { value: { ...r, data }, transfer: [data.buffer as ArrayBuffer] };
    }
  }
}

port.once("message", (job: Job) => {
  run(job).then(
    ({ value, transfer }) => post({ type: "done", value }, transfer),
    (e: unknown) =>
      post(e instanceof PdfError ? { type: "error", code: e.code, message: e.message } : { type: "error", code: "failed", message: "The job failed." }),
  );
});
