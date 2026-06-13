using System;
using System.Collections.Generic;

namespace IntelliOps.AgentWorker
{
    public class LogAggregator
    {
        // 【完整保留】你原汁原味的執行緒安全清單與專屬鎖物件
        private readonly List<LogGroup> _logs = new List<LogGroup>();
        private readonly object _lockObj = new object();

        // 【完整保留】讓外部（如未來的 WPF）唯讀讀取 Logs，防止意外修改
        public IReadOnlyList<LogGroup> Logs
        {
            get
            {
                lock (_lockObj) { return new List<LogGroup>(_logs); }
            }
        }

        // 【完整保留】神經傳導事件：觸發後讓 Program.cs 知道有新錯誤進來
        public event Action<LogGroup>? OnLogAdded;

        public LogAggregator()
        {
            // 拔除 UI Dispatcher，背景服務不需要
        }

        /// <summary>
        /// 接收來自 LogMonitor 的即時 Linux 原始日誌，洗滌並聚合
        /// </summary>
        public void ProcessRawLog(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return;

            // 1. 透過方案 B 二進位解碼器，秒解 PRI 與 Severity 等級
            var parseResult = LinuxSyslogParser.ParseLine(rawLine);

            // 2. 建立標準的 LogGroup 節點，並完美對接新版跨平台 Model 欄位
            var newLog = new LogGroup
            {
                Id = Guid.NewGuid().ToString(),
                Message = rawLine, // 完整原始日誌
                Source = "Linux_Syslog",
                EventId = "SYS",   // 預設系統事件標籤
                Count = 1,
                LastSeen = DateTime.Now,
                Severity = parseResult.IsCritical ? LogSeverity.Error : LogSeverity.Info,
                SeverityLabel = parseResult.SeverityLabel
            };

            // 3. 把剝離了標頭的「最純淨主體」更新進去，確保 RAG 餘弦夾角 100% 精準
            newLog.Context.PrimaryErrorLog = parseResult.CoreMessage;
            newLog.Context.SurroundingLogs = rawLine;
            newLog.Context.Timestamp = DateTime.Now;

            // 4. 【完整接回】使用 lock 確保當多個日誌同時併發噴出時，List 不會崩潰
            // 【優化】將事件觸發一併收納進鎖物件中，徹底杜絕多執行緒並行輸出導致控制台畫面交錯、順序亂掉的 Race Condition
            lock (_lockObj)
            {
                _logs.Add(newLog);

                // 【完整接回】維持最多 100 筆記憶，避免長時間監聽導致記憶體爆掉
                if (_logs.Count > 100)
                {
                    _logs.RemoveAt(0);
                }

                // 5. 觸發事件，把這筆洗滌乾淨的 Log 丟出去給 Agent 進行混合審查排隊
                OnLogAdded?.Invoke(newLog);
            }
        }
    }
}