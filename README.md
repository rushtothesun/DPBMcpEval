# DPBMcpEval
An mcp plugin for DreamPoeBot allowing AI Agents to interact with DPB.

Based on https://github.com/yuridevx/ExportGlobals

Requires: https://github.com/yuridevx/csharp-dll-mcp

Example config in Google Antigravity:

```
{
  "mcpServers": {
    "dreampoebot-reflector": {
      "command": "C:/Path/To/McpNetDll/bin/Release/net9.0/McpNetDll.exe",
      "args": [
        "C:/Path/To/DreamPoeBot/DreamPoeBot.exe"
      ],
      "disabled": false
    },
    "dreampoebot-eval": {
      "command": "node",
      "args": [
        "C:/Path/To/mcp_http_bridge_dpb.js"
      ],
      "disabled": false
    }
  }
}
```

Google Antigravity doesn't support mcp http so we have to use a bridge.
<details>
<summary><b>Click to expand the dreampoebot_bridge.js script</b></summary>

<br>

```javascript
#!/usr/bin/env node
const http = require('http');
const fs = require('fs');

const targetUrl = 'http://localhost:5100/mcp';
const logFile = 'C:/Users/YOURNAME/.gemini/antigravity/mcp_bridge_dpb.log';

let buffer = '';
let sessionId = null;

function log(msg) {
    fs.appendFileSync(logFile, `[${new Date().toISOString()}] ${msg}\n`);
}

log("DreamPoeBot MCP bridge started");

process.stdin.setEncoding('utf8');

process.stdin.on('data', (chunk) => {
    buffer += chunk;

    let lines = buffer.split('\n');
    buffer = lines.pop(); // Keep the incomplete line in the buffer

    for (const line of lines) {
        if (!line.trim()) continue;

        try {
            const parsed = JSON.parse(line);
            log(`IN: ${line}`);

            // Intercept the initialized notification to prevent MethodNotFound errors.
            if (parsed.method === "notifications/initialized") {
                log("Intercepted and ignored initialized notification.");
                continue;
            }

            sendHttpRequest(line);
        } catch (e) {
            log(`Error parsing input JSON: ${e.message}`);
        }
    }
});

function sendHttpRequest(jsonStr) {
    const url = new URL(targetUrl);
    const headers = {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(jsonStr)
    };

    if (sessionId) {
        headers['Mcp-Session-Id'] = sessionId;
    }

    const options = {
        hostname: url.hostname,
        port: url.port,
        path: url.pathname + url.search,
        method: 'POST',
        headers: headers
    };

    const req = http.request(options, (res) => {
        let responseBody = '';
        res.setEncoding('utf8');

        const newSessionId = res.headers['mcp-session-id'];
        if (newSessionId) {
            sessionId = newSessionId;
            log(`Session ID updated: ${sessionId}`);
        }

        res.on('data', (chunk) => {
            responseBody += chunk;
        });

        res.on('end', () => {
            log(`OUT: ${responseBody}`);
            if (responseBody) {
                process.stdout.write(responseBody + '\n');
            }
        });
    });

    req.on('error', (e) => {
        log(`Problem with request: ${e.message}`);
    });

    req.write(jsonStr);
    req.end();
}
```
</details>
<details>
<summary><b>AI Agent Instructions</b></summary>

<br>

# DreamPoeBot MCP Integration Guide for AI Agents

**Critical Context for AI Assistants**: If you are trying to write DreamPoeBot plugins, automate gameplay, or read game memory via DreamPoeBot, you MUST follow these guidelines. This document summarizes the architecture of the McpEval MCP integration and its limitations.

---

## 1. Dual Server Architecture

The DreamPoeBot environment exposes TWO distinct MCP servers defined in your `mcp_config.json`:

