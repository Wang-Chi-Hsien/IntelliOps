using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace IntelliOps.DataIngestion
{
    // RAG 知識庫單一項目模型 (結構需與主程式 AgentWorker 保持一致)
    public class KnowledgeItem
    {
        public string ErrorPattern { get; set; } = "";
        public string Solution { get; set; } = "";
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }

    class Program
    {
        private static readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        
        // [設定] 你的 Loghub CSV 檔案放置的資料夾目錄
        private static readonly string LogSourceFolder = @"/home/luke/IntelliOps.WPF/"; 
        
        // [設定] 輸出的在地向量資料庫實體路徑（直接塞進 AgentWorker 的執行目錄中）
        private static readonly string OutputDbPath = @"/home/luke/IntelliOps.WPF/IntelliOps.AgentWorker/qdrant_local_db.json";

        static async Task Main(string[] args)
        {
            Console.WriteLine("🚀 [Qdrant Ingestion Pipeline] 啟動中...");

            if (!Directory.Exists(LogSourceFolder))
            {
                Console.WriteLine($"❌ 找不到日誌來源資料夾: {LogSourceFolder}");
                return;
            }

            // 自動抓取該目錄下所有結構化 CSV 檔 (如 OpenSSH_2k.log_structured.csv 等)
            var csvFiles = Directory.GetFiles(LogSourceFolder, "*_structured.csv");

            if (!csvFiles.Any())
            {
                Console.WriteLine($"⚠️ 在 {LogSourceFolder} 中沒有找到任何 *_structured.csv 檔案！");
                return;
            }

            var allProcessedItems = new List<KnowledgeItem>();

            // 【已修正】移除了先前打字錯位的 university 關鍵字
            foreach (var csvPath in csvFiles)
            {
                Console.WriteLine($"\n📂 正在處理日誌源: {Path.GetFileName(csvPath)} ...");
                var lines = await File.ReadAllLinesAsync(csvPath);
                var dataLines = lines.Skip(1).ToList(); // 跳過第一行 CSV 欄位標頭

                int fileSuccessCount = 0;
                foreach (var line in dataLines)
                {
                    // 根據 Loghub 標準格式切分欄位
                    var columns = line.Split(',');
                    if (columns.Length < 9) continue;

                    string content = columns[6];       // Content 欄位 (原始錯誤訊息文字)
                    string eventId = columns[7];       // EventId 欄位 (如 E10, E19)
                    string eventTemplate = columns[8]; // EventTemplate 欄位 (正規化萬用模版)

                    try
                    {
                        // 呼叫 Ollama 本地端模型產生 768 維度向量
                        var vector = await GenerateEmbeddingAsync(content);
                        if (vector == null || vector.Length == 0) continue;

                        allProcessedItems.Add(new KnowledgeItem
                        {
                            ErrorPattern = eventId, // 以 EventId 作為 RAG 與 MCP 檢索的特徵標籤
                            Solution = $"[來源: {Path.GetFileName(csvPath)}] 模版: {eventTemplate} | 範例日誌: {content}",
                            Embedding = vector
                        });

                        fileSuccessCount++;
                        if (fileSuccessCount % 100 == 0)
                        {
                            Console.WriteLine($"⏳ [{Path.GetFileName(csvPath)}] 已加工 {fileSuccessCount}/{dataLines.Count} 筆向量...");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ 處理失敗，跳過該行。錯誤: {ex.Message}");
                    }
                }
                Console.WriteLine($"✅ 檔案 {Path.GetFileName(csvPath)} 處理完畢，成功轉換 {fileSuccessCount} 筆資料。");
            }

            // 將所有整合好的 OpenSSH 與 Linux 向量資料進行在地持久化儲存
            Console.WriteLine("\n💾 正在將全量向量資料寫入在地端 JSON 資料庫...");
            
            string? targetDir = Path.GetDirectoryName(OutputDbPath);
            if (targetDir != null) Directory.CreateDirectory(targetDir);

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            string jsonDbContent = JsonSerializer.Serialize(allProcessedItems, jsonOptions);
            await File.WriteAllTextAsync(OutputDbPath, jsonDbContent);

            Console.WriteLine($"\n🎉 [Ingestion 管線建置成功] 共 {allProcessedItems.Count} 筆日誌特徵已寫入在地 Qdrant 模擬庫！");
            Console.WriteLine($"📍 資料庫實體位置: {OutputDbPath}");
        }

        // 呼叫本機 Ollama 產生 Embeddings 向量
        private static async Task<float[]?> GenerateEmbeddingAsync(string text)
        {
            try
            {
                var requestBody = new { model = "nomic-embed-text", prompt = text };
                var response = await _httpClient.PostAsJsonAsync("api/embeddings", requestBody);
                if (!response.IsSuccessStatusCode) return null;

                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                if (doc.RootElement.TryGetProperty("embedding", out var embeddingArr))
                {
                    return embeddingArr.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                }
            }
            catch { }
            return null;
        }
    }
}