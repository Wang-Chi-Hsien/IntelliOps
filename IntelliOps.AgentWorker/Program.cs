using System;
using System.Threading;
using System.Threading.Tasks;

namespace IntelliOps.AgentWorker
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("IntelliOps Agent Worker 啟動中...");

            // 1. 建立核心元件
            var aggregator = new LogAggregator();
            var core = new IntelliOpsCore();
            var monitor = new LogMonitor(aggregator);

            // 2. 初始化 AI 大腦
            await core.InitializeAsync();
            Console.WriteLine("AI 大腦初始化完成。");

            // 3. 設定神經傳導路徑 (當 Aggregator 收到 Log 時，呼叫 Core 分析)
            aggregator.OnLogAdded += async (logGroup) =>
            {
                var result = await core.AnalyzeLogAsync(logGroup.Context, (progress) =>
                {
                    // 在終端機印出 AI 思考過程
                    Console.WriteLine(progress);
                });

                logGroup.CachedAnalysis = result;

                Console.WriteLine("========================================");
                Console.WriteLine(" RCA 報告生成完畢 (已寫入快取/準備推播)");
                Console.WriteLine("========================================\n");
            };

            // 4. 準備優雅關閉的機制 (按 Ctrl+C 時觸發)
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\n正在安全關閉系統...");
            };

            // 5. 開始監聽檔案 (請替換為您測試用的假 Log 檔，或 Linux 真實路徑)
            // 在 Windows 測試時，可以先建一個 C:\temp\test.log
            string logPath = "/var/log/syslog";

            // 為了在 Windows 本機測試，我們做個簡單的判斷
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                logPath = "C:\\temp\\syslog.log";
                if (!System.IO.File.Exists(logPath)) System.IO.File.Create(logPath).Close();
            }

            // 啟動監聽器 (這會卡住主執行緒，直到使用者按下 Ctrl+C)
            await monitor.StartTailingAsync(logPath, cts.Token);

            Console.WriteLine("系統已完全關閉。");
        }
    }
}