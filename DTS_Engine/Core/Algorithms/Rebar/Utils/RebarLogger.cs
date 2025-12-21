using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.Utils
{
    /// <summary>
    /// V3.5.2: Diagnostic logging utility for debugging rebar pipeline.
    /// Logs to both file and accumulates in-memory for display.
    /// Enable via DtsSettings.EnablePipelineLogging = true
    /// </summary>
    public static class RebarLogger
    {
        private static readonly object _lock = new object();
        private static StringBuilder _sessionLog = new StringBuilder();
        private static string _logPath;

        public static bool IsEnabled { get; set; } = false;

        public static void Initialize(string basePath = null)
        {
            _sessionLog.Clear();
            _logPath = basePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DTS_Engine", "Logs", $"RebarPipeline_{DateTime.Now:yyyyMMdd_HHmmss}.log"
            );

            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        public static void Log(string message)
        {
            if (!IsEnabled) return;
            lock (_lock)
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                _sessionLog.AppendLine(line);

                try
                {
                    if (!string.IsNullOrEmpty(_logPath))
                        File.AppendAllText(_logPath, line + Environment.NewLine);
                }
                catch { /* Ignore file write errors */ }
            }
        }

        public static void LogPhase(string phaseName)
        {
            Log($"═══════════════════════════════════════════════════════════════");
            Log($"  {phaseName}");
            Log($"═══════════════════════════════════════════════════════════════");
        }

        public static void LogScenario(SolutionContext ctx)
        {
            if (!IsEnabled) return;
            Log($"SCENARIO: {ctx.ScenarioId}");
            Log($"  Backbone Top: {ctx.TopBackboneCount}D{ctx.TopBackboneDiameter}");
            Log($"  Backbone Bot: {ctx.BotBackboneCount}D{ctx.BotBackboneDiameter}");
            Log($"  BeamWidth: {ctx.BeamWidth:F0}mm");
        }

        public static void LogSpanRequirements(int spanIndex, string spanId,
            double[] topArea, double[] botArea, double safetyFactor)
        {
            if (!IsEnabled) return;
            Log($"  SPAN {spanIndex}: {spanId}");
            Log($"    TopArea [L/M/R] = [{string.Join(", ", topArea?.Select(a => $"{a:F2}") ?? new[] { "null" })}] cm²");
            Log($"    BotArea [L/M/R] = [{string.Join(", ", botArea?.Select(a => $"{a:F2}") ?? new[] { "null" })}] cm²");
            Log($"    SafetyFactor = {safetyFactor:F2}");
        }

        public static void LogSupportDesign(int supportIndex, bool isTop,
            double reqLeft, double reqRight, double maxReq, double backboneArea)
        {
            if (!IsEnabled) return;
            string position = isTop ? "TOP" : "BOT";
            Log($"  SUPPORT {supportIndex} ({position}):");
            Log($"    Req_Left={reqLeft:F2}, Req_Right={reqRight:F2} → Max={maxReq:F2} cm²");
            Log($"    BackboneArea={backboneArea:F2} cm²");
            Log($"    Deficit={Math.Max(0, maxReq - backboneArea):F2} cm²");
        }

        public static void LogDesignResult(string key, int diameter, int count, int layer)
        {
            if (!IsEnabled) return;
            if (count == 0)
                Log($"    → {key}: NO ADDON NEEDED");
            else
                Log($"    → {key}: {count}D{diameter} @ Layer {layer}");
        }

        public static void LogMidSpanDesign(string spanId, bool isTop,
            double reqArea, double backboneArea, int addonCount, int addonDia)
        {
            if (!IsEnabled) return;
            string position = isTop ? "TOP" : "BOT";
            Log($"  MIDSPAN {spanId} ({position}):");
            Log($"    ReqArea={reqArea:F2}, BackboneArea={backboneArea:F2}, Deficit={Math.Max(0, reqArea - backboneArea):F2}");
            if (addonCount > 0)
                Log($"    → Addon: {addonCount}D{addonDia}");
            else
                Log($"    → NO ADDON NEEDED");
        }

        public static void LogSolutionSummary(ContinuousBeamSolution sol)
        {
            if (!IsEnabled || sol == null) return;
            Log($"SOLUTION SUMMARY: {sol.OptionName}");
            Log($"  Valid: {sol.IsValid}");
            Log($"  TotalWeight: {sol.TotalSteelWeight:F1} kg");
            Log($"  EfficiencyScore: {sol.EfficiencyScore:F2}");
            Log($"  Reinforcements ({sol.Reinforcements?.Count ?? 0}):");

            if (sol.Reinforcements != null)
            {
                foreach (var kvp in sol.Reinforcements.OrderBy(k => k.Key))
                {
                    var spec = kvp.Value;
                    Log($"    {kvp.Key}: {spec.Count}D{spec.Diameter} (Layer {spec.Layer})");
                }
            }
        }

        public static void LogError(string message)
        {
            if (!IsEnabled) return;
            Log($"*** ERROR: {message} ***");
        }

        public static string GetSessionLog()
        {
            return _sessionLog.ToString();
        }

        public static void Clear()
        {
            _sessionLog.Clear();
        }
    }
}
