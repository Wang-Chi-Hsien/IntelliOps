using System;
using System.Threading;
using System.Threading.Tasks;

namespace IntelliOps.AgentWorker
{
    public static class ConsoleHelper
    {
        // 這是終端機畫面的「麥克風」，誰拿到誰才能講話
        private static readonly object _consoleLock = new object();

        // 安全地印出任何一行字，保證不會打斷別人的動畫
        public static void WriteLineSafely(string message)
        {
            lock (_consoleLock)
            {
                // 先清除當前行 (防止蓋到動畫)，印出訊息，再換行
                Console.WriteLine($"\r{message}".PadRight(Console.WindowWidth - 1));
            }
        }

        public static async Task<T> RunWithSpinnerAsync<T>(string message, Func<Task<T>> action)
        {
            using var cts = new CancellationTokenSource();
            // 在 RunWithSpinnerAsync 裡面，spinnerTask 啟動前加入這行：
            Console.WriteLine(); // 強制換到全新的一行

            var spinnerTask = Task.Run(async () =>
            {
                char[] spinner = { '|', '/', '-', '\\' };
                int counter = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    lock (_consoleLock)
                    {
                        Console.Write($"\r{message} {spinner[counter++ % 4]} ");
                    }
                    await Task.Delay(100);
                }
                lock (_consoleLock)
                {
                    Console.Write($"\r{new string(' ', message.Length + 4)}\r");
                }
            });

            try
            {
                return await action();
            }
            finally
            {
                cts.Cancel();
                await spinnerTask;
            }
        }

        public static void PrintReportSafely(string report)
        {
            lock (_consoleLock)
            {
                Console.WriteLine("\n========================================================");
                Console.WriteLine("🚨 [新錯誤攔截] AI 診斷報告完成");
                Console.WriteLine("========================================================");
                Console.WriteLine(report);
                Console.WriteLine("========================================================\n");
            }
        }
    }
}