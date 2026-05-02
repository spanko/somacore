# MCP servers for this repo

Recommended MCP servers for Claude Code working in `somacore`. Two servers, both removing real friction without adding rope.

## Servers

### 1. GitHub

Issues, PRs, cross-branch search, commit history. Use the official server:

- Package: `@modelcontextprotocol/server-github`
- Auth: GitHub personal access token (PAT) with `repo` scope, or fine-grained PAT scoped to `spanko/somacore` and any other repos you want exposed.

### 2. PostgreSQL (read-only)

Schema introspection and ad-hoc SELECT against the dev database. **Read-only is mandatory** — writes go through migrations.

- Package: `@modelcontextprotocol/server-postgres`
- Connection string points at the dev Postgres with a **read-only role**, not the admin role.

Create the read-only role before pointing the MCP server at it:

```sql
CREATE ROLE somacore_ro WITH LOGIN PASSWORD '...' CONNECTION LIMIT 5;
GRANT CONNECT ON DATABASE somacore TO somacore_ro;
GRANT USAGE ON SCHEMA public TO somacore_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO somacore_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO somacore_ro;
```

Store the password in your local secrets manager — not in the MCP config file in plaintext.

## Sample `~/.config/claude-code/mcp.json`

Adjust paths and credentials for your environment.

```json
{
  "mcpServers": {
    "github": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-github"],
      "env": {
        "GITHUB_PERSONAL_ACCESS_TOKEN": "${env:GITHUB_PAT}"
      }
    },
    "postgres-somacore-dev": {
      "command": "npx",
      "args": [
        "-y",
        "@modelcontextprotocol/server-postgres",
        "${env:SOMACORE_DEV_PG_RO_URL}"
      ]
    }
  }
}
```

Then export the env vars in your shell profile:

```bash
export GITHUB_PAT="ghp_..."
export SOMACORE_DEV_PG_RO_URL="postgresql://somacore_ro:...@somacore-dev-pg.postgres.database.azure.com:5432/somacore?sslmode=require"
```

## Servers we deliberately skipped

- **Azure CLI / Azure MCP server.** Microsoft has one in preview; the Azure CLI is right there in the terminal and Claude Code can call it directly. Adopt the dedicated server when we feel the pain.
- **App Insights / Log Analytics MCP.** Same logic — useful when debugging, not load-bearing for setup. Worth revisiting once we have real traffic to debug.
- **Web search / browser MCPs.** Documentation lookup is fine, but the signal-to-noise hurts more than it helps for code work; CLAUDE.md and the docs in this repo should be the primary source of truth.
- **A WHOOP MCP server.** Doesn't exist; building one wraps a small REST API that we'll call directly anyway. Premature.

## Validating the setup

After configuring, in a Claude Code session:

```
/tools
```

You should see `github__*` and `postgres-somacore-dev__*` tools listed. If not, check the MCP server logs (Claude Code prints them on startup with `--mcp-debug`).

Quick smoke tests:

- "List the open issues on this repo."
- "What columns does the `users` table have?"

Both should work without further prompting.
