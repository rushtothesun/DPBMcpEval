using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace McpEval
{
    public partial class McpEvalGui : UserControl
    {
        private readonly McpEvalPlugin _plugin;
        private bool _initialized;

        public McpEvalGui(McpEvalPlugin plugin)
        {
            _plugin = plugin;
            InitializeComponent();
            _initialized = true;
            UpdateStatus();
        }

        private void CheckBoxEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _plugin == null) return;

            if (McpEvalSettings.Instance.ServerEnabled)
            {
                _plugin.StartServer();
            }
            else
            {
                _plugin.StopServer();
            }

            UpdateStatus();
        }

        private void ButtonApply_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            _plugin.RestartServer();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_plugin == null || LabelStatus == null) return;

            if (_plugin.IsServerRunning)
            {
                LabelStatus.Content = $"Running on port {McpEvalSettings.Instance.Port}";
                LabelStatus.Foreground = new SolidColorBrush(Colors.Green);
            }
            else
            {
                LabelStatus.Content = "Stopped";
                LabelStatus.Foreground = new SolidColorBrush(Colors.Red);
            }
        }
    }
}
