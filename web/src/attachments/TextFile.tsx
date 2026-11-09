// SPDX-License-Identifier: AGPL-3.0-or-later
import { Download } from "lucide-react";
import { useEffect, useState } from "react";
import { fileAtUrl, type FileAt } from "../api/paths";
import { fetchFile } from "../api/notes";

/**
 * A text file of a skill's folder as its text, in a `pre`: never rendered. A script, an HTML or an SVG file is data
 * here; React sets it as text, and no frame, object or object URL puts it on the page. The server serves it as
 * `text/plain` with a policy that forbids anything to run, as a second line. `path` is below `vault`.
 */
export default function TextFile({ vault, path, at = {} }: { vault: string; path: string; at?: FileAt }) {
  const [state, setState] = useState<{ text: string } | "failed" | null>(null);
  const { version, checkpoint } = at;

  useEffect(() => {
    const controller = new AbortController();
    setState(null);
    void fetchFile(vault, path, { version, checkpoint }, "arrayBuffer", controller.signal)
      .then(({ data, response }) => {
        if (controller.signal.aborted) return;
        setState(response.ok && data ? { text: new TextDecoder("utf-8").decode(data as ArrayBuffer) } : "failed");
      })
      .catch(() => {
        if (!controller.signal.aborted) setState("failed");
      });
    return () => controller.abort();
  }, [vault, path, version, checkpoint]);

  const href = fileAtUrl(vault, path, at);
  return (
    <div className="flex h-full w-full flex-col gap-3 overflow-hidden px-3 pt-[72px] pb-[88px]">
      {state === "failed" ? (
        <p role="alert" className="text-[14px] text-[#a09d96]">
          The file couldn't be read.
        </p>
      ) : (
        <pre
          tabIndex={0}
          aria-label="File content"
          className="min-h-0 flex-1 overflow-auto rounded-[10px] bg-[#1a1a1e] p-3 font-mono text-[13px] leading-[1.5] whitespace-pre-wrap [overflow-wrap:anywhere] text-[#eceae4]"
        >
          {state?.text}
        </pre>
      )}
      <a href={`${href}${href.includes("?") ? "&" : "?"}download=1`} download className="flex items-center gap-2 self-start text-[14px] text-[#f0a848] underline">
        <Download size={16} aria-hidden />
        Download
      </a>
    </div>
  );
}
