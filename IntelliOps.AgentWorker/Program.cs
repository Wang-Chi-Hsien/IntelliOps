using System;
using System.Threading;
using System.Threading.Tasks;

namespace IntelliOps.AgentWorker
{
    class Program
    {
        // 新增這行：這是一個通行證，參數 (1, 1) 代表同時間「只允許 1 個任務」進入 AI 大腦
        private static readonly SemaphoreSlim _aiSemaphore = new SemaphoreSlim(1, 1);
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("IntelliOps Agent Worker 啟動中...");

            // 1. 建立核心元件
            var aggregator = new LogAggregator();
            var core = new IntelliOpsCore();
            var monitor = new LogMonitor(aggregator);

            // 2. 初始化 AI 大腦
            await core.InitializeAsync();
            Console.WriteLine("AI 大腦初始化完成。");

            // 3. 設定神經傳導路徑
            aggregator.OnLogAdded += async (logGroup) =>
            {
                // [修改] 使用安全的印字，不會破壞畫面
                ConsoleHelper.WriteLineSafely($"🚨 [錯誤排隊中] 發現異常: {logGroup.Context.PrimaryErrorLog}");

                await _aiSemaphore.WaitAsync();

                try
                {
                    var result = await ConsoleHelper.RunWithSpinnerAsync(
                        "   [處理中] 大腦推理中，請稍候",
                        async () => await core.AnalyzeLogAsync(logGroup.Context)
                    );

                    logGroup.CachedAnalysis = result;

                    // 印出最終報告
                    ConsoleHelper.PrintReportSafely(result.AiAnalysis);

                    // [修改] 移除 Console.ReadLine()！
                    // 背景程式不應等待輸入。我們只記錄日誌，真正的反饋應由 Web API 處理。
                    ConsoleHelper.WriteLineSafely("   [系統提示] 報告已存檔。等待外部系統匯入學習反饋...");
                    ConsoleHelper.WriteLineSafely("--------------------------------------------------------");
                }
                finally
                {
                    // 確保通行證一定會被歸還，讓下一個錯誤可以進來！
                    _aiSemaphore.Release();
                }
            };

            // 4. 準備優雅關閉的機制
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\n正在安全關閉系統...");
            };

            // 5. 開始監聽檔案
            string logPath = "/var/log/syslog";
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                logPath = "C:\\temp\\syslog.log";
                if (!System.IO.File.Exists(logPath)) System.IO.File.Create(logPath).Close();
            }

            await monitor.StartTailingAsync(logPath, cts.Token);

            Console.WriteLine("系統已完全關閉。");
        }
    }
}