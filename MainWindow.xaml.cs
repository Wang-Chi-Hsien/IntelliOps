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

        // [關鍵] 控制是否自動跳轉到最新 Log
        private bool _isAutoScroll = true;

        public MainWindow()
        {
            InitializeComponent();

            // [修正 1] 建構子現在是輕量的，可以直接實例化，不會卡死
            _core = new IntelliOpsCore();

            this.Loaded += MainWindow_Loaded;
        }

        // [修正 2] 將初始化邏輯獨立出來，使用 async/await 避免死鎖
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 給予使用者反饋，表示正在連線
                if (TxtAiAnalysis != null)
                    TxtAiAnalysis.Text = "系統初始化中... (正在連線至 Ollama 模型與載入知識庫)";

                // 2. [關鍵] 非同步初始化 Core (這裡會載入 Embedding，需要一點時間)
                await _core.InitializeAsync();

                // 3. 初始化完成，顯示就緒
                if (TxtAiAnalysis != null)
                    TxtAiAnalysis.Text = "系統就緒。正在監控 Windows Event Log...";

                // 4. 設定 Log 聚合器 (這部分保持原樣)
                _aggregator = new LogAggregator();
                LogListView.ItemsSource = _aggregator.Logs;

                // OnLogAdded 事件：根據模式決定要不要跳轉
                _aggregator.OnLogAdded += (newLog) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        // 只有在 "自動模式" 下，才切換選取項目並捲動
                        if (_isAutoScroll)
                        {
                            LogListView.SelectedItem = newLog;
                            LogListView.ScrollIntoView(newLog);
                        }
                    });
                };

                // 5. 一切就緒後，才開始監聽 Log
                StartListening();
            }
            catch (Exception ex)
            {
                if (TxtAiAnalysis != null)
                    TxtAiAnalysis.Text = $"嚴重錯誤：系統初始化失敗。\n原因: {ex.Message}\n請確認 Ollama 是否已開啟。";
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
                    // 權限不足或其他錯誤處理
                    Debug.WriteLine($"監聽 Log 失敗: {ex.Message}");
                }
            });
        }

        private async void LogListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LogListView.SelectedItem is LogGroup currentLog) // 這裡改名為 currentLog 以示區別
            {
                // 1. 處理自動捲動邏輯 (保持原樣)
                if (_aggregator.Logs.Count > 0 && currentLog != _aggregator.Logs.Last())
                {
                    _isAutoScroll = false;
                    BtnResumeScroll.Visibility = Visibility.Visible;
                }

                // 2. 切換時，先重置右側 UI 狀態
                // 不管有沒有分析結果，先清空，避免看到上一個 Log 的殘留資訊
                TxtRagScore.Text = "";
                TxtRagContent.Text = "";
                TxtAiAnalysis.Text = "";
                ActionList.ItemsSource = null;

                // 隱藏 Loading，直到我們確定要分析才打開
                if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Collapsed;

                // 3. 檢查是否有快取
                if (currentLog.CachedAnalysis != null)
                {
                    DisplayResult(currentLog.CachedAnalysis);
                }
                else
                {
                    // --- 開始新分析 ---

                    if (_core == null) return;

                    // 顯示讀取條 (因為這是當前選中的項目)
                    if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Visible;
                    if (TxtAiAnalysis != null) TxtAiAnalysis.Text = "AI 分析中...";

                    // [關鍵修正] 捕捉當前的 Context
                    // 在 await 發生前，currentLog 是我們發出請求的對象
                    var analysisTask = _core.AnalyzeLogAsync(currentLog.Message);

                    // 等待結果... 使用者這時候可能會點選別的 Log
                    var result = await analysisTask;

                    // --- [核心邏輯] 身分檢查 ---

                    // 1. 先存檔 (不管使用者現在看哪裡，這個結果都是屬於 currentLog 的)
                    currentLog.CachedAnalysis = result;

                    // 2. 檢查使用者現在是否還停留在這個 Log 上？
                    // 只有當 "現在選中的項目" == "當初發出請求的項目" 時，才更新 UI
                    if (LogListView.SelectedItem == currentLog)
                    {
                        if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Collapsed;
                        DisplayResult(result);
                    }
                    else
                    {
                        // 使用者已經跑去別的 Log 了！
                        // 什麼都不要做 (Do Nothing)。
                        // 結果已經存在 CachedAnalysis 裡了，等使用者改天切回來時，
                        // 上面的 `if (currentLog.CachedAnalysis != null)` 就會直接顯示結果。
                        Debug.WriteLine("背景分析完成，但使用者已切換畫面，僅寫入快取。");
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

        // [新增] 恢復自動捲動按鈕
        private void BtnResumeScroll_Click(object sender, RoutedEventArgs e)
        {
            _isAutoScroll = true; // 恢復自動模式
            BtnResumeScroll.Visibility = Visibility.Collapsed; // 隱藏按鈕

            // 馬上跳到最新一筆
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

                var confirm = MessageBox.Show($"Are you sure you want to execute: {action}?",
                                            "Security Confirmation",
                                            MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (confirm == MessageBoxResult.Yes)
                {
                    _ = _core.ExecuteActionAsync(action);
                }
            }
        }
    }
}