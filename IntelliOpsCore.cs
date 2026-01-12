using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Embeddings;
using System.Diagnostics;
using System.Net.Http;
using System.Numerics.Tensors; // 記得確認 NuGet 有裝 System.Numerics.Tensors
using System.Text.RegularExpressions;

namespace IntelliOps.WPF
{
    public class IntelliOpsCore
    {
        private Kernel _kernel;
        private IChatCompletionService _chat;
        private ITextEmbeddingGenerationService _embedding;
        private List<KnowledgeItem> _knowledgeBase = new List<KnowledgeItem>();
        private string _currentLogContext = "";

        // [修正 2] HttpClient 改為 static readonly，全域共用一個實體，避免 Socket 耗盡
        private static readonly HttpClient _sharedHttpClient = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:11434/v1/"),
            Timeout = TimeSpan.FromMinutes(3) // Timeout 保持 3 分鐘
        };

        private readonly HashSet<string> _autoSafeTools = new()
        {
            "FlushDNS", "OpenBluetoothSettings", "OpenNetworkStatus",
            "OpenTaskManager", "OpenCalculator"
        };

        // 建構子 (Constructor) - 保持輕量，只做基本物件配置
        public IntelliOpsCore()
        {
            // 這裡不再呼叫 .Wait()，也不建立 HttpClient
            // 只保留 Semantic Kernel 的 Builder 準備工作，但 Build() 建議延後到 Init 或是這裡做皆可
            // 為了安全，我們將依賴注入的建立放在這裡，但網路請求相關的放在後面
        }

        // [修正 1] 新增非同步初始化方法 (Async Initialization Pattern)
        // 這是由外部 (MainWindow) 呼叫的，不會卡住 UI
        public async Task InitializeAsync()
        {
            try
            {
                var builder = Kernel.CreateBuilder();

                // 使用共用的 _sharedHttpClient
                builder.AddOpenAIChatCompletion("phi3", "ollama", httpClient: _sharedHttpClient);
                builder.AddOpenAITextEmbeddingGeneration("nomic-embed-text", "ollama", httpClient: _sharedHttpClient);

                _kernel = builder.Build();
                _chat = _kernel.GetRequiredService<IChatCompletionService>();
                _embedding = _kernel.GetRequiredService<ITextEmbeddingGenerationService>();

                // 這裡可以使用 await 了！不會死鎖！
                await LoadKnowledgeBaseAsync();
            }
            catch (Exception ex)
            {
                // 處理初始化失敗，例如 Ollama 沒開
                Debug.WriteLine($"初始化失敗: {ex.Message}");
                throw; // 拋出異常讓 UI 層知道初始化失敗
            }
        }

