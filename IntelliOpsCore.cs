using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Diagnostics;
using System.Net.Http;
using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using System.Text;

#pragma warning disable SKEXP0110 
#pragma warning disable SKEXP0001

namespace IntelliOps.WPF
{
    public class IntelliOpsCore
    {
        private Kernel _kernel = null!;
        private IChatCompletionService _chat = null!;
        private IEmbeddingGenerator<string, Embedding<float>> _embedding = null!;

        private List<KnowledgeItem> _knowledgeBase = new List<KnowledgeItem>();
        private string _currentLogContext = "";

        // 設定 Ollama 本機連線
        private static readonly HttpClient _sharedHttpClient = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/v1/"),
            Timeout = TimeSpan.FromMinutes(3)
        };

        private readonly HashSet<string> _autoSafeTools = new()
        {
            "FlushDNS", "OpenBluetoothSettings", "OpenNetworkStatus",
            "OpenTaskManager", "OpenCalculator"
        };

        public async Task InitializeAsync()
        {
            try
            {
                if (!await IsOllamaRunning())
                {
                    throw new Exception("無法連線至 Ollama。\n請確認您已執行 'ollama serve' 且模型已下載 (qwen2.5:3b)。");
                }

                var builder = Kernel.CreateBuilder();

                // [修改] 改用指令跟隨能力較強的 qwen2.5:3b 
                builder.AddOpenAIChatCompletion("qwen2.5:3b", "ollama", httpClient: _sharedHttpClient);

                // Embedding 模型 (若 qwen3-embedding 不存在，可改回 nomic-embed-text)
                builder.AddOpenAIEmbeddingGenerator("nomic-embed-text", "ollama", httpClient: _sharedHttpClient);

                _kernel = builder.Build();
                _chat = _kernel.GetRequiredService<IChatCompletionService>();
                _embedding = _kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

                await LoadKnowledgeBaseAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化失敗: {ex.Message}");
                throw;
            }
        }

