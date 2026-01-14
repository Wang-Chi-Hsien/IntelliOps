using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Linq;

namespace IntelliOps.WPF
{
    public partial class MainWindow : Window
    {
        private LogAggregator _aggregator = null!;
        private IntelliOpsCore _core = null!;
        private bool _isAutoScroll = true;

        public MainWindow()
        {
            InitializeComponent();
            _core = new IntelliOpsCore();
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TxtAiAnalysis != null)
                    TxtAiAnalysis.Text = "系統初始化中... (正在連線至 Ollama 模型 qwen2.5:3b 與載入知識庫)";

                await _core.InitializeAsync();

                if (TxtAiAnalysis != null)
                    TxtAiAnalysis.Text = "系統就緒。正在監控 Windows Event Log...";

                _aggregator = new LogAggregator();
                LogListView.ItemsSource = _aggregator.Logs;

                _aggregator.OnLogAdded += (newLog) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_isAutoScroll)
                        {
                            LogListView.SelectedItem = newLog;
                            LogListView.ScrollIntoView(newLog);
                        }
                    });
                };

                StartListening();
            }
            catch (Exception ex)
            {
                if (TxtAiAnalysis != null)
                    TxtAiAnalysis.Text = $"嚴重錯誤：系統初始化失敗。\n請確認您已執行 'ollama serve' 並且已下載 'qwen2.5:3b' 模型。\n錯誤細節: {ex.Message}";
                MessageBox.Show($"初始化失敗: {ex.Message}", "系統錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartListening()
        {
            Task.Run(() =>
            {
                var eventLog = new EventLog("Application");
                try
                {
                    eventLog.EnableRaisingEvents = true;
                    eventLog.EntryWritten += (s, e) =>
                    {
                        if (e.Entry.EntryType == EventLogEntryType.Error ||
                            e.Entry.EntryType == EventLogEntryType.Warning)
                        {
                            int safeEventId = (int)e.Entry.InstanceId;
                            _aggregator.AddLog(e.Entry.Message, e.Entry.Source, safeEventId, e.Entry.EntryType);
                        }
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"監聽 Log 失敗: {ex.Message}");
                }
            });
        }

        private async void LogListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LogListView.SelectedItem is LogGroup currentLog)
            {
                // 1. UI 重置
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

                // 2. 檢查快取
                if (currentLog.CachedAnalysis != null)
                {
                    DisplayResult(currentLog.CachedAnalysis);
                }
                else
                {
                    // --- 3. 開始新分析 (Streaming) ---
                    if (_core == null) return;

                    if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Visible;

                    // 傳入 Callback 進行即時更新
                    // 注意：此處使用 await 接收最終結果，但過程中會不斷觸發 callback
                    var analysisTask = _core.AnalyzeLogAsync(currentLog.Message, (partialText) =>
                    {
                        // 確保切回 UI 執行緒
                        Dispatcher.Invoke(() =>
                        {
                            // [防呆] 只有當使用者還看著這一筆 Log 時，才更新畫面
                            if (LogListView.SelectedItem == currentLog)
                            {
                                TxtAiAnalysis.Text = partialText;
                                // 讓 ScrollViewer 捲到底部 (假設外層有 ScrollViewer 名為 LogScrollViewer，若無可忽略)
                                // LogScrollViewer.ScrollToBottom(); 
                            }
                        });
                    });

                    var result = await analysisTask;

                    // 4. 分析完成，存入快取
                    currentLog.CachedAnalysis = result;

                    // 5. 再次檢查是否要更新最終 UI
                    if (LogListView.SelectedItem == currentLog)
                    {
                        if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Collapsed;
                        DisplayResult(result);
                    }
                }
            }
        }

        private void DisplayResult(AnalysisResult result)
        {
            TxtRagScore.Text = $"Similarity: {result.MatchScore:P0}";
            TxtRagContent.Text = result.RagResult;
            TxtAiAnalysis.Text = result.AiAnalysis;
            ActionList.ItemsSource = result.SuggestedActions;
        }

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
                if (action == "SearchStackOverflow")
                {
                    _ = _core.ExecuteActionAsync(action);
                    return;
                }

                var confirm = MessageBox.Show($"確定要執行系統操作: {action} 嗎?",
                                              "安全確認",
                                              MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (confirm == MessageBoxResult.Yes)
                {
                    _ = _core.ExecuteActionAsync(action);
                }
            }
        }
    }
}