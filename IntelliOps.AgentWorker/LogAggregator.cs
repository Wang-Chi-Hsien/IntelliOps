using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;

namespace IntelliOps.AgentWorker
{
    public class LogAggregator
    {
        public ObservableCollection<LogGroup> Logs { get; private set; } = new ObservableCollection<LogGroup>();
        private Dispatcher _uiDispatcher;

        public event Action<LogGroup>? OnLogAdded;

        public LogAggregator()
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
        }

        // [修改] 第一個參數改為 LogEventContext
        public void AddLog(LogEventContext context, string source, int eventId, EventLogEntryType type)
        {
            _uiDispatcher.Invoke(() =>
            {
                var newLog = new LogGroup
                {
                    Id = Guid.NewGuid().ToString(),
                    Message = context.PrimaryErrorLog, // UI 依然只顯示主要錯誤
                    Source = source,
                    EventId = eventId,
                    LastSeen = context.Timestamp,
                    Count = 1,
                    LogType = type,
                    Context = context // [新增] 將完整 Context 存入
                };

                Logs.Add(newLog);
                OnLogAdded?.Invoke(newLog);

                if (Logs.Count > 100) Logs.RemoveAt(0);
            });
        }
    }
}