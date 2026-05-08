using System;
using System.Diagnostics;

namespace IntelliOps.AgentWorker
{
    // [新增] 用來傳遞給大腦的完整上下文結構
    public class LogEventContext
    {
        public string PrimaryErrorLog { get; set; } = "";
        public string SurroundingLogs { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    // 1. AI 分析結果模型
    public class AnalysisResult
    {
        public string RagResult { get; set; } = "";
        public double MatchScore { get; set; } = 0.0;

        // 最終的 RCA 報告會全部存在這裡面
        public string AiAnalysis { get; set; } = "";

        // [刪除] SuggestedActions，改由人類閱讀 AiAnalysis 報告後自行操作
    }

    // 2. 知識庫項目模型 (用於 RAG)
    public class KnowledgeItem
    {
        public string ErrorPattern { get; set; } = "";
        public string Solution { get; set; } = "";

        // [刪除] AutoAction，徹底拔除自動執行風險

        public float[] Embedding { get; set; } = Array.Empty<float>();
    }

    // 3. UI 顯示用的 Log 群組模型
    public class LogGroup
    {
        // [優化] 給予預設 GUID，避免產生空字串的 ID
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Message { get; set; } = "";
        public string Source { get; set; } = "";
        public int EventId { get; set; }
        public int Count { get; set; } = 1;
        public DateTime LastSeen { get; set; }
        public string DisplayTime => LastSeen.ToString("HH:mm:ss");

        public EventLogEntryType LogType { get; set; }

        // [保留] 為了快速綁定 WPF 介面暫時保留，未來建議移至 WPF 的 IValueConverter
        public string SeverityColor
        {
            get
            {
                if (LogType == EventLogEntryType.Error) return "#FF5555";
                if (LogType == EventLogEntryType.Warning) return "#FFCC00";
                return "#FFFFFF";
            }
        }

        public AnalysisResult? CachedAnalysis { get; set; }
        public LogEventContext Context { get; set; } = new LogEventContext();
    }
}