1.  **`dreampoebot-eval` (The Live Game Execution Server)**
    *   **Role**: Executes raw C# code inside the running DreamPoeBot process with full access to `LokiPoe`, `BotManager`, and all game APIs.
    *   **Transport**: The McpEval plugin runs an HTTP JSON-RPC server on `http://localhost:5100/mcp`. Since Antigravity only supports stdio MCP transport, a Node.js bridge script (`mcp_http_bridge_dpb.js`) translates between stdio and HTTP.
    *   **Tools**: Exposes one tool: `execute` (compile and run C# scripts). The script must define a public class with a `public object Execute()` method.
    *   **Compiler**: Uses `RoslynCodeCompiler.CreateLatestCSharpProvider()` (CodeDom API on .NET Framework 4.8), referencing all assemblies loaded in the AppDomain.

2.  **`dreampoebot-reflector` (The Static Types Analysis Server)**
    *   **Role**: A background .NET process (`McpNetDll.exe`) that statically parses `DreamPoeBot.exe` using `MetadataLoadContext` over standard stdio.
    *   **Purpose**: Use this to discover what DreamPoeBot's C# classes (e.g., `LokiPoe`, `Monster`, `Item`) look like structurally before you write a script for the live environment.

---

## 2. Reflector Tool Best Practices (CRITICAL)

The DreamPoeBot `.exe` contains thousands of internal types.

### Available Reflector Tools

| Tool | Best For | Notes |
|---|---|---|
| `SearchByKeywords` | Natural-language searches | Fast (~80ms). Scope with `searchScope` (e.g., `types`, `properties`) to avoid large result sets for common terms. |
| `SearchElements` | Precise regex pattern matching | Best for finding exact type names. Scope to `types` to filter noise. |
| `ListNamespaces` | Browsing the type hierarchy | Explore architecture by namespace, like an IDE's Solution Explorer. |
| `GetTypeDetails` | Full API blueprint for a known type | Use after finding a type name to see all its Properties, Methods, and Fields. |

---

## 3. Example Workflow

1.  **Hypothesize**: "I need to find the player's current health."
2.  **Search Static Types**: Use `SearchElements` restricted to `types` to search for `Life` or `Health`.
3.  **Inspect Structure**: Send the type name through `GetTypeDetails` to read its C# signature.
4.  **Execute Live**: Use `mcp_dreampoebot-eval_execute` with a script referencing those exact properties:
    ```csharp
    using DreamPoeBot.Loki.Game;

    public class Script
    {
        public object Execute()
        {
            if (!LokiPoe.IsInGame) return new { Error = "Not in-game" };

            var me = LokiPoe.Me;
            return new {
                PlayerName = me.Name,
                CurrentHealth = me.Health,
                MaxHealth = me.MaxHealth
            };
        }
    }
    ```
5.  **Receive**: Get live JSON results from the game memory.

---

## 4. Global Entry Points (No Wrapper Needed)

DreamPoeBot's core game state is instantly accessible via global static entry points (no need to pass in or instantiate a `GameController` wrapper):

| Entry Point | Description |
|---|---|
| `LokiPoe.IsInGame` | Check if in game |
| `LokiPoe.IsInLoginScreen` | Check if on login screen |
| `LokiPoe.Me` | The player **object instance** |
| `LokiPoe.InGameState.*` | Stash, inventory, skills, chat, UI panels |
| `LokiPoe.ObjectManager.*` | Queries returning **instances** of entities, monsters, items |
| `BotManager.*` | Bot state, current bot, enabled plugins |
| `ExilePather.*` | Static pathfinding calculations |
| `PluginManager.*` | Enabled plugins, plugin lookup |

If you are reading existing DreamPoeBot plugin source code (e.g., FollowBot), the same `LokiPoe.*` and `BotManager.*` references work identically in MCP scripts.

---

## 5. ProcessHookManager Safety (CRITICAL)

If your script interacts with the game (mouse clicks, movement, keystrokes), you **MUST** wrap the interaction with:

```csharp
LokiPoe.ProcessHookManager.Enable();
try
{
    // game interaction code here
}
finally
{
    LokiPoe.ProcessHookManager.Disable();
}
```

**FAILURE TO CALL `Disable()` WILL PERMANENTLY LOCK THE CHARACTER FROM ALL FUTURE INPUT** until DreamPoeBot is restarted. Always use a `try/finally` block.

For read-only queries (checking game state, reading entity data), no hook management is needed.

---

## 6. Key Namespaces

| Namespace | Contents |
|---|---|
| `DreamPoeBot.Loki.Game` | `LokiPoe` (main entry point), game objects, game data |
| `DreamPoeBot.Loki.Game.Objects` | `Monster`, `Chest`, `Item`, `AreaTransition`, etc. |
| `DreamPoeBot.Loki.Bot` | `BotManager`, `PluginManager`, `Blacklist`, etc. |
| `DreamPoeBot.Loki.Bot.Pathfinding` | `ExilePather` |
| `DreamPoeBot.Loki.Common` | `Logger`, `Configuration` |
| `DreamPoeBot.BotFramework` | `KeyManager`, `MouseManager` |

---

## 7. Known Issues
*   DreamPoeBot is **.NET Framework 4.8** — do not use C# features beyond C# 7.3 (no records, no `is not`, no file-scoped namespaces, etc.).
*   Compiled script assemblies stay in the AppDomain permanently (no `AssemblyLoadContext` unloading). Avoid running thousands of scripts without restarting DreamPoeBot.
```
