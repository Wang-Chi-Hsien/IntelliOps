using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IntelliOps.WPF
{
    public partial class MainWindow : Window
    {
        // [移至 AgentWorker] 這些核心物件現在由背景服務負責，UI 不再直接持有
        // private LogAggregator _aggregator = null!;
        // private IntelliOpsCore _core = null!;

        private bool _isAutoScroll = true;

        public MainWindow()
        {
            InitializeComponent();

            // [移至 AgentWorker]
            // _core = new IntelliOpsCore();

            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TxtAiAnalysis != null)
                    TxtAiAnalysis.Text = "WPF 戰情面板已啟動。等待連線至 AgentWorker 資料庫...";

                // [移至 AgentWorker]
                // await _core.InitializeAsync();
                // _aggregator = new LogAggregator();
                // LogListView.ItemsSource = _aggregator.Logs;

                // _aggregator.OnLogAdded += (newLog) => { ... };

                // [移至 AgentWorker] 監聽系統日誌的工作，現在應該交給背景常駐的 Agent 去做
                // StartListening();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"UI 初始化失敗: {ex.Message}", "系統錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // [移至 AgentWorker] 這個 Queue 和監聽邏輯，請原封不動搬到 AgentWorker 裡面！
        // private Queue<(DateTime Time, string Message, EventLogEntryType Type)> _recentLogsBuffer = new();
        // private void StartListening() { ... }

        private async void LogListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // [移至 AgentWorker] 由於 LogGroup, AnalysisResult, 和 _core 都搬走了，這裡暫時註解
            /*
            if (LogListView.SelectedItem is LogGroup currentLog)
            {
                if (_aggregator.Logs.Count > 0 && currentLog != _aggregator.Logs.Last())
                {
                    _isAutoScroll = false;
                    BtnResumeScroll.Visibility = Visibility.Visible;
                }

                TxtRagScore.Text = "";
                TxtRagContent.Text = "";
                TxtAiAnalysis.Text = "";
                ActionList.ItemsSource = null;
                if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Collapsed;

                if (currentLog.CachedAnalysis != null)
                {
                    DisplayResult(currentLog.CachedAnalysis);
                }
                else
                {
                    if (_core == null) return;
                    if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Visible;

                    var analysisTask = _core.AnalyzeLogAsync(currentLog.Context, (partialText) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (LogListView.SelectedItem == currentLog)
                                TxtAiAnalysis.Text = partialText;
                        });
                    });

                    var result = await analysisTask;
                    currentLog.CachedAnalysis = result;

                    if (LogListView.SelectedItem == currentLog)
                    {
                        if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Collapsed;
                        DisplayResult(result);
                    }
                }
            }
            */
        }

        // [移至 AgentWorker] 由於 AnalysisResult 搬走了，此方法暫時註解
        /*
        private void DisplayResult(AnalysisResult result)
        {
            TxtRagScore.Text = $"Similarity: {result.MatchScore:P0}";
            TxtRagContent.Text = result.RagResult;
            TxtAiAnalysis.Text = result.AiAnalysis;
            ActionList.ItemsSource = result.SuggestedActions;
        }
        */

        private void BtnResumeScroll_Click(object sender, RoutedEventArgs e)
        {
            _isAutoScroll = true;
            BtnResumeScroll.Visibility = Visibility.Collapsed;
            if (LogListView.Items.Count > 0)
            {
                var lastItem = LogListView.Items[LogListView.Items.Count - 1];
                LogListView.SelectedItem = lastItem;
                LogListView.ScrollIntoView(lastItem);
            }
        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string action)
            {
                // [移至 AgentWorker] 這裡未來要改成透過 API 或資料庫告訴 Agent 去執行指令
                /*
                string currentPrimaryLog = "";
                if (LogListView.SelectedItem is LogGroup currentLog)
                {
                    currentPrimaryLog = currentLog.Message;
                }

                if (action == "SearchStackOverflow")
                {
                    _ = _core.ExecuteActionAsync(action, currentPrimaryLog);
                    return;
                }

                var confirm = MessageBox.Show($"確定要執行系統操作: {action} 嗎?",
                                              "安全確認",
                                              MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (confirm == MessageBoxResult.Yes)
                {
                    _ = _core.ExecuteActionAsync(action, currentPrimaryLog);
                }
                */
            }
        }
    }
}