        private async Task<bool> IsOllamaRunning()
        {
            try
            {
                var response = await _sharedHttpClient.GetAsync("http://127.0.0.1:11434/");
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async Task LoadKnowledgeBaseAsync()
        {
            // 範例種子資料
            var seeds = new List<KnowledgeItem> {
                new KnowledgeItem { ErrorPattern = "0x80040154", Solution = "網卡驅動異常。請檢查網路設定。", AutoAction = "OpenNetworkStatus" },
                new KnowledgeItem { ErrorPattern = "Bluetooth", Solution = "藍芽服務異常。請至設定頁面重啟藍芽。", AutoAction = "OpenBluetoothSettings" }
            };

            foreach (var item in seeds)
            {
                try
                {
                    var generated = await _embedding.GenerateAsync(new[] { item.ErrorPattern });
                    item.Embedding = generated[0].Vector.ToArray();
                    _knowledgeBase.Add(item);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"載入知識庫失敗: {ex.Message}");
                }
            }
        }

        // [修改] 新增 Action<string> onProgress 參數，用於串流回傳
        public async Task<AnalysisResult> AnalyzeLogAsync(string logMessage, Action<string>? onProgress = null)
        {
            _currentLogContext = logMessage;
            var result = new AnalysisResult();

            try
            {
                // --- Phase 1: RAG 快車道 ---
                // 為了讓 UI 有反應，先回傳初始狀態
                onProgress?.Invoke("正在檢索知識庫 (RAG Search)...");

                var generatedQuery = await _embedding.GenerateAsync(new[] { logMessage });
                var queryVector = generatedQuery[0].Vector;

                var best = _knowledgeBase
                    .Select(x => new { Item = x, Score = TensorPrimitives.CosineSimilarity(x.Embedding, queryVector.Span) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                double score = best?.Score ?? 0;
                result.MatchScore = score;

                // 若命中率極高，直接返回
                if (best != null && score > 0.75)
                {
                    result.RagResult = best.Item.Solution;
                    string hitMsg = $"[已命中歷史案例] (相似度: {score:P0})\n{best.Item.Solution}\n\n⚡ [系統自動執行] 已開啟: {best.Item.AutoAction}";

                    result.AiAnalysis = hitMsg;
                    onProgress?.Invoke(hitMsg); // 更新 UI

                    result.SuggestedActions.Add(best.Item.AutoAction);
                    _ = ExecuteActionAsync(best.Item.AutoAction);
                    return result;
                }

                string ragContext = (best != null && score > 0.6)
                    ? $"參考知識庫案例 (相似度 {score:P0}): {best.Item.Solution}"
                    : "無相關歷史案例。";

                result.RagResult = ragContext;

                // --- Phase 2: Agent-to-Agent 協作 ---

                onProgress?.Invoke("啟動 AI 探員進行分析...\n");

                ChatCompletionAgent detectiveAgent = new()
                {
                    Name = "Detective",
                    Instructions =
                        @"你是一位資深 Windows 系統分析師。
                        你的任務是:
                        1.閱讀錯誤日誌 (Log) 與參考資訊 (RAG Context)分析問題的根本原因。
                        2.只能使用「繁體中文」，不得使用簡體中文或中國用語。
                        3.請直接講重點，不要打招呼，字數控制在 50 字以內。",
                    Kernel = _kernel
                };

                // [重點修改] SysAdmin 的 Prompt 更加嚴格，防止廢話
                ChatCompletionAgent sysAdminAgent = new()
                {
                    Name = "SysAdmin",
                    Instructions =
                        @"你是一位自動化維運機器人。你的唯一任務是根據分析結果選擇一個工具。
                        【可用工具箱】: FlushDNS, OpenBluetoothSettings, OpenNetworkStatus, OpenTaskManager, SearchStackOverflow
                        
                        嚴格遵守以下輸出規則：
                        1. 不要輸出任何問候語、解釋或客套話。
                        2. 你的輸出為說明該如何解決上述問題且必需包含一行格式指令。
                        3. 格式指令必須嚴格為：ACTION: <ToolName>
                        
                        範例輸出：
                        ACTION: FlushDNS",
                    Kernel = _kernel
                };

                AgentGroupChat chat = new(detectiveAgent, sysAdminAgent)
                {
                    ExecutionSettings = new()
                    {
                        SelectionStrategy = new SequentialSelectionStrategy(),
                        TerminationStrategy = new SysAdminTerminationStrategy()
                        {
                            Agents = [sysAdminAgent],
                            MaximumIterations = 2
                        }
                    }
                };

                string prompt = $"錯誤 Log: {logMessage}\n{ragContext}";
                chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, prompt));

                StringBuilder conversationLog = new StringBuilder();
                string finalResponse = "";

                // [關鍵修改] 串流處理 Loop
                await foreach (var content in chat.InvokeAsync())
                {
                    string roleName = content.AuthorName == "Detective" ? "🔍 分析師" : "🛠️ 維修工";

                    // 格式化輸出
                    string newContent = $"[{roleName}]:\n{content.Content}\n\n";

                    conversationLog.Append(newContent);

                    // 即時通知 UI 更新
                    onProgress?.Invoke(conversationLog.ToString());

                    if (content.AuthorName == "SysAdmin")
                    {
                        finalResponse = content.Content ?? "";
                    }
                }

                result.AiAnalysis = conversationLog.ToString();

                // --- Phase 3: 執行官 (解析與執行) ---

                // Regex 解析 (從右邊找，防止前面有干擾字元)
                var actionMatch = Regex.Match(finalResponse, @"ACTION:\s*([a-zA-Z]+)", RegexOptions.RightToLeft | RegexOptions.IgnoreCase);
                string proposedAction = actionMatch.Success ? actionMatch.Groups[1].Value : "";

                // 安全過濾
                if (string.IsNullOrEmpty(proposedAction) || !_autoSafeTools.Contains(proposedAction))
                {
                    proposedAction = "SearchStackOverflow";
                }

                // 特殊規則：SQL 相關通常查 StackOverflow 比較快
                if (logMessage.Contains("SQL", StringComparison.OrdinalIgnoreCase))
                    proposedAction = "SearchStackOverflow";

                if (_autoSafeTools.Contains(proposedAction))
                {
                    string autoMsg = $"\n⚡ [自動化執行] 系統已自動開啟視窗: {proposedAction}";
                    result.AiAnalysis += autoMsg;
                    onProgress?.Invoke(result.AiAnalysis); // 最後更新一次 UI

                    _ = ExecuteActionAsync(proposedAction);
                }
                else
                {
                    string suggestMsg = $"\n⚠️ [建議操作] 建議執行: {proposedAction}";
                    result.AiAnalysis += suggestMsg;
                    onProgress?.Invoke(result.AiAnalysis); // 最後更新一次 UI

                    result.SuggestedActions.Add(proposedAction);
                    if (proposedAction != "SearchStackOverflow") result.SuggestedActions.Add("SearchStackOverflow");
                }
            }
            catch (Exception ex)
            {
                result.AiAnalysis = $"Agent 協作發生錯誤: {ex.Message}";
                result.SuggestedActions.Add("SearchStackOverflow");
                onProgress?.Invoke(result.AiAnalysis);
            }

            return result;
        }

        public async Task ExecuteActionAsync(string action)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (action == "SearchStackOverflow")
                    {
                        string query = _currentLogContext;
                        var errorCodeMatch = Regex.Match(_currentLogContext, @"(0x[0-9A-Fa-f]+)|(ExitCode:\s*\d+)");
                        if (errorCodeMatch.Success) query = errorCodeMatch.Value;
                        Process.Start(new ProcessStartInfo { FileName = $"https://stackoverflow.com/search?q={Uri.EscapeDataString(query)}", UseShellExecute = true });
                    }
                    else if (action == "OpenCalculator") Process.Start("calc.exe");
                    else if (action == "FlushDNS") Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c ipconfig /flushdns", UseShellExecute = true, CreateNoWindow = true });
                    else if (action == "OpenBluetoothSettings") Process.Start(new ProcessStartInfo { FileName = "explorer", Arguments = "ms-settings:bluetooth", UseShellExecute = true });
                    else if (action == "OpenNetworkStatus") Process.Start(new ProcessStartInfo { FileName = "explorer", Arguments = "ms-settings:network-status", UseShellExecute = true });
                    else if (action == "OpenTaskManager") Process.Start(new ProcessStartInfo { FileName = "taskmgr.exe", UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Action execution failed: {ex.Message}");
                }
            });
        }

        private class SysAdminTerminationStrategy : TerminationStrategy
        {
            protected override Task<bool> ShouldAgentTerminateAsync(Agent agent, IReadOnlyList<ChatMessageContent> history, CancellationToken cancellationToken)
            {
                return Task.FromResult(agent.Name == "SysAdmin");
            }
        }
    }
}