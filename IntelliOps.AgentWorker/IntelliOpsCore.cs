using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System;
using System.Collections.Concurrent; // [新增] 支援執行緒安全集合
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

namespace IntelliOps.AgentWorker
{
    public class IntelliOpsCore
    {
        private Kernel _kernel = null!;
        private IChatCompletionService _chat = null!;
        private IEmbeddingGenerator<string, Embedding<float>> _embedding = null!;

        private List<KnowledgeItem> _knowledgeBase = new List<KnowledgeItem>();

        // [修改] 使用 ConcurrentDictionary 確保多執行緒安全
        private readonly ConcurrentDictionary<string, (DateTime Expire, AnalysisResult Result)> _semanticCache = new();

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
                    throw new Exception("無法連線至 Ollama。");
                }

                var builder = Kernel.CreateBuilder();
                builder.AddOpenAIChatCompletion("qwen2.5:3b", "ollama", httpClient: _sharedHttpClient);
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
            try { var response = await _sharedHttpClient.GetAsync("http://127.0.0.1:11434/"); return response.IsSuccessStatusCode; }
            catch { return false; }
        }

        private async Task LoadKnowledgeBaseAsync()
        {
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
                catch { }
            }
        }

        public async Task<AnalysisResult> AnalyzeLogAsync(LogEventContext logContext, Action<string>? onProgress = null)
        {
            // [關鍵] 不再使用類別變數 _currentLogContext，直接使用區域變數，避免執行緒污染
            string primaryLog = logContext.PrimaryErrorLog;
            var result = new AnalysisResult();

            // 1. 快取檢查 (執行緒安全)
            if (_semanticCache.TryGetValue(primaryLog, out var cacheHit) && cacheHit.Expire > DateTime.Now)
            {
                onProgress?.Invoke("⚡ [系統快取] 偵測到重複錯誤，已直接載入最近的分析結果...");
                return cacheHit.Result;
            }

            try
            {
                onProgress?.Invoke("正在比對 RAG 知識庫...");

                var generatedQuery = await _embedding.GenerateAsync(new[] { primaryLog });
                var queryVector = generatedQuery[0].Vector;

                var best = _knowledgeBase
                    .Select(x => new { Item = x, Score = TensorPrimitives.CosineSimilarity(x.Embedding, queryVector.Span) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                double score = best?.Score ?? 0;
                result.MatchScore = score;

                if (best != null && score > 0.75)
                {
                    result.RagResult = best.Item.Solution;
                    string hitMsg = $"[RAG 命中] (相似度: {score:P0})\n{best.Item.Solution}";
                    result.AiAnalysis = hitMsg;
                    onProgress?.Invoke(hitMsg);
                    result.SuggestedActions.Add(best.Item.AutoAction);

                    // [修改] 傳遞當前 Log 給執行函數，不共用全域變數
                    _ = ExecuteActionAsync(best.Item.AutoAction, primaryLog);

                    _semanticCache.TryAdd(primaryLog, (DateTime.Now.AddSeconds(60), result));
                    return result;
                }

                string ragContext = (best != null && score > 0.6) ? best.Item.Solution : "無直接案例。";
                result.RagResult = ragContext;

                onProgress?.Invoke("Agent 深度分析中...\n");

                // [優化] 讀取外部指令並加強「焦點鎖定」
                string detectiveInstructions = File.Exists("Skills/Detective.md") ? File.ReadAllText("Skills/Detective.md") : "你是一位分析師。";
                string sysAdminInstructions = File.Exists("Skills/SysAdmin.md") ? File.ReadAllText("Skills/SysAdmin.md") : "你是一位維修機器人。";

                ChatCompletionAgent detectiveAgent = new() { Name = "Detective", Instructions = detectiveInstructions, Kernel = _kernel };
                ChatCompletionAgent sysAdminAgent = new() { Name = "SysAdmin", Instructions = sysAdminInstructions, Kernel = _kernel };

                AgentGroupChat chat = new(detectiveAgent, sysAdminAgent)
                {
                    ExecutionSettings = new()
                    {
                        SelectionStrategy = new SequentialSelectionStrategy(),
                        TerminationStrategy = new SysAdminTerminationStrategy() { Agents = [sysAdminAgent], MaximumIterations = 2 }
                    }
                };

                // [重點] 在 Prompt 中強化焦點鎖定，防止上下文污染
                string prompt = $"【分析焦點 (核心錯誤)】: {primaryLog}\n" +
                                $"【參考上下文 (僅供輔助)】: \n{logContext.SurroundingLogs}\n" +
                                "注意：若上下文與核心錯誤無關，請完全忽略。";

                chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, prompt));

                StringBuilder conversationLog = new StringBuilder();
                string finalResponse = "";

                await foreach (var content in chat.InvokeAsync())
                {
                    string role = content.AuthorName == "Detective" ? "🔍 分析" : "🛠️ 動作";
                    conversationLog.Append($"[{role}]:\n{content.Content}\n\n");
                    onProgress?.Invoke(conversationLog.ToString());
                    if (content.AuthorName == "SysAdmin") finalResponse = content.Content ?? "";
                }

                result.AiAnalysis = conversationLog.ToString();

                var actionMatch = Regex.Match(finalResponse, @"ACTION:\s*([a-zA-Z]+)", RegexOptions.RightToLeft | RegexOptions.IgnoreCase);
                string proposedAction = actionMatch.Success ? actionMatch.Groups[1].Value : "SearchStackOverflow";

                if (_autoSafeTools.Contains(proposedAction))
                {
                    onProgress?.Invoke(result.AiAnalysis + $"\n⚡ 自動執行: {proposedAction}");
                    _ = ExecuteActionAsync(proposedAction, primaryLog);
                }
                else
                {
                    result.SuggestedActions.Add(proposedAction);
                }

                _semanticCache.TryAdd(primaryLog, (DateTime.Now.AddSeconds(60), result));
            }
            catch (Exception ex) { onProgress?.Invoke($"分析失敗: {ex.Message}"); }

            return result;
        }

        // [修改] 接受 primaryLog 參數，不再依賴全域變數
        public async Task ExecuteActionAsync(string action, string primaryLog)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (action == "SearchStackOverflow")
                    {
                        var errorCodeMatch = Regex.Match(primaryLog, @"(0x[0-9A-Fa-f]+)|(ExitCode:\s*\d+)");
                        string q = errorCodeMatch.Success ? errorCodeMatch.Value : primaryLog;
                        Process.Start(new ProcessStartInfo { FileName = $"https://stackoverflow.com/search?q={Uri.EscapeDataString(q)}", UseShellExecute = true });
                    }
                    else if (action == "OpenCalculator") Process.Start("calc.exe");
                    else if (action == "FlushDNS") Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c ipconfig /flushdns", UseShellExecute = true, CreateNoWindow = true });
                    else if (action == "OpenBluetoothSettings") Process.Start(new ProcessStartInfo { FileName = "explorer", Arguments = "ms-settings:bluetooth", UseShellExecute = true });
                    else if (action == "OpenNetworkStatus") Process.Start(new ProcessStartInfo { FileName = "explorer", Arguments = "ms-settings:network-status", UseShellExecute = true });
                    else if (action == "OpenTaskManager") Process.Start(new ProcessStartInfo { FileName = "taskmgr.exe", UseShellExecute = true });
                }
                catch { }
            });
        }

        private class SysAdminTerminationStrategy : TerminationStrategy
        {
            protected override Task<bool> ShouldAgentTerminateAsync(Agent agent, IReadOnlyList<ChatMessageContent> history, CancellationToken cancellationToken)
                => Task.FromResult(agent.Name == "SysAdmin");
        }
    }
}