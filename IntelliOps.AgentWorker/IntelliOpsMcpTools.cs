using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace IntelliOps.AgentWorker
{
    public class IntelliOpsMcpTools
    {
        private readonly List<KnowledgeItem> _knowledgeBase;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embedding;
        
        // 防禦機制：記錄大腦在此次對話中已經查過的關鍵字與次數
        private readonly HashSet<string> _searchedQueries = new(StringComparer.OrdinalIgnoreCase);
        private int _callCounter = 0;
        private const int MaxMcpCalls = 2; // 強制規定大腦最多只能發動 2 次自主調查

        public IntelliOpsMcpTools(List<KnowledgeItem> knowledgeBase, IEmbeddingGenerator<string, Embedding<float>> embedding)
        {
            _knowledgeBase = knowledgeBase;
            _embedding = embedding;
        }

        // 重設防禦計數器（每次處理新 Log 時由 Core 呼叫）
        public void ResetSession()
        {
            _searchedQueries.Clear();
            _callCounter = 0;
        }

        [KernelFunction, Description("當第一步提供的 3 筆歷史紀錄不足以診斷錯誤時，大腦(LLM)可使用此工具發動主動調查，輸入更精準的維運關鍵字或錯誤碼，重新至 Qdrant 知識庫動態撈取更深層的解法資訊。")]
        public async Task<string> SearchQdrantKnowledgeBase(
            [Description("大腦自行推理出的新檢索關鍵字或特定錯誤碼(如 EventID, Exception名稱)")] string searchKeyword)
        {
            // 防禦 1：死循環熔斷
            if (_callCounter >= MaxMcpCalls)
            {
                return "[MCP 系統防禦：已達到單次診斷的最大主動查詢次數限制。請根據現有資料輸出報告。]";
            }

            // 防禦 2：查詢去重
            if (_searchedQueries.Contains(searchKeyword))
            {
                return "[MCP 系統防禦：此關鍵字先前已檢索過，請勿重複查詢相同內容，避免陷入無限思考迴圈。]";
            }

            _callCounter++;
            _searchedQueries.Add(searchKeyword);

            try
            {
                // 將大腦自主生成的關鍵字轉向量
                var generatedQuery = await _embedding.GenerateAsync(new[] { searchKeyword });
                var queryVector = generatedQuery[0].Vector;

                // 進行向量相似度搜尋（撈取前 2 筆最相關的隱藏資料）
                var matches = _knowledgeBase
                    .Select(x => new { Item = x, Score = TensorPrimitives.CosineSimilarity(x.Embedding, queryVector.Span) })
                    .Where(x => x.Score > 0.65) // 既然是進階主動搜尋，分數界限稍微放寬，擴大搜索網
                    .OrderByDescending(x => x.Score)
                    .Take(2)
                    .ToList();

                if (!matches.Any())
                {
                    return $"[MCP 調查結果]：使用關鍵字 '{searchKeyword}' 檢索 Qdrant 知識庫，未找到進一步的相關解答。";
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[MCP 調查成功 - 找到了額外的關聯線索] (關鍵字: {searchKeyword}):");
                foreach (var match in matches)
                {
                    sb.AppendLine($"- 歷史模式: {match.Item.ErrorPattern} (關聯度: {match.Score:P0})");
                    sb.AppendLine($"  適用解法: {match.Item.Solution}");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"[MCP 調查異常]：檢索資料庫時發生錯誤 {ex.Message}";
            }
        }
    }
}