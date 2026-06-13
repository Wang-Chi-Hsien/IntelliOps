using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IntelliOps.AgentWorker
{
    public class LogMonitor
    {
        private readonly LogAggregator _aggregator;

        // 【修正】建構子正確注入 LogAggregator，與 Program.cs 完美對齊
        public LogMonitor(LogAggregator aggregator)
        {
            _aggregator = aggregator;
        }

        /// <summary>
        /// 啟動非同步即時追蹤 (類似 Linux tail -f 機制)
        /// </summary>
        public async Task StartTailingAsync(string logFilePath, CancellationToken cancellationToken)
        {
            Console.WriteLine($"📂 [Linux LogMonitor] 正在啟動，目標監聽檔案: {logFilePath}");

            // 防呆：如果模擬的日誌資料夾不存在，自動建立空的
            string? dir = Path.GetDirectoryName(logFilePath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(logFilePath)) await File.WriteAllTextAsync(logFilePath, "");

            // 使用 FileShare.ReadWrite 允許外部的 echo >> 邊寫邊讀，防止檔案鎖死
            using var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fileStream);

            // 一啟動時先將指標移到檔案最末尾，只監聽最新喷出來的即時日誌
            fileStream.Seek(0, SeekOrigin.End);

            Console.WriteLine("🚀 [Linux LogMonitor] 串流管道已打通！等待新日誌注入...");

            while (!cancellationToken.IsCancellationRequested)
            {
                string? rawLine = await reader.ReadLineAsync();

                if (rawLine == null)
                {
                    // 如果讀到尾巴了，暫停 500 毫秒再繼續檢查，避免 CPU 發生 100% 盲等空轉
                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rawLine)) continue;

                // 抓到即時新日誌，直接無阻塞地送入 Aggregator 的多執行緒安全處理管線
                _aggregator.ProcessRawLog(rawLine);
            }
        }
    }
}