using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace IntelliOps.AgentWorker
{
    public class LogAggregator
    {
        // [修改] 拔除 ObservableCollection，改用標準的 List。
        // 並加入 lock 物件來確保多執行緒寫入時的安全 (Thread-Safety)
        private readonly List<LogGroup> _logs = new List<LogGroup>();
        private readonly object _lockObj = new object();

        // 讓外部只能唯讀讀取 Logs，防止意外修改
        public IReadOnlyList<LogGroup> Logs
        {
            get
            {
                lock (_lockObj) { return new List<LogGroup>(_logs); }
            }
        }

        // [保留] 這個事件非常重要！未來 Agent 就是訂閱這個事件來知道「有新錯誤進來了」
        public event Action<LogGroup>? OnLogAdded;

        public LogAggregator()
        {
            // [刪除] _uiDispatcher = Dispatcher.CurrentDispatcher;
            // 背景服務不需要，也不能有 UI Dispatcher。
        }

        public void AddLog(LogEventContext context, string source, int eventId, EventLogEntryType type)
        {
            // [刪除] _uiDispatcher.Invoke(...)

            var newLog = new LogGroup
            {
                Id = Guid.NewGuid().ToString(),
                Message = context.PrimaryErrorLog,
                Source = source,
                EventId = eventId,
                LastSeen = context.Timestamp,
                Count = 1,
                LogType = type,
                Context = context
            };

            // 使用 lock 確保當多個 Error 同時發生時，List 不會崩潰
            lock (_lockObj)
            {
                _logs.Add(newLog);

                // 維持最多 100 筆記憶，避免記憶體爆掉
                if (_logs.Count > 100)
                {
                    _logs.RemoveAt(0);
                }
            }

            // 觸發事件，把這筆 Log 丟出去給 Agent 分析
            OnLogAdded?.Invoke(newLog);
        }
    }
}