using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Embeddings;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

#pragma warning disable SKEXP0001, CS0618

[SupportedOSPlatform("windows")]
class Program
{
    static Kernel? _kernel;
    static IChatCompletionService? _chatService;
    static ITextEmbeddingGenerationService? _embeddingService;
    static ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
    static List<KnowledgeItem> _knowledgeBase = new List<KnowledgeItem>();

    // 定義 Log 結構，加入時間戳記以便未來分析上下文
    record LogEntry(string Message, string Source, int EventID, DateTime Time);

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== IntelliOps: 企業級 RAG 優先架構 (Demo版) ===");

        // 1. 初始化 AI (使用 Ollama)
        var builder = Kernel.CreateBuilder();

        // Chat Model (慢車道用)
        builder.AddOpenAIChatCompletion("llama3.1", "ollama", httpClient: new HttpClient { BaseAddress = new Uri("http://localhost:11434/v1/") });
        // Embedding Model (快車道 RAG 用) - 必備
        builder.AddOpenAITextEmbeddingGeneration("nomic-embed-text", "ollama", httpClient: new HttpClient { BaseAddress = new Uri("http://localhost:11434/v1/") });
        builder.Plugins.AddFromType<ActionTools>(); // 掛載執行工具

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();
        _embeddingService = _kernel.GetRequiredService<ITextEmbeddingGenerationService>();

        // 2. 載入企業知識庫 (模擬 Dell 替換檔案)
        await LoadCorporateKnowledgeBase();

        // 3. 啟動 EventLog 監聽 (抓你的 eventcreate)
        StartEventLogWatcher();

        Console.WriteLine("[系統] 監聽中... 請執行你的 batch 檔產生錯誤。");

