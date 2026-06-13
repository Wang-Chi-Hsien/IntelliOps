using System;
using System.Threading;
using System.Threading.Tasks;

namespace IntelliOps.AgentWorker
{
    class Program
    {
        // 訊號量限制：確保同時間「只允許 1 個任務」進入 AI 大腦推理，防止 GPU 記憶體爆掉
        private static readonly SemaphoreSlim _aiSemaphore = new SemaphoreSlim(1, 1);

        static async Task Main(string[] args)
        {
            // 強制開啟 UTF8 編碼，確保地端 Qwen 模型吐出來的繁體中文 RCA 報告不會變亂碼
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("====================================================");
            Console.WriteLine("🚀 [IntelliOps 智能運維 AgentWorker - 方案 B 完全體] 啟動");
            Console.WriteLine("====================================================");

            // 1. 建立核心元件
            var aggregator = new LogAggregator();
            var core = new IntelliOpsCore();
            
            // 【對齊關鍵 1】建立 Monitor，傳入 aggregator 作為日誌處理管線
            var monitor = new LogMonitor(aggregator); 

            // 2. 初始化 AI 大腦與在地端 52MB 向量資料庫 (載入 qdrant_local_db.json)
            await core.InitializeAsync();
            Console.WriteLine("✅ AI 大腦與在地端向量資料庫初始化完成。");

            // 3. 設定神經傳導路徑 (當 LogMonitor 抓到日誌並由 Aggregator 聚合、洗滌後觸發)
            aggregator.OnLogAdded += async (logGroup) =>
            {
                // 讀取從檔案尾巴抓到的原始日誌行
                string rawLine = logGroup.Message; 

                // 核心解耦管線：丟進二進位解碼器，秒解 PRI 與 Severity 等級
                var parseResult = LinuxSyslogParser.ParseLine(rawLine);

                // 根據工業標準：只有 Severity <= 3 (Emergency, Alert, Critical, Error) 的日誌才放行觸發 AI
                if (parseResult.IsCritical)
                {
                    ConsoleHelper.WriteLineSafely($"\n🔥 [RFC 3164 觸發 AI] 偵測到核心致命事件！");
                    ConsoleHelper.WriteLineSafely($"   [日誌等級]: {parseResult.SeverityLabel}");
                    ConsoleHelper.WriteLineSafely($"   [核心主體]: {parseResult.CoreMessage}");
                    ConsoleHelper.WriteLineSafely($"   [排隊狀態]: 正在進入大腦排隊序列...");

                    // 請求通行證，確保排隊
                    await _aiSemaphore.WaitAsync();

                    try
                    {
                        // 帶有動態 Spinner 旋轉特效的 AI 混合審查推理流程
                        var result = await ConsoleHelper.RunWithSpinnerAsync(
                            "   [深度分析中] 混合審查特工與 MCP 工具調度中，請稍候",
                            async () => await core.AnalyzeLogAsync(logGroup.Context)
                        );

                        logGroup.CachedAnalysis = result;

                        // 執行緒安全地在控制台渲染精準的 Markdown 格式 RCA 報告
                        ConsoleHelper.PrintReportSafely(result.AiAnalysis);

                        ConsoleHelper.WriteLineSafely("   [系統提示] 報告已持久化存檔。等待外部 Web UI 反饋...");
                        ConsoleHelper.WriteLineSafely("----------------------------------------------------------------------");
                    }
                    catch (Exception ex)
                    {
                        ConsoleHelper.WriteLineSafely($"❌ [大腦推理異常]: {ex.Message}");
                    }
                    finally
                    {
                        // 確保通行證百分之百會歸還，放行下一個排隊的 Linux 錯誤事件
                        _aiSemaphore.Release();
                    }
                }
                else
                {
                    // 屬於 Severity 4~7 的一般通告或除錯日誌 (例如 Info、Debug)
                    ConsoleHelper.WriteLineSafely($"ℹ️ [解碼跳過] 等級為 {parseResult.SeverityLabel}，屬於常態流水日誌，不觸發 AI。");
                }
            };

            // 4. 準備優雅關閉 (Graceful Shutdown) 機制
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\n🛑 偵測到中斷訊號，正在安全釋放執行緒與管道，準備關閉...");
            };

            // 5. 動態設定 Linux 監聽檔案路徑
            string logPath = "/home/luke/logs/mock_syslog.log";
            
            // 跨平台防呆：如果在 Windows 本地開發環境除錯，自動換路徑
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                logPath = "C:\\temp\\syslog.log";
                if (!System.IO.File.Exists(logPath)) System.IO.File.Create(logPath).Close();
            }

            // 6. 啟動非同步追蹤 
            try
            {
                // 【對齊關鍵 2】呼叫我們在上一步補正回來的 StartTailingAsync 方法
                await monitor.StartTailingAsync(logPath, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 優雅關閉時的正常異常，不予理會
            }

            Console.WriteLine("👋 系統已完全安全釋放，關機完成。");
        }
    }
}