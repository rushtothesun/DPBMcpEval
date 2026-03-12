using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace McpEval
{
    /// <summary>
    /// HTTP server implementing MCP protocol with JSON-RPC 2.0.
    /// Adapted from ExportGlobals McpServer.cs for .NET Framework 4.8.
    /// </summary>
    public class McpHttpServer : IDisposable
    {
        private const string ProtocolVersion = "2024-11-05";
        private const string ServerName = "dreampoebot-eval";
        private const string ServerVersion = "1.0.0";
        private const int DefaultTimeoutSeconds = 30;

        private static readonly ILog Log = DreamPoeBot.Loki.Common.Logger.GetLoggerInstanceForType();

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _serverTask;
        private readonly ScriptCompiler _compiler = new ScriptCompiler();
        private readonly int _port;
        private string _sessionId;

        public bool IsRunning => _listener?.IsListening ?? false;
        public string Endpoint => $"http://localhost:{_port}/mcp";

        public McpHttpServer(int port = 5100)
        {
            _port = port;
        }

        public void Start()
        {
            if (IsRunning) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();

                _cts = new CancellationTokenSource();
                _serverTask = Task.Run(() => ListenAsync(_cts.Token));

                Log.Info($"[McpEval] MCP server started on {Endpoint}");
            }
            catch (Exception ex)
            {
                Log.Error($"[McpEval] Failed to start MCP server: {ex.Message}", ex);
                throw;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;

            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _serverTask?.Wait(TimeSpan.FromSeconds(2));
                Log.Info("[McpEval] MCP server stopped");
            }
            catch (Exception ex)
            {
                Log.Error($"[McpEval] Error stopping MCP server: {ex.Message}", ex);
            }
            finally
            {
                _listener?.Close();
                _listener = null;
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    var _ = Task.Run(() => HandleRequestAsync(context), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"[McpEval] Listener error: {ex.Message}", ex);
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            response.ContentType = "application/json";

            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers",
                "Content-Type, Accept, MCP-Protocol-Version, Mcp-Session-Id");

            try
            {
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? "/";
                if (path != "/mcp" && path != "/")
                {
                    response.StatusCode = 404;
                    response.Close();
                    return;
                }

                if (context.Request.HttpMethod != "POST")
                {
                    response.StatusCode = 405;
                    response.Close();
                    return;
                }

                string body;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    body = await reader.ReadToEndAsync();
                }

                JObject requestObj;
                try
                {
                    requestObj = JObject.Parse(body);
                }
                catch (JsonException)
                {
                    await SendJsonRpcErrorAsync(response, null, -32700, "Parse error");
                    return;
                }

                var id = requestObj["id"];
                var method = requestObj["method"]?.ToString();

                if (string.IsNullOrEmpty(method))
                {
                    await SendJsonRpcErrorAsync(response, id, -32600, "Invalid request");
                    return;
                }

                var result = await HandleMethodAsync(method, requestObj["params"], id);

                if (result == null)
                {
                    // Notification — no response expected.
                    response.StatusCode = 202;
                    response.Close();
                    return;
                }

                if (_sessionId != null)
                    response.Headers.Add("Mcp-Session-Id", _sessionId);

                await SendResponseAsync(response, result);
            }
            catch (Exception ex)
            {
                Log.Error($"[McpEval] Request error: {ex.Message}", ex);
                await SendJsonRpcErrorAsync(response, null, -32603, ex.Message);
            }
        }

        private async Task<object> HandleMethodAsync(string method, JToken paramsToken, JToken id)
        {
            switch (method)
            {
                case "initialize":
                    return HandleInitialize(id);
                case "initialized":
                    return null;
                case "tools/list":
                    return HandleToolsList(id);
                case "tools/call":
                    return HandleToolsCall(paramsToken, id);
                case "ping":
                    return CreateResponse(id, new Dictionary<string, object>());
                default:
                    return CreateErrorResponse(id, -32601, $"Method not found: {method}");
            }
        }

        private object HandleInitialize(JToken id)
        {
            _sessionId = Guid.NewGuid().ToString("N");

            return CreateResponse(id, new Dictionary<string, object>
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new Dictionary<string, object>
                {
                    ["tools"] = new Dictionary<string, object> { ["listChanged"] = false }
                },
                ["serverInfo"] = new Dictionary<string, object>
                {
                    ["name"] = ServerName,
                    ["version"] = ServerVersion
                }
            });
        }

        private object HandleToolsList(JToken id)
        {
            return CreateResponse(id, new Dictionary<string, object>
            {
                ["tools"] = GetToolDefinitions()
            });
        }

        private object HandleToolsCall(JToken paramsToken, JToken id)
        {
            var toolName = paramsToken?["name"]?.ToString();
            var arguments = paramsToken?["arguments"] as JObject;

            string text;
            bool isError;

            switch (toolName)
            {
                case "execute":
                    var code = arguments?["code"]?.ToString();
                    if (string.IsNullOrEmpty(code))
                    {
                        text = "No code provided.";
                        isError = true;
                    }
                    else
                    {
                        var result = _compiler.CompileAndExecute(code);
                        if (result.Success)
                        {
                            text = "Result: " + FormatObject(result.Result);
                            isError = false;
                        }
                        else if (result.IsCompilationError)
                        {
                            text = "Compilation failed:\n" + result.Error;
                            isError = true;
                        }
                        else
                        {
                            text = "Execution failed:\n" + result.Error;
                            isError = true;
                        }
                    }
                    break;
                default:
                    text = $"Unknown tool: {toolName}";
                    isError = true;
                    break;
            }

            return CreateResponse(id, new Dictionary<string, object>
            {
                ["content"] = new List<object>
                {
                    new Dictionary<string, object> { ["type"] = "text", ["text"] = text }
                },
                ["isError"] = isError
            });
        }

        private static string FormatObject(object obj)
        {
            if (obj == null) return "null";
            try
            {
                return JsonConvert.SerializeObject(obj, Formatting.Indented,
                    new JsonSerializerSettings
                    {
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                        MaxDepth = 5
                    });
            }
            catch
            {
                return obj.ToString();
            }
        }

        private static List<object> GetToolDefinitions()
        {
            return new List<object>
            {
                new Dictionary<string, object>
                {
                    ["name"] = "execute",
                    ["description"] =
                        "Execute C# code inside the running DreamPoeBot process with full access to the game API.\n\n" +
                        "## Code Structure\n" +
                        "Your code must define a public class (default name: 'Script') with a public `object Execute()` method.\n\n" +
                        "## CRITICAL: ProcessHookManager Safety\n" +
                        "If your script interacts with the game (mouse clicks, movement, keystrokes), you MUST wrap " +
                        "the interaction logic with `LokiPoe.ProcessHookManager.Enable();` at the start and " +
                        "`LokiPoe.ProcessHookManager.Disable();` at the end. Use a try/finally block to guarantee cleanup. " +
                        "FAILURE TO CALL Disable() WILL PERMANENTLY LOCK THE CHARACTER FROM ALL FUTURE INPUT " +
                        "until DreamPoeBot is restarted.\n\n" +
                        "## Logging\n" +
                        "Use `private static readonly ILog Log = Logger.GetLoggerInstanceForType();` for debug output. " +
                        "Requires `using log4net;` and `using DreamPoeBot.Loki.Common;`.\n\n" +
                        "## Available Global Entry Points (no wrapper needed)\n" +
                        "- `LokiPoe.IsInGame` — check if in game\n" +
                        "- `LokiPoe.IsInLoginScreen` — check if on login screen\n" +
                        "- `LokiPoe.Me` — the player object instance\n" +
                        "- `LokiPoe.InGameState.*` — stash, inventory, skills, chat, UI panels\n" +
                        "- `LokiPoe.ObjectManager.*` — queries returning instances of entities, monsters, items\n" +
                        "- `BotManager.*` — bot state, current bot, enabled plugins\n" +
                        "- `ExilePather.*` — static pathfinding calculations and navigation\n" +
                        "- `PluginManager.*` — enabled plugins, plugin lookup\n\n" +
                        "## Key Namespaces\n" +
                        "- `DreamPoeBot.Loki.Game` — LokiPoe, game objects, game data\n" +
                        "- `DreamPoeBot.Loki.Game.Objects` — Monster, Chest, Item, etc.\n" +
                        "- `DreamPoeBot.Loki.Bot` — BotManager, PluginManager, etc.\n" +
                        "- `DreamPoeBot.Loki.Bot.Pathfinding` — ExilePather\n" +
                        "- `DreamPoeBot.Loki.Common` — Logger, Configuration\n" +
                        "- `DreamPoeBot.BotFramework` — KeyManager, MouseManager\n\n" +
                        "## Example: Read-only query\n" +
                        "```csharp\n" +
                        "using DreamPoeBot.Loki.Game;\n\n" +
                        "public class Script\n" +
                        "{\n" +
                        "    public object Execute()\n" +
                        "    {\n" +
                        "        return new {\n" +
                        "            InGame = LokiPoe.IsInGame,\n" +
                        "            Player = LokiPoe.Me?.Name\n" +
                        "        };\n" +
                        "    }\n" +
                        "}\n" +
                        "```\n\n" +
                        "## Example: Game interaction (with ProcessHookManager)\n" +
                        "```csharp\n" +
                        "using DreamPoeBot.Loki.Game;\n\n" +
                        "public class Script\n" +
                        "{\n" +
                        "    public object Execute()\n" +
                        "    {\n" +
                        "        LokiPoe.ProcessHookManager.Enable();\n" +
                        "        try\n" +
                        "        {\n" +
                        "            // game interaction code here\n" +
                        "            return new { Success = true };\n" +
                        "        }\n" +
                        "        finally\n" +
                        "        {\n" +
                        "            LokiPoe.ProcessHookManager.Disable();\n" +
                        "        }\n" +
                        "    }\n" +
                        "}\n" +
                        "```\n",
                    ["inputSchema"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["code"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["description"] =
                                    "C# code defining a public class with a public object Execute() method."
                            },
                            ["timeout"] = new Dictionary<string, object>
                            {
                                ["type"] = "integer",
                                ["description"] = "Timeout in seconds. Default is 30.",
                                ["default"] = 30
                            }
                        },
                        ["required"] = new[] { "code" }
                    }
                }
            };
        }

        private static object CreateResponse(JToken id, object result)
        {
            return new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result
            };
        }

        private static object CreateErrorResponse(JToken id, int code, string message)
        {
            return new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object>
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
        }

        private async Task SendResponseAsync(HttpListenerResponse response, object result)
        {
            var json = JsonConvert.SerializeObject(result, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });

            var buffer = Encoding.UTF8.GetBytes(json);
            response.StatusCode = 200;
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        private async Task SendJsonRpcErrorAsync(HttpListenerResponse response, JToken id, int code, string message)
        {
            var errorResponse = CreateErrorResponse(id, code, message);
            response.StatusCode = 200;
            await SendResponseAsync(response, errorResponse);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