        // 4. 主迴圈
        while (true)
        {
            if (_logQueue.TryDequeue(out LogEntry? log))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[偵測異常] ID:{log.EventID} 來源:{log.Source}");
                Console.WriteLine($"[內容] {log.Message}");
                Console.ResetColor();

                await ProcessLogAsync(log);
            }
            await Task.Delay(100);
        }
    }

    // =================================================================
    //  核心邏輯：快慢車道分流
    // =================================================================
    static async Task ProcessLogAsync(LogEntry log)
    {
        Console.WriteLine("------------------------------------------------");

        // --- Phase 1: RAG 快車道 (查詢歷史案例) ---
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("[RAG Agent] 正在比對企業歷史案例... ");

        var (bestMatch, score) = await SearchKnowledgeBaseAsync(log.Message);

        // 設定門檻值：相似度 > 0.6 視為「已知問題」
        if (score > 0.6)
        {
            Console.WriteLine($"命中! (相似度: {score:P0})");
            Console.WriteLine($"[歷史解決方案] {bestMatch?.Solution}");

            // 如果歷史方案是「自動修復」，直接轉交執行官
            if (bestMatch != null && bestMatch.AutoAction != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[Fast Track] 轉交執行官自動執行: {bestMatch.AutoAction}");
                await ExecuteAction(bestMatch.AutoAction);
            }
            else
            {
                Console.WriteLine("[建議] 請參考上述歷史方案手動處理。");
            }
            Console.ResetColor();
            return; // 結束，不浪費時間問 LLM
        }

        Console.WriteLine("無完全匹配案例 (轉入慢車道分析)。");

        // --- Phase 2: 維運官分析 (LLM 推理) ---
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[維運官] 這是新問題，正在進行邏輯分析...");

        string prompt =
            $"錯誤訊息: {log.Message}\n" +
            $"EventID: {log.EventID}\n" +
            $"參考資料(RAG): {(score > 0.3 ? bestMatch?.Solution : "無")}\n\n" +
            $"你是資深維運，請分析原因。如果這是網路問題，建議使用 'OpenCalculator' (模擬網路修復工具)。\n" +
            $"如果無法自動解決，請給出建議步驟。";

        var analysis = await _chatService!.GetChatMessageContentAsync(prompt, new OpenAIPromptExecutionSettings { Temperature = 0.3 }, _kernel);
        Console.WriteLine($"[維運官分析]: {analysis}");

        // 簡單判斷是否要觸發工具 (真實情況可用 ToolCalling)
        if (analysis.ToString().Contains("OpenCalculator") || log.Message.Contains("網路"))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[執行官] 收到指令，執行模擬修復...");
            await ExecuteAction("OpenCalculator");
        }
        Console.ResetColor();
    }

    // =================================================================
    //  工具箱 (Action Tools)
    // =================================================================
    public class ActionTools
    {
        [KernelFunction]
        public void OpenCalculator()
        {
            // 這是你要的：模擬一個真的會動的東西
            Console.WriteLine(">>> 啟動網路診斷工具 (模擬: 小算盤)...");
            Process.Start("calc.exe");
        }
    }

    static async Task ExecuteAction(string actionName)
    {
        if (actionName == "OpenCalculator")
        {
            new ActionTools().OpenCalculator();
        }
        else
        {
            Console.WriteLine($"[執行官] 未知指令: {actionName} (需人工介入)");
        }
    }

    // =================================================================
    //  RAG 搜尋模組 (支援 JSON 讀取)
    // =================================================================
    class KnowledgeItem { public string ErrorPattern { get; set; } = ""; public string Solution { get; set; } = ""; public string? AutoAction { get; set; } public float[]? Embedding { get; set; } }

    static async Task LoadCorporateKnowledgeBase()
    {
        string path = "company_knowledge.json";
        if (!File.Exists(path))
        {
            // 建立預設檔案 (Dell 可以在這裡替換)
            var defaults = new List<KnowledgeItem>
            {
                new KnowledgeItem {
                    ErrorPattern = "網路介面卡重設失敗，錯誤代碼: 0x80040154",
                    Solution = "這是已知的網卡驅動 Bug。請執行網路診斷工具進行重置。",
                    AutoAction = "OpenCalculator" // 設定這個案例會自動觸發小算盤
                },
                new KnowledgeItem {
                    ErrorPattern = "Outlook 0xc0000005 Access Violation",
                    Solution = "增益集衝突。請以安全模式啟動 (outlook /safe)。",
                    AutoAction = null
                }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(defaults));
        }

        string json = File.ReadAllText(path);
        _knowledgeBase = JsonSerializer.Deserialize<List<KnowledgeItem>>(json) ?? new List<KnowledgeItem>();

        // 預先計算向量
        Console.Write("[系統] 正在向量化知識庫... ");
        foreach (var item in _knowledgeBase)
        {
            var emb = await _embeddingService!.GenerateEmbeddingAsync(item.ErrorPattern);
            item.Embedding = emb.ToArray();
        }
        Console.WriteLine("完成。");
    }

    static async Task<(KnowledgeItem?, float)> SearchKnowledgeBaseAsync(string query)
    {
        var queryEmb = await _embeddingService!.GenerateEmbeddingAsync(query);
        var best = _knowledgeBase
            .Select(x => new { Item = x, Score = TensorPrimitives.CosineSimilarity(x.Embedding, queryEmb.Span) })
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        return best != null ? (best.Item, best.Score) : (null, 0f);
    }

    // =================================================================
    //  EventLog 監聽器 (專門抓 eventcreate)
    // =================================================================
    static void StartEventLogWatcher()
    {
        EventLog myLog = new EventLog("Application");
        myLog.EnableRaisingEvents = true;
        myLog.EntryWritten += (sender, e) =>
        {
            // [修改] 只要是 Error 就抓，不管來源是不是 IntelliOpsDemo
            // 注意：這可能會導致你的畫面被大量真實錯誤洗版
            if (e.Entry.EntryType == EventLogEntryType.Error)
            {
                // 過濾掉一些太頻繁的系統雜訊 (選用)
                if (e.Entry.Source == "Microsoft-Windows-Perflib") return;

                _logQueue.Enqueue(new LogEntry(e.Entry.Message, e.Entry.Source, e.Entry.EventID, e.Entry.TimeGenerated));
            }
        };
    }
}