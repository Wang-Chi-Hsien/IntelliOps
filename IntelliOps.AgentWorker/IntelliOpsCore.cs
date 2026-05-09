using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SKEXP0110 
#pragma warning disable SKEXP0001
#pragma warning disable SKEXP0011
#pragma warning disable SKEXP0010

namespace IntelliOps.AgentWorker
{
    public class IntelliOpsCore
    {
        private Kernel _kernel = null!;
        private IChatCompletionService _chat = null!;
        private IEmbeddingGenerator<string, Embedding<float>> _embedding = null!;

        // 知識庫
        private List<KnowledgeItem> _knowledgeBase = new List<KnowledgeItem>();

        // [新增] 記憶體快取：用來防止同一錯誤在短時間內重複觸發 AI，節省算力
        private readonly IMemoryCache _reportCache = new MemoryCache(new MemoryCacheOptions());

        private static readonly HttpClient _sharedHttpClient = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/v1/"),
            Timeout = TimeSpan.FromMinutes(3)
        };

        public async Task InitializeAsync()
        {
            try
            {
                if (!await IsOllamaRunning())
                    throw new Exception("無法連線至 Ollama。請確認 ollama serve 已啟動。");

                var builder = Kernel.CreateBuilder();
                // 若未來硬體允許，建議將 qwen2.5:3b 升級為 qwen2.5:7b 或 llama3:8b 以獲得更好的報告品質
                builder.AddOpenAIChatCompletion("qwen2.5:3b", "ollama", httpClient: _sharedHttpClient);
                builder.AddOpenAIEmbeddingGenerator("nomic-embed-text", "ollama", httpClient: _sharedHttpClient);

                _kernel = builder.Build();
                _chat = _kernel.GetRequiredService<IChatCompletionService>();
                _embedding = _kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

                await LoadKnowledgeBaseAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IntelliOps 初始化失敗: {ex.Message}");
                throw;
            }
        }

        private async Task<bool> IsOllamaRunning()
        {
            try { var response = await _sharedHttpClient.GetAsync("http://127.0.0.1:11434/"); return response.IsSuccessStatusCode; }
            catch { return false; }
        }

        private async Task LoadKnowledgeBaseAsync()
        {
            var seeds = new List<KnowledgeItem> {
                new KnowledgeItem { ErrorPattern = "0x80040154", Solution = "COM 元件未註冊。建議重新安裝相關依賴或執行 regsvr32 重新註冊 dll。" },
                new KnowledgeItem { ErrorPattern = "java.lang.OutOfMemoryError: Java heap space", Solution = "JVM 記憶體不足。建議修改啟動腳本，將 -Xmx 參數調高，或檢查是否有 Memory Leak。" },
                new KnowledgeItem { ErrorPattern = "Connection refused: connect", Solution = "無法連線至目標 Port。請檢查目標 Server 防火牆設定或服務是否已啟動。" }
            };

            foreach (var item in seeds)
            {
                try
                {
                    var generated = await _embedding.GenerateAsync(new[] { item.ErrorPattern });
                    item.Embedding = generated[0].Vector.ToArray();
                    _knowledgeBase.Add(item);
                }
                catch { }
            }
        }

