# Connect your AI

Any AI client that speaks MCP can use Kumbu². There are two ways in, both on the **Access** page (drawer → Access):

- a **connection**, for apps that sign in through your browser (this is called OAuth);
- an **API key**, for agents that run on your own machine.

Give each AI its own connection or key, named after it, with only the vaults it needs. What your AI can do once it is in: [[Your AI in Kumbu²]].

Your Kumbu² address is {{server}}: the address you open Kumbu² at (in a browser, the one in the address bar).

## This is how you connect Claude (claude.ai, Desktop and mobile)

1. In Kumbu², open the drawer → Access → **Connect an app**.
2. Give it a name, choose the vaults and the rights for each (none, read, or read and write) and, if you like, an expiry.
3. Copy the connection's own address that Kumbu² shows.
4. In claude.ai, open Settings → Connectors → Add custom connector, paste the address, and leave client ID and secret empty.
5. Kumbu²'s consent page shows what is being connected. Choose **Allow**.

A connector added on claude.ai also works in Claude Desktop and in the mobile apps.

*Tested.*

## This is how you connect ChatGPT

ChatGPT on the web and the ChatGPT desktop app connect differently.

**On the web (chatgpt.com):**

1. In Kumbu², open the drawer → Access → **Connect an app**, as for Claude above, and copy the connection's own address.
2. In ChatGPT, add a custom connector with that address and set its authentication to **DCR** (dynamic client registration). With the default setting, signing in doesn't work.
3. Kumbu²'s consent page shows what is being connected. Choose **Allow**.

**In the desktop app:** signing in (OAuth) doesn't work there; use an API key instead.

1. In Kumbu², open the drawer → Access → **New key**, choose its vaults and rights, and copy the key: it is shown only once.
2. In the app, add the connector with the address `{{server}}/mcp`, and add a **custom header** named `Authorization` with the value `Bearer kumbu_…` (your key in place of `kumbu_…`). Leave the app's own field for a token empty: the header is what works.

*Tested.*

## This is how you connect Claude Code

1. In Kumbu², open the drawer → Access → **New key**. Choose its vaults and rights, and copy the key: it is shown only once.
2. In a terminal, run:

```
claude mcp add --transport http kumbu {{server}}/mcp --header "Authorization: Bearer kumbu_…"
```

with your key in place of `kumbu_…`.

*Tested.*

## This is how you connect any other MCP client

- With an API key: streamable HTTP at `{{server}}/mcp`, with the header `Authorization: Bearer <your key>`.
- With a connection: the connection's own address, if your client supports signing in to MCP servers (OAuth).

*Tested with Grok and Mistral (a connection).*

## Good to know

- The plain address `{{server}}/mcp` takes API keys only; a connection's own address takes no key.
- A key or a connection is revoked on the Access page, and stops working at once.

Back to [[Start here]].
