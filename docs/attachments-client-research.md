# MCP clients and multimodal tool results: research for Kumbu² v2.5 attachments

Researched 2026-10-26, ChatGPT section updated 2026-10-27. Every finding is dated (date of the source, or "read 2026-10-26" for living docs and source code on a default branch).

Legend:

- **[C]** confirmed by a primary source (spec, official docs, source code, maintainer statement, or a detailed reproducible report)
- **[R]** reported by users in an issue with a reproduction, no maintainer confirmation
- **[I]** inferred by me from the sources, not stated anywhere
- **[?]** unknown: no source found

---

## 1. MCP spec: tool-result content types

Current protocol version: **2026-07-28** ([versioning](https://modelcontextprotocol.io/specification/versioning), read 2026-10-26). The tool-result content types are unchanged from 2025-11-25 ([tools, 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/server/tools); [tools, 2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)). [C]

| Type | Shape | Notes |
|---|---|---|
| `text` | `{type, text}` | |
| `image` | `{type, data: base64, mimeType}` | |
| `audio` | `{type, data: base64, mimeType}` | |
| `resource_link` | `{type, uri, name, description?, mimeType?}` | "the tool will return a URI that can be subscribed to or fetched by the client". Links "are not guaranteed to appear in the results of a `resources/list` request." Fetching is a **MAY** for the client, not a requirement. |
| `resource` (embedded) | `{type, resource: {uri, mimeType?, text \| blob}}` | `blob` is base64 (`BlobResourceContents`). Servers that use embedded resources **SHOULD** implement the `resources` capability. |
| `structuredContent` | any JSON value (loosened in 2026-07-28, SEP-2106) | A tool that returns it "SHOULD also return the serialized JSON in a TextContent block". |

All content types accept `annotations` (`audience: ["user"|"assistant"]`, `priority`, `lastModified`). [C]

**Size guidance:** none for tool results in the spec. [C, by absence on the tools page] The only related item is the long-standing open issue [#527 "Base64 in JSON RPC will not scale for file content"](https://github.com/modelcontextprotocol/modelcontextprotocol/issues/527) (opened 2025-05-14, still open), which suggests download URLs as an alternative. [C]

**Client capability announcing accepted content types:** none. 2026-07-28 sends `io.modelcontextprotocol/clientCapabilities` in every request's `_meta` and adds an `extensions` field to `ClientCapabilities` ([changelog 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/changelog)), but there is no field for "I accept images / PDFs / resource links". [C, by absence] A server only learns the client's identity from `io.modelcontextprotocol/clientInfo` (SHOULD on each request in 2026-07-28; `initialize` in older versions). [C] One server author in [claude-code#53453](https://github.com/anthropics/claude-code/issues/53453) (2026-04-26) branches on `user-agent: Claude-User` as a workaround. [R]

---

## 2. Client by client

### Summary table

| Client | `image` part → model as image | Embedded `resource` with `application/pdf` blob | `resource_link` | Known limits |
|---|---|---|---|---|
| **claude.ai web** (remote connectors) | **Yes** [C]. Same-turn delivery fixed "early August 2026" (bug from mid-July) | **Broken**: rejected with `-32602 invalid_union`, blob stripped by the connector layer ([claude-ai-mcp#1086](https://github.com/anthropics/claude-ai-mcp/issues/1086), open, 2026-09-29) [R]. Not documented as a document block anywhere [?] | Not fetched ([claude-code#53453](https://github.com/anthropics/claude-code/issues/53453), 2026-04-26) [R]; breaks MCP Apps widgets ([#753](https://github.com/anthropics/claude-ai-mcp/issues/753)) [R] | ~150,000 chars per tool result, 240 s timeout [C]; 10 MB per image, 20 images per message [C] |
| **Claude Desktop** (chat) | **Yes** [C], same fix as above | Same `invalid_union` rejection ([claude-code#94746](https://github.com/anthropics/claude-code/issues/94746), closed "completed" 2026-10-06 with no explanation) [R]; status after the fix unknown [?] | Shown in the UI ([claude-ai-mcp#1173](https://github.com/anthropics/claude-ai-mcp/issues/1173), 2026-10-22) [R]; fetch unknown [?] | as claude.ai [C] |
| **Claude Code** (CLI, v2.1.310) | **Yes**: PNG/JPEG/GIF/WebP inline, possibly downscaled; original also saved to a file [C] | **Not a document**: bytes saved to `tool-results/…pdf`, model gets a placeholder line with the path [C] (maintainer, 2026-08-17). The model can then use the built-in `Read` tool on that file, and Read does send PDFs as document blocks [I] | Supported since 1.0.44 (2025-07-07) [C]; the model reads it via the MCP resource tool on demand, it is not auto-inlined [R/I] | 25,000 tokens default (`MAX_MCP_OUTPUT_TOKENS`), warning at 10,000; text > 50,000 chars goes to a file unless `_meta["anthropic/maxResultSizeChars"]` (max 500,000); images still count against the token limit; 16 MiB per HTTP response [C] |
| **Claude mobile** | Yes [I]: tool calls go through Claude's backend, so presumably the same as claude.ai; the image delay was reproduced on iOS [R] | as claude.ai [I] | as claude.ai [I] | as claude.ai [I] |
| **Hermes Agent** (main, read 2026-10-26) | **Yes**, when the model has vision and the route accepts images in tool results: at most 4 per result, downscaled (1568 px, JPEG); otherwise a `MEDIA:` path only [C] | **Not a document**: decoded into a document cache, model gets `[MCP resource saved to <path> … read it with read_file]` [C] | **Not fetched**: text marker pointing at the server's `read_resource` tool [C] | 50 MB per decoded blob; 2,000,000-char hard cap on text, 50K spillover to file [C] |
| **ChatGPT** (connectors / Apps SDK) | **No**: an image block shows up as `{}`, "not guaranteed to become model-visible vision input" (OpenAI support, 2026-04-22) [C]; in Code Mode "Image output is not supported" since about 2026-09-18, while Codex reads the same image ([codex#46927](https://github.com/openai/codex/issues/46927), open) [R] | [?] | One user says it works ([claude-code#53453](https://github.com/anthropics/claude-code/issues/53453)) [R]; in the newer ChatGPT environment `resources/read` of a `resource_link` fails with `-32002 Unknown resource`, the older one copied the file to `/mnt/data` (2026-10-23) [R] | None documented; a ~3 MiB PNG fails in an Apps SDK component where a ~3 KB one works, cause unknown [R] |
| **Codex** (OpenAI CLI, main, read 2026-10-27) | **Yes**: becomes an `input_image` in the `function_call_output` sent to the Responses API, detail `high` unless `_meta["codex/imageDetail"]` says otherwise [C] (code) | **Not a document**: unknown content types are serialised as JSON text, so the base64 blob lands in the context as text [C] (code) | Same: JSON text, not fetched [C] (code) | [?] |
| **Cursor** | Yes, "if the model supports images" [C] (docs, read 2026-10-26); CLI and GPT-model bugs reported [R] | [?] (not documented) | [?] | [?] |
| **VS Code Copilot** (source, file last changed 2026-06-24) | Yes: becomes a resized data part for the model [C] (code); actual vision depends on the model [I] | **Not a document**: non-image blobs become a *linked resource reference* (the model gets a permalink, not the bytes) [C] (code) | Image MIME: VS Code reads the resource itself and inlines the image [C]; other MIME types: passed as a reference [C] | [?] |
| **Open WebUI** (v0.12.x) | Yes since PR [#30358](https://github.com/open-webui/open-webui/pull/30358) (merged 2026-09-22) [C] | **Dropped**: becomes the text `[Resource: …] (binary data, mimeType: …)`, bytes gone (maintainer confirmed, 2026-10-24) [C] | [?] | [?] |
| **LibreChat** (main, parser changed 2026-09-24) | Yes for recognised providers (Anthropic, OpenAI, Google, …): sent as `image_url` artefacts [C] (code); for other providers the result is stringified [C] | **Summarised**: binary blob turned into a short text note, never sent as base64 [C] (code) | Described as text, not fetched [C] (code) | 10 MB per image (`MCP_IMAGE_DATA_MAX_BYTES`) [C] |

### 2.1 claude.ai (web, remote connectors)

- Officially supported: "Text and image-based tool results", "Text and binary resources"; not supported: resource subscriptions, sampling, advanced/draft capabilities ([Build an MCP server for Claude](https://claude.com/docs/connectors/building/index.md), read 2026-10-26). [C]
- Limits: max tool result ~150,000 characters, tool call timeout 240 s (same page). [C] When a result exceeds ~150,000 chars and the code-execution sandbox is on, Claude writes the result to the sandbox instead of passing it inline ([MCP Apps troubleshooting](https://claude.com/docs/connectors/building/mcp-apps/troubleshooting.md)). The same page advises: "if your data includes large blobs such as full document text, base64-encoded images … return identifiers or previews in the initial result and provide a separate tool to retrieve the full content when needed". [C] Whether base64 image data counts towards the 150,000 chars: [?]
- **Images:** reach the model. From about mid-July 2026 there was a regression where they arrived one turn late ([claude-ai-mcp#731](https://github.com/anthropics/claude-ai-mcp/issues/731), 2026-07-30; [#643](https://github.com/anthropics/claude-ai-mcp/issues/643)). Anthropic (localden, 2026-10-18): "We fixed that on our side in early August: the model now receives image blocks from MCP tool results in the same turn, with no client update required." [C] UI: images appear only in the collapsed tool accordion, not inline in the answer ([#238](https://github.com/anthropics/claude-ai-mcp/issues/238), open). [R]
- **Embedded PDF blob:** the custom-connector layer drops `blob` and rejects the whole result with `-32602 Invalid tools/call result: invalid_union` (reproduced with Google's own Cloud Storage MCP server, [#1086](https://github.com/anthropics/claude-ai-mcp/issues/1086), 2026-09-29, open, no reply). Also seen on Anthropic's own Slack connector for PDF/XLSX/DOCX while PNG and text worked (comment on [claude-code#94746](https://github.com/anthropics/claude-code/issues/94746), 2026-09-16). [R] A separate bug routes a non-image blob (`text/csv`) into the image pipeline: "The tool returned an image in an unsupported format" ([#536](https://github.com/anthropics/claude-ai-mcp/issues/536), 2026-07-03, open). [R] Embedded **text** resources do reach the model ([#287](https://github.com/anthropics/claude-ai-mcp/issues/287), 2026-05-14). [R] Whether a working PDF blob would become a `document` block: **no source** [?].
- **Voice mode** replaces embedded resources with `[non-text content: resource]` on web, desktop and iPhone ([#972](https://github.com/anthropics/claude-ai-mcp/issues/972), 2026-09-02, reconfirmed 2026-10-24). [R]
- **resource_link:** "Claude Code and Claude.ai do not fetch or render `resource_link` items" ([claude-code#53453](https://github.com/anthropics/claude-code/issues/53453), 2026-04-26, closed stale with no fix). [R] An MCP Apps widget gets no tool-result event when the result contains a `resource_link` ([#753](https://github.com/anthropics/claude-ai-mcp/issues/753), 2026-08-02, closed as a duplicate of #647). [R]

### 2.2 Claude Desktop

- Same hosted limits and image fix as claude.ai (the docs table says "claude.ai and Desktop"). [C]
- PDF blob through a connector: `-32602 invalid_union` (Desktop 2.110.0, protocol 2026-07-28). The same payload works through the Claude Code engine ([claude-code#94746](https://github.com/anthropics/claude-code/issues/94746), opened 2026-09-16, **closed "completed" 2026-10-06** with no maintainer comment). A user on 2026-09-23 said "Works fine in Claude Code, web version of Cowork but fails on Desktop". [R] Whether it now works, and how (document block or file on disk): [?]
- Local stdio server in Desktop chat: the model receives text and image blocks, but the expanded tool view shows only the image ([claude-ai-mcp#1173](https://github.com/anthropics/claude-ai-mcp/issues/1173), 2026-10-22, Desktop 3.1623.1 on CachyOS). That report also says the images are saved as `…/tool-results/mcp-remote-devices-blob-<id>.jpg`, so the Desktop chat apparently runs local servers through the Claude Code engine. [R/I]

### 2.3 Claude Code

Source: [Claude Code MCP docs, "MCP output limits and warnings" and "Images in tool results"](https://code.claude.com/docs/en/mcp) (read 2026-10-26) and [CHANGELOG](https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md) (latest 2.1.310).

- **Images** [C]: "When an MCP tool returns a PNG, JPEG, GIF, or WebP image, Claude sees the image inline in the conversation. The inline copy may be scaled down or compressed …" The original is also saved to `tool-results/` (v2.1.283+). Embedded resources with an image `blob` are forwarded as image blocks too (maintainer bcherny on [#72271](https://github.com/anthropics/claude-code/issues/72271), 2026-08-17). Unsupported image MIME types such as SVG are saved to disk (2.1.144). Images are no longer dropped when `structuredContent` is present (fixed 2.1.128, [#54737](https://github.com/anthropics/claude-code/issues/54737), 2026-05-02). Before these fixes, base64 images counted as text and hit the 25k limit ([#9152](https://github.com/anthropics/claude-code/issues/9152), 2025-10; [#31208](https://github.com/anthropics/claude-code/issues/31208), 2026-03). A user there found that the image was lost when the tool had an `outputSchema`. [R, now fixed]
- **PDF / binary blobs** [C]: 2.1.69: "tools returning PDFs, Office documents, or audio now save decoded bytes to disk with the correct file extension instead of dumping raw base64 into the conversation context." The model sees `[Resource from <server> at <uri>] Binary content (<mimeType>, <size>) saved to <path>` ([#72271](https://github.com/anthropics/claude-code/issues/72271)). Maintainer, 2026-08-17: "PDFs, audio, and other binary types are still saved to disk with a placeholder line, so the request for `document`/audio forwarding is a real gap." Since the built-in `Read` tool attaches PDFs as `document` blocks (same issue), the model *can* follow up with `Read <path>` and get the PDF natively, at the cost of one extra tool call. [I]
- **resource_link** [C/R]: support added in 1.0.44 ([#2826](https://github.com/anthropics/claude-code/issues/2826), 2025-07-07). In practice the model calls the MCP resource-read tool, and in 2025 that pulled the base64 resource in as text and filled the context ([#6099](https://github.com/anthropics/claude-code/issues/6099), 2025-08-19, closed stale). [R] Current behaviour for a `resources/read` that returns an image or PDF blob: [?] (probably the same disk-save path [I]).
- **Limits** [C]: warning at 10,000 tokens; default cap 25,000 tokens (`MAX_MCP_OUTPUT_TOKENS`); a successful result without images that exceeds the cap is written to a file; text over 50,000 chars is written to a file unless the tool declares `_meta["anthropic/maxResultSizeChars"]` (ceiling 500,000) in `tools/list`; "Tools that return image data are still subject to `MAX_MCP_OUTPUT_TOKENS`"; HTTP/SSE response body capped at 16 MiB; error text over ~11,000 chars is trimmed to head and tail.

### 2.4 Claude mobile apps

- The connectors docs say remote connectors work on mobile ([overview](https://claude.com/docs/connectors/overview), read 2026-10-26), and that "MCP tool calls are proxied through Claude's backend" ([MCP Apps troubleshooting](https://claude.com/docs/connectors/building/mcp-apps/troubleshooting.md)). [C] So the content handling is most likely the same as claude.ai web. [I] Supporting data points: the image one-turn delay was reproduced on iOS (comment on [#731](https://github.com/anthropics/claude-ai-mcp/issues/731), 2026-08-02), and the voice-mode resource drop on the iPhone app ([#972](https://github.com/anthropics/claude-ai-mcp/issues/972)). [R] No mobile-specific documentation. [?]

### 2.5 Hermes Agent (Nous Research)

Source: [`tools/mcp_tool_content.py`](https://github.com/NousResearch/hermes-agent/blob/main/tools/mcp_tool_content.py) (last commit 2026-10-08) and [docs `user-guide/features/mcp.md`](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/features/mcp.md), both read 2026-10-26. [C]

- **image**: cached to disk and reported as `MEDIA:/path`. "When the main model can see images inside tool results, the image itself is attached to the result too" (gated by `agent.image_input_mode`, `auxiliary.vision`, the model's vision capability and provider support). At most 4 native images per result (`_MCP_NATIVE_IMAGE_MAX = 4`), resized to `vision.embed_target_bytes` / 1568 px long edge / JPEG "because they are re-sent on every later turn". Identical pixels already in context are refused (`vision.max_calls_per_image`, keyed on the resized data URL), which is a built-in de-duplication.
- **Embedded blob (PDF)**: decoded (cap 50 MB) into the document cache; the model gets `[MCP resource saved to <path> (<mime>, <n> bytes) — read it with read_file or terminal tools]`. **No document part.**
- **Embedded text**: inlined as text.
- **resource_link**: not fetched; `[MCP resource link: uri=…, name=…, mimeType=… — fetch it with <server>_read_resource]`.
- Unsupported blocks become `[MCP content dropped: unsupported block (…)]` rather than vanishing silently.
- Whether `<server>_read_resource` returns a PDF blob as a document part: [?] (probably the same disk-cache path [I]).

### 2.6 ChatGPT / OpenAI connectors

- [Apps SDK reference](https://developers.openai.com/apps-sdk/reference) (read 2026-10-26): `content` and `structuredContent` are "Surfaced to the model and the component"; `_meta` is "Delivered only to the component. Hidden from the model." It does not say which content item types are supported. [C]
- **image**: a connector tool returning an MCP image block shows `{}`. OpenAI Support (2026-04-22): an "integration-surface mismatch rather than an invalid MCP tool result … connector output is not guaranteed to become model-visible vision input". Suggested workaround: return a text summary and/or `structuredContent`, or call the Responses API with the image yourself ([community thread 1375446](https://community.openai.com/t/chatgpt-connector-returns-empty-for-mcp-tool-results-with-type-image-while-same-tool-works-in-mcp-inspector/1375446), 2026-03-02 → 2026-04-22). [C] An older thread (2025-09-29) reports image and resource_link both failing; it was closed 2026-08-09 without a fix ([thread 1360430](https://community.openai.com/t/image-responses-from-mcp-tools-do-not-work/1360430)). [R]
- **image in Code Mode** (added 2026-10-27): since about 2026-09-18, an MCP tool returning `type: "image"` (base64 JPEG) gets "Image output is not supported." in ChatGPT Web / Code Mode, and the model gets no pixels; text from the same tool still arrives. With the same MCP server, Codex CLI 0.153.4 describes the image correctly. A 284-byte JPEG fails the same way, so it is not the size. No OpenAI reply ([community thread 1399319](https://community.openai.com/t/regression-code-mode-image-output-now-returns-image-output-is-not-supported/1399319), 2026-09-20; [codex#46927](https://github.com/openai/codex/issues/46927), opened 2026-09-21, open, labels `bug`, `codex-web`, `tool-calls`). [R]
- **image in an Apps SDK component** (added 2026-10-27): a ~3 KB PNG shows in the component, a valid ~3 MiB PNG gives only a text answer and no component. The author found no threshold; whether the model itself sees either image is not established. No OpenAI reply ([community thread 1399928](https://community.openai.com/t/mcp-tool-returns-an-image-successfully-but-chatgpt-shows-only-text-no-image-or-widget/1399928), 2026-09-22). [R] An Apps SDK widget showing an image to the user does not make it vision input for the model. [I]
- **resource_link** (added 2026-10-27): a `resource_link` to a 10 MB ZIP (`mcp-file://` URI) used to be copied into the sandbox as `/mnt/data/<name>.zip`; in the newer ChatGPT environment `resources/read` fails with `Mcp error: -32002: Unknown resource`, no file is created. No OpenAI reply ([community thread 1409031](https://community.openai.com/t/regression-mcp-binary-file-materialization-fails-in-the-new-chatgpt-environment/1409031), 2026-10-23). [R] So ChatGPT did fetch resource links into its sandbox at some point, but not reliably now, and a file in the sandbox is not vision input either. [I]
- **The only documented vision path** is OpenAI's Responses API (`input_image` by URL, base64 data URL or file id), i.e. outside a connector. [C]
- PDF resources, size limits: [?]
- **Conclusion (2026-10-27):** a standard MCP `image` part does not reach the ChatGPT model as an image, in two separate ways (`{}` in connectors, "Image output is not supported" in Code Mode), while the same server works in Claude, Cursor, VS Code and OpenAI's own Codex, which hands it to the same models through the Responses API (§2.12). This is a ChatGPT limitation, not a fault of the server's results. [I, from the sources above] A Kumbu² server should therefore not bend its results for ChatGPT, but keep the text block (§5.1) so ChatGPT still gets the name, size and dimensions.

### 2.7 Cursor

- [Cursor MCP docs](https://cursor.com/docs/context/mcp) (read 2026-10-26): "MCP servers can return images … Return them as base64 encoded strings" and "Cursor attaches returned images to the chat. If the model supports images, it analyzes them." [C] Forum reports: CLI not seeing images ([thread](https://forum.cursor.com/t/cursor-cli-cant-parse-mcp-image-responses/135764)); a UI regression where the model still gets the image but the chat no longer shows it ([thread](https://forum.cursor.com/t/regression-mcp-tool-image-content-no-longer-renders-in-agent-chat-model-receives-it-ui-does-not/168824)). [R]
- Resources, PDFs, limits: not documented. [?]

### 2.8 VS Code (GitHub Copilot)

Source: [`src/vs/workbench/contrib/mcp/common/mcpLanguageModelToolContribution.ts`](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/contrib/mcp/common/mcpLanguageModelToolContribution.ts) (last commit 2026-06-24, read 2026-10-26). [C, code]

- `image` / `audio`: decoded, images resized, pushed as a `data` part for the model.
- `resource_link` with an image MIME type: **VS Code reads the resource itself** and inlines the bytes as a data part. Other MIME types: a JSON reference part (`McpToolResourceLinkMimeType`) with the URI.
- Embedded `resource` with an image MIME type and `blob`: inlined like an image. Any other blob (PDF included): a linked-resource reference to a chat permalink, **not** the bytes.
- `annotations.audience` is respected: parts without `assistant` in the audience are **not sent to the model**.
- **Text blocks are not sent to the model when `structuredContent` is present** (structuredContent is sent as JSON text instead). This matters if Kumbu² ever adds `structuredContent`.
- Whether the Copilot model backend passes data parts on as real vision input: likely for vision models [I]; not checked.

### 2.9 Open WebUI

- Images: until September 2026 the model did not see MCP images, only the UI did ([#30327](https://github.com/open-webui/open-webui/issues/30327)). Fixed by PR [#30358](https://github.com/open-webui/open-webui/pull/30358) "pass MCP tool images to the model, not only to the UI", merged 2026-09-22. [C]
- Non-image blobs: "an MCP tool returning an embedded resource with a binary blob … is turned into the text `[Resource: …] (binary data, mimeType: …)`. The bytes are gone." The reporter's table (v0.12.3) shows the model gets "text + image" for `ImageContent`. The maintainer reproduced it on 2026-10-24 and said that delivering other file types "is a capability that does not exist yet" ([#33075](https://github.com/open-webui/open-webui/issues/33075)). [C]
- resource_link, limits: [?]

### 2.10 LibreChat

Source: [`packages/api/src/mcp/parsers.ts`](https://github.com/LibreChat-AI/LibreChat/blob/main/packages/api/src/mcp/parsers.ts) (last commit 2026-09-24, read 2026-10-26). [C, code]

- For recognised providers (`anthropic`, `openai`, `google`, `bedrock`, `openrouter`, …): `image` and embedded image blobs become `image_url` artefacts, "merged back by the agents package". For any other provider, the whole result is stringified.
- Non-image blobs: "summarized rather than emitted, so they never reach the model as base64"; a NUL byte marks a payload as binary, which in practice covers PDFs. [C/I]
- `resource_link`: described as text (`describeResourceLink`), not fetched.
- Image limit: `MCP_IMAGE_DATA_MAX_BYTES`, default 10 MB.
- History: [discussion #9961](https://github.com/danny-avila/LibreChat/discussions/9961) reported images as raw base64 text at about 214k tokens (older). The current code no longer does this for recognised providers. [R/C]

### 2.11 Extra: Anthropic Messages API MCP connector (`mcp_servers`)

Not a chat client, but relevant if anyone wires Kumbu² in through the API: image blocks are replaced with the text `[MCP tool returned an image (format: image/png) but the Anthropic API doesn't currently support image responses]` ([claude-ai-mcp#969](https://github.com/anthropics/claude-ai-mcp/issues/969), 2026-09-01, open). [R]

### 2.12 Codex (OpenAI's coding agent)

Source: `convert_mcp_content_to_items` in [`codex-rs/protocol/src/models.rs`](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/models.rs) (last commit 2026-10-17, read 2026-10-27; latest release `rust-v0.164.0`). [C, code]

Codex is the MCP client itself and talks to the Responses API directly, so it decides how a tool result reaches the model:

- `text` → `input_text`. A text part with `_meta["codex/encryptedContent"]: true` is passed on as encrypted content.
- `image` → `input_image` with a data URL (`data:<mimeType>;base64,<data>`; data that already starts with `data:` is kept as it is) inside the `function_call_output`. The detail level comes from `_meta["codex/imageDetail"]` (`auto`, `low`, `high`, `original`), default `high`.
- `audio` → `input_audio`, likewise as a data URL.
- Everything else, including embedded `resource` (with a PDF blob) and `resource_link`, is unknown to this parser and **serialised as JSON text**: an embedded PDF reaches the model as base64 text, the costly case §3 warns about. [C, code; the cost is I]

Why this matters for ChatGPT (§2.6): the model can take images in tool output, and OpenAI's own client delivers them; the reporter of [codex#46927](https://github.com/openai/codex/issues/46927) confirms it hands-on with Codex CLI 0.153.4. What drops the image in ChatGPT is the layer between a connector and the chat (hosted by OpenAI), not the model and not the MCP server. [I, from the code and the report]

For Kumbu² v2.5: `_meta["codex/imageDetail"]: "low"` would make thumbnails cheap on Codex; other clients ignore the key. Do not use embedded resources for anything large, since Codex would put the whole base64 into the context. [I]

---

## 3. Anthropic Messages API: what a client *could* map to

From [Handle tool calls](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls) (read 2026-10-26) [C]:

> `content` (optional): … a string …, a list of nested content blocks …, or a list of document blocks … These content blocks can use the `text`, `image`, `document`, or `search_result` types.

So a client can legally map:

| MCP | Messages API block inside `tool_result` |
|---|---|
| `image` (jpeg/png/gif/webp) | `image` (`source.type: base64 \| url \| file`) |
| embedded `resource` with `application/pdf` blob | `document` (`source.type: base64`, `media_type: application/pdf`) |
| embedded text / markdown | `text`, or `document` with `source.type: text` (enables citations) |
| search-like results | `search_result` |

Limits that apply [C]:

- Images ([Vision](https://platform.claude.com/docs/en/build-with-claude/vision)): JPEG, PNG, GIF, WebP; max 8000×8000 px; 10 MB base64 per image on the API (5 MB on Bedrock, 10 MB on claude.ai); 20 per message on claude.ai, 100–600 per API request; above 20 images a stricter per-image dimension limit applies, and "images nested inside `tool_result` content" count. Cost = ⌈w/28⌉×⌈h/28⌉ visual tokens, capped at 1,568 (standard) or 4,784 (high-res tier, Claude 4.7+, 2,576 px long edge). Oversized images are downscaled.
- PDFs ([PDF support](https://platform.claude.com/docs/en/build-with-claude/pdf-support)): request size 32 MB; max 600 pages (100 if context < 1M tokens); no encrypted PDFs; ~1,500–3,000 text tokens per page **plus** the image cost of each page (pages are rasterised).
- Both can be referenced by Files API `file_id` instead of base64 to avoid resending bytes each turn. Images and documents can be prompt-cached.
- The token comparison that motivates the design: a 1000×1000 image is 1,296 visual tokens as an `image` block, versus roughly 15,000–25,000+ tokens when the same base64 is fed in as text (user measurements in [claude-code#31208](https://github.com/anthropics/claude-code/issues/31208)). [C for the first number, R for the second]

---

## 4. Conditional fetch / "not modified" and avoiding duplicate large results

**In the spec:**

- 2026-07-28 adds `ttlMs` and `cacheScope` (`CacheableResult`, SEP-2549) — but **only** for `server/discover`, `tools/list`, `prompts/list`, `resources/list`, `resources/templates/list` and **`resources/read`**, **not** `tools/call` ([Caching](https://modelcontextprotocol.io/specification/2026-07-28/server/utilities/caching)). It is purely TTL-based (like `Cache-Control: max-age`); there is no ETag, no validator, no `If-None-Match`/304 equivalent. [C] The SEP-2549 text does not mention ETags. [C, by absence]
- Resource `annotations.lastModified` exists and could carry a version marker, but no conditional-request semantics are attached to it. [C]
- New non-normative "Stateful Tools" guidance (2026-07-28 tools page): with sessions gone, cross-call state should use "an explicit handle from a creation tool and accepting that handle as an argument on subsequent calls". [C] A version/ETag argument on a read tool fits this pattern (the model carries the token forward), but **no established MCP convention for it was found**. [I/?]

**In clients:**

- No client found that de-duplicates identical tool results by itself, **except** Hermes: `vision.max_calls_per_image` refuses to re-attach an image whose resized pixels are already in context and tells the model so. [C]
- Claude Code / Hermes write large or binary results to disk and hand the model a path. That is de facto "send once, refer by path" on those two clients. [C]
- Anthropic's own guidance for hosted connectors: "return identifiers or previews in the initial result and provide a separate tool to retrieve the full content when needed" ([MCP Apps troubleshooting](https://claude.com/docs/connectors/building/mcp-apps/troubleshooting.md)). [C]
- At API level the equivalent mechanisms are Files API `file_id` and prompt caching. MCP has no way to hand a client a `file_id`. [C/I]

**Conclusion for the pattern:** there is no standard, so it has to be designed server-side. The natural shape is a `version`/`etag` argument on the attachment-read tool that returns a short text "unchanged since version X, already in this conversation" instead of the bytes. Whether the model reliably passes it: [?], needs a hands-on test. [I]

---

## 5. Consequences for the Kumbu² v2.5 design (my assessment, [I])

1. **Images: use the top-level `image` content type, never base64 text and never an embedded image blob as the primary path.** `image` is the only binary type that reaches the model natively on all Anthropic surfaces, Hermes, VS Code, Cursor, Open WebUI and LibreChat. Downscale server-side (≤ 1568 px long edge, or 2576 px for high-res models; JPEG/WebP), because clients re-send the image each turn and Claude Code still counts images against 25k tokens. Always include a short `text` block alongside (ChatGPT drops the image, voice mode drops resources), and do not set `audience: ["user"]` on parts the model must see.
2. **PDFs have no portable native path today.** No MCP client maps an embedded `application/pdf` blob to a `document` block. Claude Code and Hermes save it to disk (the model can then read it, on Claude Code even natively via `Read`). claude.ai/Desktop currently reject such results outright (`invalid_union`), and Open WebUI/LibreChat drop the bytes. Design: a PDF read returns (a) extracted text per page range as `text` (works everywhere), and/or (b) pages rendered as `image` parts (native everywhere `image` works; Claude Code's own maintainer names this as the workaround). Keep the embedded-blob path optional and test it per client; track claude-ai-mcp#1086 and the Claude Code "document forwarding" gap.
3. **Do not rely on `resource_link` or resources for delivery, and build "send once" yourself.** No major chat client fetches a `resource_link` automatically (Claude Code and Hermes leave it to the model, VS Code only for images, claude.ai not at all), and resource TTL caching does not cover `tools/call`. So: list/metadata tools return ids, sizes, page counts and a content hash; the read tool takes an explicit page/region selection plus an optional `ifNoneMatch`-style hash, and answers "unchanged" with text. Stay well under ~150k chars and 25k tokens per result (page through large documents). There is no client capability to detect support, so do not branch on `clientInfo` except as a last resort.

---

## 6. A test for v2.5: does the model really see the image?

Proposed on 2026-10-27 (idea from a ChatGPT session). Metadata and an honest-sounding answer can look like vision ("Here is your image" with a plausible description), so the test needs something only the pixels carry:

1. Generate an image with a random six-digit code drawn into it. The code appears nowhere else: not in the file name, the note, the text block, alt text or any metadata.
2. Let the client fetch it through the attachment-read tool, then ask the model for the code.
3. Pass = the exact code. A wrong or refused code means the model did not get the pixels, whatever it says about the image.

Run it per client (claude.ai, Desktop, Claude Code, ChatGPT connector and Code Mode, Le Chat, Cursor, VS Code), with a small image and one near the size limit, and record the results here. Generate a new code for every run, so an earlier answer in the context can't give it away.

