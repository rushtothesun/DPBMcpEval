using System.ComponentModel;
using DreamPoeBot.Loki;
using DreamPoeBot.Loki.Common;

namespace McpEval
{
    public class McpEvalSettings : JsonSettings
    {
        private static McpEvalSettings _instance;

        public static McpEvalSettings Instance => _instance ?? (_instance = new McpEvalSettings());

        public McpEvalSettings()
            : base(GetSettingsFilePath(Configuration.Instance.Name, string.Format("{0}.json", "McpEval")))
        {
        }

        private int _port;
        private bool _serverEnabled;

        [DefaultValue(5100)]
        public int Port
        {
            get => _port;
            set
            {
                if (value == _port) return;
                _port = value;
                NotifyPropertyChanged(() => Port);
                Save();
            }
        }

        [DefaultValue(true)]
        public bool ServerEnabled
        {
            get => _serverEnabled;
            set
            {
                if (value == _serverEnabled) return;
                _serverEnabled = value;
                NotifyPropertyChanged(() => ServerEnabled);
                Save();
            }
        }
    }
}
