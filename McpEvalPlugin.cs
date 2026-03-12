using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using DreamPoeBot.Loki.Bot;
using DreamPoeBot.Loki.Common;
using log4net;

namespace McpEval
{
    public class McpEvalPlugin : IPlugin
    {
        private static readonly ILog Log = Logger.GetLoggerInstanceForType();

        private McpHttpServer _server;
        private McpEvalGui _gui;

        #region Implementation of IAuthored

        public string Name => "McpEval";
        public string Author => "Rushtothesun";
        public string Description => "MCP JSON-RPC server for AI-driven live code execution.";
        public string Version => "1.0.0";

        #endregion

        #region Implementation of IBase

        public void Initialize()
        {
            Log.Info("[McpEval] Initializing...");

            if (McpEvalSettings.Instance.ServerEnabled)
            {
                StartServer();
            }
        }

        public void Deinitialize()
        {
            StopServer();
            Log.Info("[McpEval] Deinitialized.");
        }

        #endregion

        #region Implementation of IConfigurable

        public JsonSettings Settings => McpEvalSettings.Instance;
        public UserControl Control => (_gui ?? (_gui = new McpEvalGui(this)));

        #endregion

        #region Implementation of ILogicProvider

        public async Task<LogicResult> Logic(Logic logic)
        {
            return LogicResult.Unprovided;
        }

        #endregion

        #region Implementation of IMessageHandler

        public MessageResult Message(Message message)
        {
            return MessageResult.Unprocessed;
        }

        #endregion

        #region Implementation of IEnableable

        public void Enable()
        {
            if (McpEvalSettings.Instance.ServerEnabled)
            {
                StartServer();
            }
        }

        public void Disable()
        {
            StopServer();
        }

        #endregion

        #region Server Management

        public bool IsServerRunning => _server?.IsRunning ?? false;

        public void StartServer()
        {
            if (_server?.IsRunning == true) return;

            try
            {
                var port = McpEvalSettings.Instance.Port;
                _server = new McpHttpServer(port);
                _server.Start();
                Log.Info($"[McpEval] MCP server started on port {port}");
            }
            catch (Exception ex)
            {
                Log.Error($"[McpEval] Failed to start MCP server: {ex.Message}", ex);
            }
        }

        public void StopServer()
        {
            if (_server == null) return;

            try
            {
                _server.Stop();
                _server.Dispose();
                _server = null;
                Log.Info("[McpEval] MCP server stopped.");
            }
            catch (Exception ex)
            {
                Log.Error($"[McpEval] Error stopping MCP server: {ex.Message}", ex);
            }
        }

        public void RestartServer()
        {
            StopServer();
            StartServer();
        }

        #endregion

        public override string ToString()
        {
            return Name + ": " + Description;
        }
    }
}