        private async Task LoadKnowledgeBaseAsync()
        {
            // (這段程式碼保持不變，邏輯同原版)
            var seeds = new List<KnowledgeItem> {
                new KnowledgeItem { ErrorPattern = "0x80040154", Solution = "網卡驅動異常。請檢查網路設定。", AutoAction = "OpenNetworkStatus" },
                new KnowledgeItem { ErrorPattern = "Bluetooth", Solution = "藍芽服務異常。請至設定頁面重啟藍芽。", AutoAction = "OpenBluetoothSettings" }
            };

            foreach (var item in seeds)
            {
                try
                {
                    // 這裡的網路呼叫現在是安全的
                    var emb = await _embedding.GenerateEmbeddingAsync(item.ErrorPattern);
                    item.Embedding = emb.ToArray();
                    _knowledgeBase.Add(item);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"載入知識庫項目失敗: {ex.Message}");
                }
            }
        }

        public async Task<AnalysisResult> AnalyzeLogAsync(string logMessage)
        {
            _currentLogContext = logMessage;
            var result = new AnalysisResult();

            try
            {
                // --- Phase 1: RAG 快車道 ---
                var queryEmb = await _embedding.GenerateEmbeddingAsync(logMessage);
                var best = _knowledgeBase
                    .Select(x => new { Item = x, Score = TensorPrimitives.CosineSimilarity(x.Embedding, queryEmb.Span) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                double score = best?.Score ?? 0;
                result.MatchScore = score;
                Debug.WriteLine($"Log: {logMessage.Substring(0, Math.Min(20, logMessage.Length))}... | Match: {best?.Item.ErrorPattern} | Score: {score}");

                // 門檻 0.7 + 關鍵字防呆
                if (best != null && score > 0.7)
                {
                    bool safeToAutoRun = false;
                    string action = best.Item.AutoAction;

                    if (action == "OpenBluetoothSettings" &&
                       (logMessage.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || logMessage.Contains("藍芽")))
                    {
                        safeToAutoRun = true;
                    }
                    else if (action == "OpenNetworkStatus" &&
                            (logMessage.Contains("Network", StringComparison.OrdinalIgnoreCase) || logMessage.Contains("網卡") || logMessage.Contains("連線")))
                    {
                        safeToAutoRun = true;
                    }
                    else if (action == "OpenTaskManager" &&
                            (logMessage.Contains("CPU") || logMessage.Contains("Memory") || logMessage.Contains("Slow")))
                    {
                        safeToAutoRun = true;
                    }
                    else if (action == "OpenCalculator" || action == "FlushDNS")
                    {
                        safeToAutoRun = true;
                    }

                    if (safeToAutoRun && !string.IsNullOrEmpty(action))
                    {
                        result.RagResult = best.Item.Solution;
                        result.AiAnalysis = $"[已命中歷史案例] (相似度: {score:P0})\n{best.Item.Solution}\n\n⚡ [系統自動執行] 已開啟: {action}";
                        _ = ExecuteActionAsync(action);
                        return result;
                    }
                    else
                    {
                        result.RagResult = best.Item.Solution;
                        result.AiAnalysis = $"[已命中歷史案例] (相似度: {score:P0})\n建議方案: {best.Item.Solution}";
                        result.SuggestedActions.Add(action);
                        return result;
                    }
                }

                result.RagResult = $"無高度匹配 (最高分: {score:P0})";

                // --- Phase 2: AI 維運官分析 ---
                var history = new ChatHistory();
                history.AddSystemMessage(@"你是一位資深 Windows 維運專家。

                    任務：分析錯誤 Log 並選擇最合適的工具。

                    【可用工具箱】
                    1. FlushDNS: 僅限 DNS/域名解析問題。

                    2. OpenBluetoothSettings: 僅限 藍芽/Bluetooth 相關錯誤。

                    3. OpenNetworkStatus: 僅限 網卡/IP/Wi-Fi/斷線 問題。

                    4. OpenTaskManager: 僅限 系統凍結/CPU 100%/記憶體不足。

                    5. SearchStackOverflow: 適用於 SQL 錯誤、程式碼錯誤、應用程式崩潰、Timeout、或找不到對應工具時。

                    【重要規則】
                    - 不要重複輸出規則。

                    - 不要輸出可用工具清單。

                    - 直接回答分析結果。

                    - 只能使用「繁體中文」不得使用簡體中文或中國用語

                    - 用詞需符合台灣 IT 習慣

                    【輸出格式】
                    請嚴格依照下方格式輸出（不要使用 Markdown 列表或粗體）：

                    【可能原因】: (分析 log 原因，50字以內)
                    【建議處理】: (條列式解決步驟，50字以內)
                    ACTION: <工具名稱>

                    重要：最後一行必須是 「ACTION: 工具名稱」，例如 「ACTION: OpenNetworkStatus」，若無對應工具則輸出 「ACTION: SearchStackOverflow」。");

                history.AddUserMessage($"錯誤 Log: {logMessage}");

                var settings = new OpenAIPromptExecutionSettings { Temperature = 0.1, MaxTokens = 800 };

                // [修正] 加入 try-catch 處理 Timeout 與網路例外
                string aiResponse = "";
                try
                {
                    var chatResult = await _chat.GetChatMessageContentAsync(history, settings);
                    aiResponse = chatResult.ToString();
                }
                catch (TaskCanceledException)
                {
                    aiResponse = "⚠️ AI 回應逾時 (超過 3 分鐘)，建議手動檢查或重試。";
                    result.AiAnalysis = aiResponse;
                    result.SuggestedActions.Add("SearchStackOverflow");
                    return result;
                }
                catch (Exception ex)
                {
                    aiResponse = $"⚠️ AI 連線錯誤: {ex.Message}";
                    result.AiAnalysis = aiResponse;
                    result.SuggestedActions.Add("SearchStackOverflow");
                    return result;
                }

                result.AiAnalysis = aiResponse;


                // --- Phase 3: 執行官 (強化版解析邏輯) ---

                // 步驟 A: 先嘗試原本的正規表達式抓取 (最精準)
                var actionMatch = Regex.Match(aiResponse, @"ACTION:\s*([a-zA-Z]+)", RegexOptions.RightToLeft);
                string proposedAction = actionMatch.Success ? actionMatch.Groups[1].Value : null;

                // 步驟 B: [新增] 如果抓不到 ACTION 標籤，改用「關鍵字掃描」補救
                // (AI 在建議文字裡明明提到了 OpenNetworkStatus，我們不應該無視它)
                if (string.IsNullOrEmpty(proposedAction) || proposedAction == "SearchStackOverflow")
                {
                    foreach (var tool in _autoSafeTools)
                    {
                        // 如果 AI 的回應內容中，明確出現了工具名稱，就直接採用
                        if (aiResponse.Contains(tool, StringComparison.OrdinalIgnoreCase))
                        {
                            proposedAction = tool;
                            Debug.WriteLine($"[補救機制] Regex 失敗，但從內文中掃描到工具: {tool}");
                            break;
                        }
                    }
                }

                // 步驟 C: 真的都找不到，才保底去 StackOverflow
                if (string.IsNullOrEmpty(proposedAction))
                {
                    proposedAction = "SearchStackOverflow";
                }

                if (logMessage.Contains("SQL") && proposedAction != "SearchStackOverflow") proposedAction = "SearchStackOverflow";

                if (!_autoSafeTools.Contains(proposedAction) && proposedAction != "SearchStackOverflow")
                {
                    proposedAction = "SearchStackOverflow";
                }

                if (_autoSafeTools.Contains(proposedAction))
                {
                    _ = ExecuteActionAsync(proposedAction);
                    result.AiAnalysis += $"\n\n⚡ [自動化執行] 系統已自動開啟視窗: {proposedAction}";
                }
                else
                {
                    result.AiAnalysis += $"\n\n⚠️ [建議操作] 建議執行: {proposedAction}";
                    result.SuggestedActions.Add(proposedAction);
                    if (proposedAction != "SearchStackOverflow") result.SuggestedActions.Add("SearchStackOverflow");
                }
            }
            catch (Exception ex)
            {
                result.AiAnalysis = $"系統發生未預期錯誤: {ex.Message}";
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
                    else if (action == "OpenCalculator")
                    {
                        Process.Start("calc.exe");
                    }
                    else if (action == "FlushDNS")
                    {
                        Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c ipconfig /flushdns", UseShellExecute = true, CreateNoWindow = true });
                    }
                    else if (action == "OpenBluetoothSettings")
                    {
                        Process.Start(new ProcessStartInfo { FileName = "explorer", Arguments = "ms-settings:bluetooth", UseShellExecute = true });
                    }
                    else if (action == "OpenNetworkStatus")
                    {
                        Process.Start(new ProcessStartInfo { FileName = "explorer", Arguments = "ms-settings:network-status", UseShellExecute = true });
                    }
                    else if (action == "OpenTaskManager")
                    {
                        Process.Start(new ProcessStartInfo { FileName = "taskmgr.exe", UseShellExecute = true });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Action execution failed: {ex.Message}");
                }
            });
        }
    }
}