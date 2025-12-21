using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Algorithms.Rebar.Utils;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.Pipeline
{
    /// <summary>
    /// Orchestrator cho việc thiết kế đồng thời nhiều dầm trong 1 tầng/dự án.
    /// Đảm bảo đồng bộ đường kính, cập nhật NeighborDesigns sau mỗi dầm.
    /// </summary>
    public class MultiBeamOrchestrator
    {
        private readonly RebarPipeline _pipeline;

        public MultiBeamOrchestrator(RebarPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        /// <summary>
        /// Thiết kế tất cả dầm trong danh sách, đồng bộ NeighborDesigns.
        /// </summary>
        /// <param name="beams">Danh sách dầm cần thiết kế (theo thứ tự ưu tiên)</param>
        /// <param name="settings">Settings người dùng</param>
        /// <param name="initialConstraints">Ràng buộc ban đầu (có thể null)</param>
        /// <returns>Dictionary: GroupName -> Best Solution</returns>
        public Dictionary<string, ContinuousBeamSolution> SolveFloor(
            List<(BeamGroup Group, List<BeamResultData> SpanResults)> beams,
            DtsSettings settings,
            ProjectConstraints initialConstraints = null)
        {
            var results = new Dictionary<string, ContinuousBeamSolution>();
            var globalConstraints = initialConstraints ?? new ProjectConstraints();


            foreach (var (group, spanResults) in beams)
            {
                // Check if this beam was locked by user
                ExternalConstraints external = null;
                if (group.LockedAt.HasValue && group.SelectedDesign != null)
                {
                    external = new ExternalConstraints
                    {
                        ForcedBackboneDiameter = group.SelectedDesign.BackboneDiameter,
                        ForcedBackboneCountTop = group.SelectedDesign.BackboneCount_Top,
                        ForcedBackboneCountBot = group.SelectedDesign.BackboneCount_Bot,
                        Source = "UserLock"
                    };

                }

                // Execute pipeline for this beam
                var proposals = _pipeline.Execute(group, spanResults, settings, globalConstraints, external);

                // V3.5 UPGRADE: Use Strategy-based selection for diverse proposals
                var selectedProposals = ProposalSelector.SelectDiverseSolutions(proposals, 5);

                if (selectedProposals.Any())
                {
                    // First proposal is the "best" according to multi-strategy ranking
                    var bestSolution = selectedProposals.First();

                    // Store all alternatives for UI display
                    bestSolution.AlternativeSolutions = selectedProposals;

                    results[group.GroupName] = bestSolution;

                    // Update NeighborDesigns for next beams to reference
                    globalConstraints.NeighborDesigns[group.GroupName] = new NeighborDesign
                    {
                        BackboneDiameter = bestSolution.BackboneDiameter,
                        BackboneCount = bestSolution.BackboneCount_Top,
                        StirrupDiameter = 10  // TODO: Get from StirrupCalculator when implemented
                    };

                    // If no preferred diameter set yet, use this beam's backbone as preferred
                    if (!globalConstraints.PreferredMainDiameter.HasValue)
                    {
                        globalConstraints.PreferredMainDiameter = bestSolution.BackboneDiameter;
                    }
                }
                else
                {
                    // ALL SOLUTIONS INVALID - Create explicit failure entry
                    // This prevents silent failures where user gets a broken design
                    var firstFailed = proposals.FirstOrDefault();
                    results[group.GroupName] = new ContinuousBeamSolution
                    {
                        OptionName = "FAILED",
                        IsValid = false,
                        ValidationMessage = firstFailed?.ValidationMessage ??
                            string.Format("CRITICAL: Không tìm được phương án hợp lệ cho dầm {0}", group.GroupName)
                    };
                }
            }


            return results;
        }

        /// <summary>
        /// Tính toán lại một dầm cụ thể (sau khi user thay đổi lock/unlock).
        /// </summary>
        public ContinuousBeamSolution RecalculateSingle(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings,
            ProjectConstraints globalConstraints)
        {
            var proposals = _pipeline.Execute(group, spanResults, settings, globalConstraints, null);
            return proposals.FirstOrDefault();
        }
    }
}
