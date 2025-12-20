using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Algorithms.Rebar.Pipeline;
using DTS_Engine.Core.Algorithms.Rebar.Pipeline.Stages;
using DTS_Engine.Core.Algorithms.Rebar.Rules;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar
{
    /// <summary>
    /// V3.0 Entry Point - Pipeline-based Rebar Calculator.
    /// Thin wrapper that composes the pipeline from stages and rules.
    /// 
    /// Usage:
    ///     var calculator = new RebarCalculatorV3();
    ///     var solutions = calculator.Calculate(group, spanResults, settings);
    /// </summary>
    public class RebarCalculatorV3
    {
        private readonly RebarPipeline _pipeline;

        /// <summary>
        /// Create V3 calculator with default stages and rules.
        /// </summary>
        public RebarCalculatorV3()
        {
            // Create stages in order
            var stages = new List<IRebarPipelineStage>
            {
                new ScenarioGenerator(),      // Stage 1: Generate backbone scenarios
                new ReinforcementFiller()     // Stage 2: Fill reinforcement per span
                // Stage 3: StirrupCalculator (V3.1)
                // Stage 4: ConflictResolver (V3.1)
            };

            // Create rule engine with default rules
            var rules = new List<IDesignRule>
            {
                new PyramidRule(),            // Priority 1: Critical - L2 <= L1
                new SymmetryRule(),           // Priority 5: Warning - Prefer even counts
                new PreferredDiameterRule(),  // Priority 10: Info - Diameter matching
                new WastePenaltyRule()        // Priority 15: Warning - Penalize waste bars
            };

            var ruleEngine = new RuleEngine(rules);

            _pipeline = new RebarPipeline(stages, ruleEngine);
        }

        /// <summary>
        /// Create V3 calculator with custom pipeline.
        /// </summary>
        public RebarCalculatorV3(RebarPipeline customPipeline)
        {
            _pipeline = customPipeline;
        }

        /// <summary>
        /// Calculate proposals for a BeamGroup.
        /// </summary>
        /// <param name="group">Beam group to calculate</param>
        /// <param name="spanResults">SAP analysis results per span</param>
        /// <param name="settings">User settings</param>
        /// <param name="projectConstraints">Optional project-level constraints</param>
        /// <returns>Top 5 solutions ranked by TotalScore</returns>
        public List<ContinuousBeamSolution> Calculate(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings,
            ProjectConstraints projectConstraints = null)
        {
            return _pipeline.Execute(group, spanResults, settings, projectConstraints ?? new ProjectConstraints(), null);
        }

        /// <summary>
        /// Calculate with external constraints (for locked beams or multi-beam sync).
        /// </summary>
        public List<ContinuousBeamSolution> CalculateWithConstraints(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings,
            ProjectConstraints projectConstraints,
            ExternalConstraints externalConstraints)
        {
            return _pipeline.Execute(group, spanResults, settings, projectConstraints ?? new ProjectConstraints(), externalConstraints);
        }

        /// <summary>
        /// Static helper matching V2 signature for easy migration.
        /// </summary>
        public static List<ContinuousBeamSolution> CalculateProposalsForGroup(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings)
        {
            // Check feature flag
            if (settings.General?.UseV3Pipeline != true)
            {
                // Fallback to V2 (intentional, suppress obsolete warning)
#pragma warning disable CS0618
                return RebarCalculator.CalculateProposalsForGroup(group, spanResults, settings);
#pragma warning restore CS0618
            }

            var calculator = new RebarCalculatorV3();
            return calculator.Calculate(group, spanResults, settings);
        }
    }
}
