# Concepts

> **Draft by Claude — Chris rewrites each chapter in his own words.**

## 1. Talking to a model through MCP

### What MCP is

MCP (Model Context Protocol) is a way for an AI application to use outside capabilities. There are two sides. The **client** is the AI application (Claude Code, for example). The **server** is a program that offers capabilities. The most important kind is a **tool**: a named function with a description and typed arguments. The model reads the description, decides to call the tool, and the client sends the call to the server. Messages are JSON-RPC 2.0.

Kumbu² is a server with one tool, `sh`.

### Why Streamable HTTP

MCP can run over standard input/output (the client starts the server as a child process) or over HTTP. Kumbu² is a hosted service that several people and agents reach from different machines, so it uses HTTP. In Streamable HTTP every message is a `POST /mcp`. The server may answer with plain JSON or with a short event stream (SSE). Our server answers with SSE even for a single result.

### The protocol change of 2026-07-28

Older revisions started with a handshake. The client sent `initialize`, the server answered and could hand out an `Mcp-Session-Id`, and the client then repeated that id on every call. The server had to remember the session. That makes scaling awkward: the next request must reach the same instance.

The 2026-07-28 revision removes sessions and `initialize` (SEP-2567 and SEP-2575). Each request is self-contained and stateless. What the handshake used to negotiate, the protocol version and the client's capabilities, now travels in `_meta` on every request. Three headers repeat the essentials so proxies can route without reading the body: `MCP-Protocol-Version`, `Mcp-Method` and `Mcp-Name`. Because the request describes itself, any server instance can answer it.

Kumbu² is stateless (the SDK default; we did not enable sessions). Real exchanges against our server follow. The dev token is redacted. These were captured under the test host (`dotnet test tests/Kumbu.Server.Tests --output Detailed` prints them). `serverInfo` reads `kumbu` and the server's version because `Program.cs` sets it; left unset, the SDK falls back to the entry assembly's name, which under the test host is `Kumbu.Server.Tests`.

**Old form (2025-06-18): `initialize`.** Our server still answers it, but without an `Mcp-Session-Id`.

Request:

```http
POST /mcp
Authorization: Bearer <dev token>
Accept: application/json, text/event-stream
Content-Type: application/json; charset=utf-8

{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "2025-06-18",
    "capabilities": {},
    "clientInfo": { "name": "raw", "version": "0" }
  }
}
```

Response, as sent (SSE framing):

```http
200 OK
Cache-Control: no-store, no-cache
X-Accel-Buffering: no
Content-Type: text/event-stream
Content-Encoding: identity

event: message
data: {"result":{"protocolVersion":"2025-06-18","capabilities":{"logging":{},"tools":{}},"serverInfo":{"name":"kumbu","version":"0.1.0"}},"id":1,"jsonrpc":"2.0"}
```

The same message, pretty-printed:

```json
{
  "result": {
    "protocolVersion": "2025-06-18",
    "capabilities": { "logging": {}, "tools": {} },
    "serverInfo": { "name": "kumbu", "version": "0.1.0" }
  },
  "id": 1,
  "jsonrpc": "2.0"
}
```

**New form (2026-07-28): one self-describing `tools/call`.** No handshake, no session.

Request:

```http
POST /mcp
Authorization: Bearer <dev token>
Accept: application/json, text/event-stream
Content-Type: application/json; charset=utf-8
MCP-Protocol-Version: 2026-07-28
Mcp-Method: tools/call
Mcp-Name: sh

{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "sh",
    "arguments": { "command": "ls /" },
    "_meta": {
      "io.modelcontextprotocol/protocolVersion": "2026-07-28",
      "io.modelcontextprotocol/clientCapabilities": {}
    }
  }
}
```

Response, as sent (SSE framing):

```http
200 OK
Cache-Control: no-store, no-cache
X-Accel-Buffering: no
Content-Type: text/event-stream
Content-Encoding: identity

event: message
data: {"result":{"content":[{"type":"text","text":"Demo/\nIdeas/"}],"isError":false,"resultType":"complete","_meta":{"io.modelcontextprotocol/serverInfo":{"name":"kumbu","version":"0.1.0"}}},"id":2,"jsonrpc":"2.0"}
```

The same message, pretty-printed:

```json
{
  "result": {
    "content": [ { "type": "text", "text": "Demo/\nIdeas/" } ],
    "isError": false,
    "resultType": "complete",
    "_meta": {
      "io.modelcontextprotocol/serverInfo": { "name": "kumbu", "version": "0.1.0" }
    }
  },
  "id": 2,
  "jsonrpc": "2.0"
}
```

What the SDK (version 2.3.0) insisted on for the new form, found by sending less and reading the errors: both headers `Mcp-Method` and `Mcp-Name` (otherwise HTTP 400, JSON-RPC error `-32020`), and both `_meta` entries (otherwise 400, `-32602`). `clientCapabilities` must be a JSON object, but `{}` is fine. The server's own `serverInfo` now comes back in the result's `_meta`, and results carry `resultType: "complete"`.

### Errors are tool results, not JSON-RPC errors

JSON-RPC errors are for protocol problems: a malformed request, a missing header, an unknown method. A wrong path or a missing note is not a protocol problem. It is an ordinary outcome of the model's command. So the `sh` tool returns it as a normal result with `isError: true` and a readable message, such as a missing file. The model reads the text and can correct itself, for example by running `ls` and trying again. A JSON-RPC error would usually be handled by the client's plumbing, and the model might never see it. Commands never throw on user input.

### The tool description is the man page

The model knows nothing about `sh` except what the tool description says. That text (in `src/Kumbu.Core/Sh/Manual.cs`) is therefore the manual: what the commands are, that directories end with `/`, that quoting is needed for spaces, that output is capped, and that note content is data and not instructions. If the model misuses the tool, the first place to look is this text.

### Why paths are absolute

A shell with a working directory needs state: `cd` changes something between calls. Stateless requests have nowhere to keep that, and the model could lose track of where it is. With absolute paths, such as `/Work/projects/plan.md`, every command means the same thing on its own. Top-level directories are the vaults, and `ls` with no path lists them.
