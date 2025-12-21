using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Algorithms.Rebar.V4;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.Utils
{
    /// <summary>
    /// V3.5.2: Diagnostic logging utility for debugging rebar pipeline.
    /// Logs to both file and accumulates in-memory for display.
    /// Enable via DtsSettings.EnablePipelineLogging = true
    /// 
    /// CRITICAL: Controlled by DtsSettings.EnablePipelineLogging - không tự động enable.
    /// </summary>
    public static class RebarLogger
    {
        private static readonly object _lock = new object();
        private static StringBuilder _sessionLog = new StringBuilder();
        private static string _logPath;

        public static bool IsEnabled { get; set; } = false;

        /// <summary>
        /// Initialize logger with DtsSettings (preferred method).
        /// Automatically sets IsEnabled from settings.
        /// </summary>
        public static void Initialize(DtsSettings settings)
        {
            IsEnabled = settings?.EnablePipelineLogging ?? false;
            if (IsEnabled)
            {
                InitializeInternal(null);
                Log($"SESSION START: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Log($"Settings: SafetyFactor={settings?.Rules?.SafetyFactor ?? 1.0:F2}, " +
                    $"WastePenalty={settings?.Rules?.WastePenaltyScore ?? 20}, " +
                    $"AlignmentPenalty={settings?.Rules?.AlignmentPenaltyScore ?? 25}");
            }
        }

        /// <summary>
        /// Initialize logger with custom path.
        /// </summary>
        public static void Initialize(string basePath = null)
        {
            InitializeInternal(basePath);
        }

        private static void InitializeInternal(string basePath)
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
            Log($"  ConstructabilityScore: {sol.ConstructabilityScore:F2}");
            Log($"  TotalScore: {sol.TotalScore:F2}");
            Log($"  WastePercentage: {sol.WastePercentage:F1}%");
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

        /// <summary>
        /// Log validation result from FailFastValidator.
        /// </summary>
        public static void LogValidation(bool isValid, string message, List<string> warnings)
        {
            if (!IsEnabled) return;

            if (isValid)
            {
                Log("VALIDATION: PASSED");
                if (warnings != null && warnings.Count > 0)
                {
                    Log($"  Warnings ({warnings.Count}):");
                    foreach (var w in warnings)
                    {
                        Log($"    ⚠ {w}");
                    }
                }
            }
            else
            {
                Log($"VALIDATION: FAILED - {message}");
            }
        }

        /// <summary>
        /// Log settings being used for calculation.
        /// </summary>
        public static void LogSettings(DtsSettings settings)
        {
            if (!IsEnabled || settings == null) return;

            Log("SETTINGS:");
            Log($"  Rules.SafetyFactor: {settings.Rules?.SafetyFactor ?? 1.0:F2}");
            Log($"  Rules.WastePenaltyScore: {settings.Rules?.WastePenaltyScore ?? 20}");
            Log($"  Rules.AlignmentPenaltyScore: {settings.Rules?.AlignmentPenaltyScore ?? 25}");
            Log($"  Beam.MinClearSpacing: {settings.Beam?.MinClearSpacing ?? 30}mm");
            Log($"  Beam.MaxClearSpacing: {settings.Beam?.MaxClearSpacing ?? 200}mm");
            Log($"  Beam.MaxLayers: {settings.Beam?.MaxLayers ?? 2}");
            Log($"  Beam.MaxBarsPerLayer: {settings.Beam?.MaxBarsPerLayer ?? 8}");
            Log($"  Beam.PreferSymmetric: {settings.Beam?.PreferSymmetric ?? true}");
            Log($"  Beam.PreferFewerBars: {settings.Beam?.PreferFewerBars ?? true}");
            Log($"  Beam.PreferredDiameter: {settings.Beam?.PreferredDiameter ?? 20}mm");
        }

        public static void LogError(string message)
        {
            if (!IsEnabled) return;
            Log($"*** ERROR: {message} ***");
        }

        public static void LogWarning(string message)
        {
            if (!IsEnabled) return;
            Log($"⚠ WARNING: {message}");
        }

        public static string GetSessionLog()
        {
            return _sessionLog.ToString();
        }

        public static string GetLogPath()
        {
            return _logPath;
        }

        /// <summary>
        /// Open the log file in default text editor (Notepad, etc.)
        /// </summary>
        public static void OpenLogFile()
        {
            if (!IsEnabled || string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _logPath,
                    UseShellExecute = true
                });
            }
            catch { /* Ignore if cannot open */ }
        }

        public static void Clear()
        {
            _sessionLog.Clear();
        }

        /// <summary>
        /// Log danh sách arrangements từ một section.
        /// </summary>
        public static void LogArrangements(string sectionId, List<SectionArrangement> arrangements, string side)
        {
            if (!IsEnabled || arrangements == null) return;

            Log($"");
            Log($"SECTION: {sectionId} - {side}");
            Log($"  Total arrangements: {arrangements.Count}");

            if (arrangements.Count == 0)
            {
                Log($"  ⚠ NO ARRANGEMENTS FOUND");
                return;
            }

            // Group by diameter
            var groups = arrangements.GroupBy(a => a.PrimaryDiameter).OrderByDescending(g => g.Key);

            foreach (var group in groups)
            {
                Log($"  Diameter {group.Key}mm ({group.Count()} arrangements):");

                // SHOW ALL (không giới hạn)
                var sorted = group.OrderByDescending(a => a.Score);
                foreach (var arr in sorted)
                {
                    string config = arr.ToDisplayString();

                    Log($"    {config,-15} | " +
                        $"Area={arr.TotalArea,6:F2}cm² | " +
                        $"Score={arr.Score,5:F1} | " +
                        $"Eff={arr.Efficiency,5:F2} | " +
                        $"Spacing={arr.ClearSpacing,3:F0}mm | " +
                        $"Layers={arr.LayerCount}");
                }
            }
        }

        /// <summary>
        /// Log backbone candidates.
        /// </summary>
        public static void LogBackboneCandidates(List<BackboneCandidate> candidates, int showTop = 10)
        {
            if (!IsEnabled || candidates == null) return;

            Log($"");
            Log($"BACKBONE CANDIDATES: {candidates.Count} total");

            var valid = candidates.Where(c => c.IsGloballyValid).ToList();
            var invalid = candidates.Where(c => !c.IsGloballyValid).ToList();

            Log($"  Valid: {valid.Count} | Invalid: {invalid.Count}");

            if (valid.Count > 0)
            {
                Log($"  TOP {Math.Min(showTop, valid.Count)} VALID CANDIDATES:");
                var top = valid.OrderByDescending(c => c.TotalScore).Take(showTop);

                int rank = 1;
                foreach (var c in top)
                {
                    int totalSections = c.FitCount + c.FailedSections.Count;
                    Log($"    #{rank}: D{c.Diameter} | T:{c.CountTop} B:{c.CountBot} | " +
                        $"Score={c.TotalScore:F1} | " +
                        $"Weight={c.EstimatedWeight:F1}kg | " +
                        $"Fit={c.FitCount}/{totalSections} | " +
                        $"AreaT={c.AreaTop:F2} AreaB={c.AreaBot:F2}");
                    rank++;
                }
            }

            if (invalid.Count > 0 && invalid.Count <= 5)
            {
                Log($"  INVALID CANDIDATES:");
                foreach (var c in invalid)
                {
                    Log($"    D{c.Diameter} T:{c.CountTop} B:{c.CountBot} | " +
                        $"Failed: {string.Join(", ", c.FailedSections)}");
                }
            }
        }

        /// <summary>
        /// Log solution comparison.
        /// </summary>
        public static void LogSolutionComparison(List<ContinuousBeamSolution> solutions)
        {
            if (!IsEnabled || solutions == null || solutions.Count == 0) return;

            Log($"");
            Log($"SOLUTION COMPARISON: {solutions.Count} solutions");
            Log($"");
            Log($"  Rank | Valid | Backbone          | Weight  | Eff% | Const | Total | Waste% | Description");
            Log($"  -----|-------|-------------------|---------|------|-------|-------|--------|-------------");

            int rank = 1;
            foreach (var sol in solutions.OrderByDescending(s => s.TotalScore))
            {
                string valid = sol.IsValid ? "✓" : "✗";
                string backbone = $"T:{sol.BackboneCount_Top}D{sol.BackboneDiameter_Top} B:{sol.BackboneCount_Bot}D{sol.BackboneDiameter_Bot}";

                Log($"  {rank,4} | {valid,5} | {backbone,-17} | {sol.TotalSteelWeight,7:F1} | {sol.EfficiencyScore,4:F0} | {sol.ConstructabilityScore,5:F0} | {sol.TotalScore,5:F0} | {sol.WastePercentage,6:F1} | {sol.Description}");

                if (!sol.IsValid && !string.IsNullOrEmpty(sol.ValidationMessage))
                {
                    Log($"       └─ ⚠ {sol.ValidationMessage}");
                }

                rank++;
            }
        }
    }
}

