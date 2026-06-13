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
using System.Text.Json;

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

        // [新增] 宣告 MCP 審查工具包
        private IntelliOpsMcpTools _mcpTools = null!;

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

                // [修改] 升級為高階推理模型 qwen3:4b 
                builder.AddOpenAIChatCompletion("qwen3:4b", "ollama", httpClient: _sharedHttpClient);
                builder.AddOpenAIEmbeddingGenerator("nomic-embed-text", "ollama", httpClient: _sharedHttpClient);

                _kernel = builder.Build();
                _chat = _kernel.GetRequiredService<IChatCompletionService>();
                _embedding = _kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

                await LoadKnowledgeBaseAsync();

                // [新增] 初始化 MCP 審查工具包，並將其外掛元件註冊至核心 Kernel
                _mcpTools = new IntelliOpsMcpTools(_knowledgeBase, _embedding);
                _kernel.Plugins.AddFromObject(_mcpTools, "McpReviewerPlugin");
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
            // 指向剛剛 Ingestion Pipeline 算好的離線向量資料庫檔案
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qdrant_local_db.json");

            // 防呆：如果找不到離線庫，才載入原本的硬編碼基本種子
            if (!File.Exists(dbPath))
            {
                Debug.WriteLine("⚠️ [警告] 找不到離線向量庫，正在加載緊急備用種子資料...");
                var seeds = new List<KnowledgeItem> {
                    new KnowledgeItem { ErrorPattern = "E0", Solution = "緊急備用：COM 元件未註冊或連線失敗。" }
                };
                foreach (var item in seeds)
                {
                    var generated = await _embedding.GenerateAsync(new[] { item.ErrorPattern });
                    item.Embedding = generated[0].Vector.ToArray();
                    _knowledgeBase.Add(item);
                }
                return;
            }

            try
            {
                Debug.WriteLine("📂 [Qdrant 在地地端庫] 偵測到實體向量檔案，開始秒讀載入...");
                
                // 讀取實體檔案並直接反序列化成記憶體中的向量 Collection
                string jsonContent = await File.ReadAllTextAsync(dbPath);
                var loadedDb = JsonSerializer.Deserialize<List<KnowledgeItem>>(jsonContent);

                if (loadedDb != null)
                {
                    _knowledgeBase = loadedDb;
                    Debug.WriteLine($"✅ [Qdrant 在地端庫] 成功載入 { _knowledgeBase.Count } 筆包含 HNSW 比對特徵的實體向量紀錄！開機完成。");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 讀取實體資料庫檔案失敗: {ex.Message}");
            }
        }
        public async Task<AnalysisResult> AnalyzeLogAsync(LogEventContext logContext, Action<string>? onProgress = null)
        {
            string primaryLog = logContext.PrimaryErrorLog;
            var result = new AnalysisResult();

            // ==========================================
            // 1. 前置快取防禦 (防止短時間內相同錯誤大量觸發)
            // ==========================================
            string cacheKey = $"Report_{primaryLog.GetHashCode()}";
            if (_reportCache.TryGetValue(cacheKey, out AnalysisResult cachedResult))
            {
                onProgress?.Invoke("[快取攔截] 此錯誤在近 10 分鐘內已分析過，直接載入歷史報告...");
                return cachedResult;
            }

            try
            {
                // 每次啟動新 Log 分析，重置 MCP 防禦計數器與歷史紀錄
                _mcpTools.ResetSession();

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
                    ragContextBuilder.AppendLine("【歷史相似維修紀錄 (由系統自動媒合)】:");
                    foreach (var match in topMatches)
                    {
                        ragContextBuilder.AppendLine($"- (相似度 {match.Score:P0}): {match.Item.Solution}");
                    }
                }
                else
                {
                    result.RagResult = "無高度相符的歷史案例。";
                    ragContextBuilder.AppendLine("【歷史紀錄】: 知識庫中無高度相關之直接案例。");
                }

                // ==========================================
                // 3. 混合審查機制 (傳統 RAG 墊底 + MCP 主動調查)
                // ==========================================
                onProgress?.Invoke("混合審查專家啟動中（如基本盤資訊不足將自動呼叫 MCP 調查）...\n");

                string systemInstructions =
                    "你是一位資深系統可靠度工程師(SRE)，目前正身處雙層審查架構中。請務必使用『繁體中文 (台灣)』回答。\n\n" +
                    "【工作流程說明】:\n" +
                    "1. 系統已經幫你自動匹配了 1~3 筆「歷史相似維修紀錄」。\n" +
                    "2. 如果你審查這些歷史紀錄與上下文後，認為答案「已經非常足夠」，請『直接』生成最終分析報告。\n" +
                    "3. 如果你審查後發現歷史紀錄不對症、或者你懷疑有其他隱藏病因，你可以『主動呼叫 MCP 工具(SearchQdrantKnowledgeBase)』，傳入你推理出的新關鍵字進行深度調查。\n" +
                    "4. 注意：MCP 工具不可濫用。一旦你獲得足夠的補充線索，或者工具提示已達上限，必須立即停止查詢，進行最終診斷。\n\n" +
                    "請『嚴格』按照以下格式輸出最終報告，絕不可包含任何問候語或多餘的解釋文字：\n\n" +
                    "【根本原因】: (一句話總結)\n" +
                    "【詳細分析】: (簡述你的診斷邏輯與審查過程，若有動用 MCP，請一併說明調查發現)\n" +
                    "【修復建議】: (列出 1-3 點工程師該執行的具體步驟)";

                string prompt = $"請分析以下伺服器錯誤:\n" +
                                $"【核心錯誤】: {primaryLog}\n" +
                                $"【發生前 50 行上下文】: \n{logContext.SurroundingLogs}\n\n" +
                                $"{ragContextBuilder.ToString()}";

                ChatHistory chatHistory = new ChatHistory(systemInstructions);
                chatHistory.AddUserMessage(prompt);

                OpenAIPromptExecutionSettings settings = new()
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
                };

                // 呼叫大腦進行多輪推理
                var response = await _chat.GetChatMessageContentAsync(chatHistory, settings, _kernel);
                string finalReport = response.Content ?? "";

                // ======================================================================
                // 🔥【空值攔截與 Fallback 強制重試機制】
                // 如果 Semantic Kernel 回傳 null 或空白，代表 Ollama 在解析工具調用時死機了。
                // 我們立刻繞過 SK 的 Function 框架，直接用純文字模式去跟 Ollama 討答案！
                // ======================================================================
                if (string.IsNullOrWhiteSpace(finalReport) || finalReport.Contains("AI 未回傳"))
                {
                    onProgress?.Invoke("⚠️ [偵測到地端模型協定相容性留白] 啟動自動 Fallback 降級防禦管線...");
                    
                    // 改用最純粹、不帶 Function Calling 的單純 Settings
                    OpenAIPromptExecutionSettings fallbackSettings = new() { FunctionChoiceBehavior = null };
                    
                    // 重新包裝一個更強硬的 Prompt，直接塞入歷史紀錄
                    ChatHistory fallbackHistory = new ChatHistory(systemInstructions);
                    fallbackHistory.AddUserMessage($"【系統強制降級重試】由於剛剛工具調用發生阻礙，請你直接根據以下資訊，嚴格按照格式印出最終的繁體中文 RCA 報告！\n\n" + prompt);

                    var fallbackResponse = await _chat.GetChatMessageContentAsync(fallbackHistory, fallbackSettings, _kernel);
                    finalReport = fallbackResponse.Content ?? "❌ [系統致命錯誤] 地端 AI 大腦完全拒絕回應，請確認 Ollama 顯存(VRAM)是否爆掉。";
                }

                StringBuilder conversationLog = new StringBuilder();
                conversationLog.Append($"[最終 RCA 混合審查報告]:\n{finalReport}\n\n");
                onProgress?.Invoke(conversationLog.ToString());

                result.AiAnalysis = finalReport; // 修正：不要把 "[最終 RCA...]" 標頭一起塞進去，只存純文字報告

                // ==========================================
                // 4. 寫入快取 (保存 10 分鐘)
                // ==========================================
                _reportCache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"分析失敗: {ex.Message}");
                result.AiAnalysis = $"❌ 核心分析發生異常: {ex.Message}";
            }

            return result;
        }

        // ==========================================
        // 5. 反饋循環 (人類確認後，AI 學習並寫入知識庫)
        // ==========================================
        public async Task LearnNewSolutionAsync(string errorPattern, string confirmedSolution)
        {
            try
            {
                var generated = await _embedding.GenerateAsync(new[] { errorPattern });

                var newItem = new KnowledgeItem
                {
                    ErrorPattern = errorPattern,
                    Solution = confirmedSolution,
                    Embedding = generated[0].Vector.ToArray()
                };

                _knowledgeBase.Add(newItem);
                Debug.WriteLine($"✅ AI 已成功將新解法寫入 RAG 知識庫！");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 學習失敗: {ex.Message}");
            }
        }
    }
}