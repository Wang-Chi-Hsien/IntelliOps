using System;
using System.Text.RegularExpressions;

namespace IntelliOps.AgentWorker
{
    public class SyslogParsedResult
    {
        public bool IsCritical { get; set; }
        public int Facility { get; set; }
        public LogSeverity Severity { get; set; }
        public string SeverityLabel { get; set; } = "";
        public string CoreMessage { get; set; } = "";
    }

    public class LinuxSyslogParser
    {
        // 抓取標準 Linux Syslog PRI 標頭，例如 "<35>"
        private static readonly Regex SyslogPriRegex = new Regex(@"^<(\d+)>(.*)", RegexOptions.Compiled);

        public static SyslogParsedResult ParseLine(string rawLine)
        {
            var result = new SyslogParsedResult { IsCritical = false, CoreMessage = rawLine };

            var match = SyslogPriRegex.Match(rawLine);
            if (match.Success)
            {
                int pri = int.Parse(match.Groups[1].Value);
                string cleanLog = match.Groups[2].Value;

                int severityCode = pri & 7; // 位元運算提取低 3 位
                result.Severity = (LogSeverity)severityCode;
                result.Facility = pri >> 3;  // 右移 3 位提取來源组組件

                result.SeverityLabel = result.Severity.ToString();
                result.CoreMessage = cleanLog.Trim();

                // 根據工業標準：Severity <= 3 (Emergency, Alert, Critical, Error) 判定為致命事件
                if (result.Severity <= LogSeverity.Error)
                {
                    result.IsCritical = true;
                }
            }
            else
            {
                // 如果日誌不帶標準網路協定 PRI 標頭，自動退化為關鍵字本地安全過濾
                result.CoreMessage = ExtractFallbackCoreMessage(rawLine);
                result.Severity = LogSeverity.Info;
                result.SeverityLabel = "Info";

                result.IsCritical = rawLine.Contains("Failed password", StringComparison.OrdinalIgnoreCase) || 
                                   rawLine.Contains("Invalid user", StringComparison.OrdinalIgnoreCase) ||
                                   rawLine.Contains("POSSIBLE BREAK-IN", StringComparison.OrdinalIgnoreCase) ||
                                   rawLine.Contains("Out of memory", StringComparison.OrdinalIgnoreCase) ||
                                   rawLine.Contains("Killed process", StringComparison.OrdinalIgnoreCase) ||
                                   rawLine.Contains("authentication failure", StringComparison.OrdinalIgnoreCase);
                
                if (result.IsCritical)
                {
                    result.Severity = LogSeverity.Error;
                    result.SeverityLabel = "Error";
                }
            }

            return result;
        }

        private static string ExtractFallbackCoreMessage(string rawLine)
        {
            int syslogHeaderIndex = rawLine.IndexOf("]:");
            if (syslogHeaderIndex != -1) return rawLine.Substring(syslogHeaderIndex + 2).Trim();

            int kernelHeaderIndex = rawLine.IndexOf("kernel:");
            if (kernelHeaderIndex != -1)
            {
                int closingBracketIndex = rawLine.IndexOf("]", kernelHeaderIndex);
                if (closingBracketIndex != -1) return rawLine.Substring(closingBracketIndex + 1).Trim();
                return rawLine.Substring(kernelHeaderIndex + 7).Trim();
            }

            return rawLine.Trim();
        }
    }
}