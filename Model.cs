using System;
using System.Collections.Generic;
using System.Diagnostics;
namespace IntelliOps.WPF
{
    // 1. AI 分析結果模型
    public class AnalysisResult
    {
        public string RagResult { get; set; } = "";
        public double MatchScore { get; set; } = 0.0;
        public string AiAnalysis { get; set; } = "";
        public List<string> SuggestedActions { get; set; } = new List<string>();
    }

    // 2. 知識庫項目模型 (用於 RAG)
    public class KnowledgeItem
    {
        public string ErrorPattern { get; set; } = "";
        public string Solution { get; set; } = "";
        public string AutoAction { get; set; } = "";
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }

    // 3. UI 顯示用的 Log 群組模型
    // 3. UI 顯示用的 Log 群組模型
    public class LogGroup
    {
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
        public string Source { get; set; } = "";
        public int EventId { get; set; }
        public int Count { get; set; } = 1;
        public DateTime LastSeen { get; set; }
        public string DisplayTime => LastSeen.ToString("HH:mm:ss");

        // [新增] 儲存 Log 類型 (Error/Warning)
        public EventLogEntryType LogType { get; set; }

        // [修改] 根據類型回傳顏色 (WPF 能讀懂的 Hex 色碼)
        public string SeverityColor 
        {
            get 
            {
                if (LogType == EventLogEntryType.Error) return "#FF5555"; // 紅色
                if (LogType == EventLogEntryType.Warning) return "#FFCC00"; // 黃色
                return "#FFFFFF"; // 其他白色
            }
        }
        
        public AnalysisResult? CachedAnalysis { get; set; }
    }
}