        public async Task<AnalysisResult> AnalyzeLogAsync(LogEventContext logContext, Action<string>? onProgress = null)
        {
            string primaryLog = logContext.PrimaryErrorLog;
            var result = new AnalysisResult();

            // ==========================================
            // 1. 前置快取防禦 (防止短時間內相同錯誤大量觸發)
            // ==========================================
            // 將 Log 轉為 Hash 作為 Cache Key (實務上可先用正則拔除時間戳)
            string cacheKey = $"Report_{primaryLog.GetHashCode()}";
            if (_reportCache.TryGetValue(cacheKey, out AnalysisResult cachedResult))
            {
                onProgress?.Invoke("⚡ [快取攔截] 此錯誤在近 10 分鐘內已分析過，直接載入歷史報告...");
                return cachedResult;
            }

            try
            {
                // ==========================================
                // 2. 進階 RAG：抓取 Top-K 歷史紀錄
                // ==========================================
                onProgress?.Invoke("正在檢索歷史維修知識庫...");
                var generatedQuery = await _embedding.GenerateAsync(new[] { primaryLog });
                var queryVector = generatedQuery[0].Vector;

                // 抓取相似度 > 0.75 的前 3 名
                var topMatches = _knowledgeBase
                    .Select(x => new { Item = x, Score = TensorPrimitives.CosineSimilarity(x.Embedding, queryVector.Span) })
                    .Where(x => x.Score > 0.75)
                    .OrderByDescending(x => x.Score)
                    .Take(3)
                    .ToList();

                result.MatchScore = topMatches.FirstOrDefault()?.Score ?? 0;
                StringBuilder ragContextBuilder = new StringBuilder();

                if (topMatches.Any())
                {
                    result.RagResult = topMatches[0].Item.Solution; // UI 顯示最高分的那筆
                    ragContextBuilder.AppendLine("【歷史相似維修紀錄 (供參考)】:");
                    foreach (var match in topMatches)
                    {
                        ragContextBuilder.AppendLine($"- (相似度 {match.Score:P0}): {match.Item.Solution}");
                    }
                }
                else
                {
                    result.RagResult = "無高度相符的歷史案例。";
                    ragContextBuilder.AppendLine("【歷史紀錄】: 知識庫中無相關案例。");
                }

                // ==========================================
                // 3. 雙 Agent 協作 (Actor-Critic 模式)
                // ==========================================
                onProgress?.Invoke("Agent 深度分析中...\n");

                string analyzerInstructions = "你是一位初級系統分析師。請根據錯誤日誌與上下文，找出可能的故障原因。若有提供「歷史相似維修紀錄」，請評估是否適用於本次錯誤。不需要產出最終報告，只需提供分析見解。";

                string reviewerInstructions = "你是一位資深可靠度工程師(SRE)。請審查初級分析師的意見，並嚴格按照以下格式輸出最終 RCA (根本原因) 報告：\n" +
                                              "【根本原因】: (一句話總結)\n" +
                                              "【詳細分析】: (簡述邏輯)\n" +
                                              "【修復建議】: (列出 1-3 點工程師該執行的具體步驟)";

                ChatCompletionAgent analyzerAgent = new() { Name = "Analyzer", Instructions = analyzerInstructions, Kernel = _kernel };
                ChatCompletionAgent reviewerAgent = new() { Name = "Reviewer", Instructions = reviewerInstructions, Kernel = _kernel };

                AgentGroupChat chat = new(analyzerAgent, reviewerAgent)
                {
                    ExecutionSettings = new()
                    {
                        SelectionStrategy = new SequentialSelectionStrategy(),
                        TerminationStrategy = new ReviewerTerminationStrategy() { Agents = [reviewerAgent], MaximumIterations = 2 }
                    }
                };

                // 將錯誤、上下文與 RAG 資料一起餵進 Prompt
                string prompt = $"請分析以下伺服器錯誤:\n" +
                                $"【核心錯誤】: {primaryLog}\n" +
                                $"【發生前 50 行上下文】: \n{logContext.SurroundingLogs}\n\n" +
                                $"{ragContextBuilder.ToString()}";

                chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, prompt));

                StringBuilder conversationLog = new StringBuilder();
                string finalReport = "";

                await foreach (var content in chat.InvokeAsync())
                {
                    string role = content.AuthorName == "Analyzer" ? "🔎 初步分析" : "📋 最終 RCA 報告";
                    conversationLog.Append($"[{role}]:\n{content.Content}\n\n");
                    onProgress?.Invoke(conversationLog.ToString());

                    if (content.AuthorName == "Reviewer") finalReport = content.Content ?? "";
                }

                result.AiAnalysis = conversationLog.ToString();

                // ==========================================
                // 4. 寫入快取 (保存 10 分鐘)
                // ==========================================
                _reportCache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
            }
            catch (Exception ex) { onProgress?.Invoke($"分析失敗: {ex.Message}"); }

            return result;
        }

        // ==========================================
        // 5. 反饋循環 (人類確認後，AI 學習並寫入知識庫)
        // ==========================================
        public async Task LearnNewSolutionAsync(string errorPattern, string confirmedSolution)
        {
            try
            {
                // 將新學到的錯誤轉換為向量
                var generated = await _embedding.GenerateAsync(new[] { errorPattern });

                var newItem = new KnowledgeItem
                {
                    ErrorPattern = errorPattern,
                    Solution = confirmedSolution,
                    Embedding = generated[0].Vector.ToArray()
                };

                _knowledgeBase.Add(newItem);
                Debug.WriteLine($"✅ AI 已成功將新解法寫入 RAG 知識庫！");

                // TODO: 實務上請在這裡撰寫程式碼，將 _knowledgeBase 存回實體檔案或 SQLite 中，避免系統重啟後遺忘
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 學習失敗: {ex.Message}");
            }
        }

        private class ReviewerTerminationStrategy : TerminationStrategy
        {
            protected override Task<bool> ShouldAgentTerminateAsync(Agent agent, IReadOnlyList<ChatMessageContent> history, CancellationToken cancellationToken)
                => Task.FromResult(agent.Name == "Reviewer");
        }
    }

}