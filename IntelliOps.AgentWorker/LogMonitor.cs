using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IntelliOps.AgentWorker
{
    public class LogMonitor
    {
        private readonly LogAggregator _aggregator;
        private readonly int _contextWindowSize = 50;
        private Queue<string> _slidingWindow = new Queue<string>();

        // 防護機制：記錄最近觸發過的錯誤，防止 AI 被連鎖錯誤淹沒
        private ConcurrentDictionary<string, DateTime> _errorThrottleCache = new();
        private readonly TimeSpan _throttleInterval = TimeSpan.FromMinutes(5);

        public LogMonitor(LogAggregator aggregator)
        {
            _aggregator = aggregator;
        }

        public async Task StartTailingAsync(string filePath, CancellationToken token)
        {
            Console.WriteLine($"👀 開始監控日誌: {filePath}");

            // 確保檔案存在，否則會報錯
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[警告] 找不到日誌檔案: {filePath}。請確認路徑或權限。");
                return;
            }

            // 使用 FileShare.ReadWrite 允許 Linux 系統同時寫入該檔案
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            // 直接跳到檔案最後面，我們只關心「未來」發生的錯誤
            fs.Seek(0, SeekOrigin.End);

            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync();

                if (line == null)
                {
                    // 檔案沒有新內容，休息 0.5 秒後再看一次 (極度省電)
                    await Task.Delay(500, token);
                    continue;
                }

                // 1. 維護上下文視窗
                _slidingWindow.Enqueue(line);
                if (_slidingWindow.Count > _contextWindowSize)
                {
                    _slidingWindow.Dequeue();
                }

                // 2. 判斷是否為嚴重錯誤 (依據您的 Linux 日誌格式調整)
                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessErrorAsync(line);
                }
            }
        }

        private async Task ProcessErrorAsync(string errorLine)
        {
            string errorSignature = ExtractErrorSignature(errorLine);

            if (_errorThrottleCache.TryGetValue(errorSignature, out DateTime lastTriggerTime))
            {
                if ((DateTime.Now - lastTriggerTime) < _throttleInterval)
                {
                    // 5 分鐘內發生過一模一樣的錯誤，直接丟棄，保護 AI
                    return;
                }
            }

            _errorThrottleCache[errorSignature] = DateTime.Now;
            string contextData = string.Join("\n", _slidingWindow);

            var context = new LogEventContext
            {
                PrimaryErrorLog = errorLine,
                SurroundingLogs = contextData,
                Timestamp = DateTime.Now
            };

            Console.WriteLine($"\n🚨 [攔截錯誤] {errorLine.Substring(0, Math.Min(50, errorLine.Length))}...");
            _aggregator.AddLog(context, "LinuxServer", 0, EventLogEntryType.Error);
        }

        private string ExtractErrorSignature(string line)
        {
            // 拔除時間戳記 (例如 2026-05-08 12:00:00) 以取得純淨的錯誤特徵
            // 這樣才能準確判斷是不是同一個錯誤
            return Regex.Replace(line, @"\[.*?\]|\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", "").Trim();
        }
    }
}