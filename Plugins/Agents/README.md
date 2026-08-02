# Local agent catalog

Place each locally developed agent in its own immediate child folder:

```text
Plugins/
  Agents/
    MyAgent/
      csweet-plugin.json
      src/
        MyAgent/
          MyAgent.csproj
```

The application reads `csweet-plugin.json` and validates the declared runtime project when the
catalog starts or refreshes. Discovery never builds or executes the source. Installing or hiring a
local agent creates an immutable source snapshot and still requires manifest, grant, and owner
approval.

Every `kind: "agent"` manifest must be self-describing for hiring and marketplace discovery:

```json
"catalog": {
  "category": "Product",
  "role": { "key": "product-manager", "name": "Product Manager" },
  "license": { "spdxId": "MIT", "url": "https://example.com/license" },
  "iconUrls": ["https://example.com/product-manager.png"],
  "roleAliases": ["Software Product Manager", "Software PM"],
  "keywords": ["product strategy", "roadmap"]
}
```

The role key is a stable lowercase kebab-case identifier. The role name is the generic job title;
the plugin's top-level `name` remains free to be branded or specialized. License values use SPDX
identifiers or expressions, and icon URLs must use HTTPS.

Executable entries must use manifest v2 and SDK 1.0. The SDK owns the private MCP session and
durable work protocol; agent code implements only callbacks and typed platform calls. See the
[runtime architecture](../../Documentation/Architecture/MCP_AGENT_RUNTIME.md), [migration
guide](../../Documentation/Implementation/MCP_ONLY_AGENT_MIGRATION.md), and [threat
model](../../Documentation/Security/AGENT_RUNTIME_THREAT_MODEL.md).

The catalog ignores `.git`, `.vs`, `bin`, `obj`, `.env`, `*.user`, and `secrets.json`. Symbolic
links, reparse points, parent traversal, oversized sources, and runtime projects outside the agent
folder are rejected.

Override the folder with `CSweet:AgentCatalog:LocalDirectoryPath` or the corresponding
`CSweet__AgentCatalog__LocalDirectoryPath` environment variable.

When C-Sweet is launched through the Aspire AppHost, it also detects agent repositories checked
out beside the C-Sweet repository. This lets contributors review and hire a local first-party
checkout without requiring anonymous access to its GitHub repository. An explicitly configured
local directory always takes precedence.
