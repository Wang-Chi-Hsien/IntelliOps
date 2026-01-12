using System.Collections.ObjectModel;
using System.Diagnostics; // [新增] 為了使用 EventLogEntryType
using System.Windows.Threading;

namespace IntelliOps.WPF
{
    public class LogAggregator
    {
        public ObservableCollection<LogGroup> Logs { get; private set; } = new ObservableCollection<LogGroup>();
        private Dispatcher _uiDispatcher;

        // [注意] 我刪除了 _lookup Dictionary，因為我們不再需要「去重/聚類」了
        // 我們希望每一條 Log 都直接顯示出來，這樣如果系統狂噴錯誤，使用者才會緊張

        public event Action<LogGroup>? OnLogAdded;

        public LogAggregator()
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
        }

        // [修改] 這裡增加了 EventLogEntryType type 參數
        public void AddLog(string message, string source, int eventId, EventLogEntryType type)
        {
            _uiDispatcher.Invoke(() =>
            {
                // 不管之前有沒有發生過，直接建立新的 Log 物件
                var newLog = new LogGroup
                {
                    Id = Guid.NewGuid().ToString(), // 給每個 Log 唯一的 ID
                    Message = message,
                    Source = source,
                    EventId = eventId,
                    LastSeen = DateTime.Now,
                    Count = 1,      // 永遠是 1，因為不合併了
                    LogType = type  // [新增] 把類型 (Error/Warning) 存進去，讓 UI 變色
                };

                Logs.Add(newLog); // 加到最下面
                OnLogAdded?.Invoke(newLog); // 通知 UI 捲動

                // (選用) 為了效能，限制只保留最近 100 筆，避免記憶體爆炸
                if (Logs.Count > 100) Logs.RemoveAt(0);
            });
        }
    }
}