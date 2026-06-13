using System;

namespace IntelliOps.AgentWorker
{
    // [新增] 跨平台日誌嚴重度列舉，徹底脫離 Windows 依賴，確保 Linux 能完美編譯
    public enum LogSeverity
    {
        Emergency = 0,
        Alert = 1,
        Critical = 2,
        Error = 3,
        Warning = 4,
        Notice = 5,
        Info = 6,
        Debug = 7,
        Unknown = 8
    }

    // 用來傳遞給 AI 大腦的完整上下文結構
    public class LogEventContext
    {
        // 經過 Parser 剝離時間與 PID 後，最純粹的核心日誌主體（用來丟給 Qdrant 計算餘弦相似度）
        public string PrimaryErrorLog { get; set; } = "";
        
        // 完整的原始 Linux 日誌行（留作 AI 大腦推理的完整上下文參考）
        public string SurroundingLogs { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    // AI 混合審查分析結果模型
    public class AnalysisResult
    {
        public string RagResult { get; set; } = "";
        public double MatchScore { get; set; } = 0.0;

        // 最終由 Qwen 大腦生成的 Markdown RCA 報告會存在這裡
        public string AiAnalysis { get; set; } = "";
    }

    // 知識庫項目模型 (用於地端 52MB Qdrant 向量庫)
    public class KnowledgeItem
    {
        public string ErrorPattern { get; set; } = ""; // 儲存 Loghub 的 EventId (如 E10, E15)
        public string Solution { get; set; } = "";     // 儲存 Template 模版公式與解法
        public float[] Embedding { get; set; } = Array.Empty<float>(); // 768 維度向量矩陣
    }

    // UI 顯示與日誌聚合用的 Log 群組模型
    public class LogGroup
    {
        // 給予預設 GUID，避免產生空字串的 ID
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Message { get; set; } = "";
        public string Source { get; set; } = "";
        public string EventId { get; set; } = ""; // 改為 string，以相容 Loghub 的 "E15"、"E10" 格式
        public int Count { get; set; } = 1;
        public DateTime LastSeen { get; set; }
        public string DisplayTime => LastSeen.ToString("HH:mm:ss");

        // 採用跨平台的自訂 Severity，讓 Linux 監聽器與 WPF 前端都能看懂
        public LogSeverity Severity { get; set; } = LogSeverity.Info;
        public string SeverityLabel { get; set; } = "Info";

        // 完美保留！未來可直接綁定至 WPF 介面，根據 Syslog 等級自動變色
        public string SeverityColor
        {
            get
            {
                // Emergency, Alert, Critical, Error 全部亮紅燈
                if (Severity <= LogSeverity.Error) return "#FF5555"; 
                // Warning 亮黃燈
                if (Severity == LogSeverity.Warning) return "#FFCC00"; 
                // 其餘常態日誌亮白燈
                return "#FFFFFF"; 
            }
        }

        // 快取分析結果，防禦日誌風暴重複呼叫 AI
        public AnalysisResult? CachedAnalysis { get; set; }
        public LogEventContext Context { get; set; } = new LogEventContext();
    }
}