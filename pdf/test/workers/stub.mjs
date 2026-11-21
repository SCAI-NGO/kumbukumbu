// SPDX-License-Identifier: AGPL-3.0-or-later
// A stand-in for dist/worker.js that speaks the same protocol: for the pool's tests, and the server's
// time-out and busy tests (an analysis and an image list never end here, a page is answered at once).
// It runs as a child process too, for the kinds the pool runs there, and then speaks over IPC.
import { parentPort } from "node:worker_threads";

const port = parentPort ?? { postMessage: (m) => process.send(m), once: (e, f) => process.once(e, f) };

port.once("message", async (job) => {
  switch (job.kind) {
    case "echo":
      port.postMessage({ type: "done", value: job.value });
      break;
    case "hang":
    case "analyse":
    case "list":
      setInterval(() => {}, 1000);
      break;
    case "page":
      // What renderPage answers, for a 1 × 1 page.
      port.postMessage({
        type: "done",
        value: { data: new Uint8Array([0x89, 0x50, 0x4e, 0x47]), mediaType: "image/png", width: 1, height: 1, pageWidth: 1, pageHeight: 1, rotate: 0 },
      });
      break;
    case "slow":
      // Reports its page count first, as an analysis does, then takes longer than the base limit.
      port.postMessage({ type: "pages", pages: job.pages });
      setTimeout(() => port.postMessage({ type: "done", value: "slow" }), job.ms);
      break;
    case "fail":
      port.postMessage({ type: "error", code: "not_pdf", message: "The file is not a PDF." });
      break;
    case "crash":
      throw new Error("secret file content");
  }
});
