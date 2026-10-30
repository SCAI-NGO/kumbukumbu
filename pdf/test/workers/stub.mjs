// SPDX-License-Identifier: AGPL-3.0-or-later
// A stand-in for dist/worker.js that speaks the same protocol: for the pool's tests, and the server's
// time-out and busy tests (an analysis never ends here, a page is answered at once).
import { parentPort } from "node:worker_threads";

parentPort.once("message", async (job) => {
  switch (job.kind) {
    case "echo":
      parentPort.postMessage({ type: "done", value: job.value });
      break;
    case "hang":
    case "analyse":
      setInterval(() => {}, 1000);
      break;
    case "page":
      // What renderPage answers, for a 1 × 1 page.
      parentPort.postMessage({
        type: "done",
        value: { data: new Uint8Array([0x89, 0x50, 0x4e, 0x47]), mediaType: "image/png", width: 1, height: 1, pageWidth: 1, pageHeight: 1 },
      });
      break;
    case "slow":
      // Reports its page count first, as an analysis does, then takes longer than the base limit.
      parentPort.postMessage({ type: "pages", pages: job.pages });
      setTimeout(() => parentPort.postMessage({ type: "done", value: "slow" }), job.ms);
      break;
    case "fail":
      parentPort.postMessage({ type: "error", code: "not_pdf", message: "The file is not a PDF." });
      break;
    case "crash":
      throw new Error("secret file content");
  }
});
