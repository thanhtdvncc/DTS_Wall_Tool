using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Shared Grid Utility Functions for beam naming and location detection.
    /// Extracted from AuditEngine for reuse in BeamGroupDetector, LinkCommands, etc.
    /// </summary>
    public static class GridUtils
    {
        /// <summary>
        /// Default tolerance for snapping to grid lines (mm)
        /// </summary>
        public const double GRID_SNAP_TOLERANCE_MM = 200.0;

        /// <summary>
        /// Find the axis range (grid names) for a min-max coordinate range.
        /// </summary>
        /// <param name="minVal">Minimum coordinate (mm)</param>
        /// <param name="maxVal">Maximum coordinate (mm)</param>
        /// <param name="grids">List of grid lines to search</param>
        /// <param name="isPoint">If true, treat as single point (min=max)</param>
        /// <returns>Grid range string like "A", "1-3", "A(+1.2m)-B"</returns>
        public static string FindAxisRange(double minVal, double maxVal,
            List<SapUtils.GridLineRecord> grids, bool isPoint = false)
        {
            if (grids == null || grids.Count == 0)
                return $"(~{minVal:0}..{maxVal:0})";

            // Find nearest start grid
            var startGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - minVal)).First();
            double startDiff = minVal - startGrid.Coordinate;

            if (isPoint || Math.Abs(maxVal - minVal) < GRID_SNAP_TOLERANCE_MM)
            {
                // Single point or very small range - return just the grid name
                return FormatGridWithOffset(startGrid.Name, startDiff);
            }

            // Find nearest end grid
            var endGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - maxVal)).First();
            double endDiff = maxVal - endGrid.Coordinate;

            if (startGrid.Name == endGrid.Name)
                return FormatGridWithOffset(startGrid.Name, startDiff);

            // Range format: "A-B" or "1-5"
            return $"{FormatGridWithOffset(startGrid.Name, startDiff)}-{FormatGridWithOffset(endGrid.Name, endDiff)}";
        }

        /// <summary>
        /// Format grid name with offset if significant.
        /// </summary>
        /// <param name="name">Grid name (e.g., "A", "1")</param>
        /// <param name="offsetMm">Offset from grid line in mm</param>
        /// <returns>Formatted name like "A" or "A(+1.2m)"</returns>
        public static string FormatGridWithOffset(string name, double offsetMm)
        {
            if (Math.Abs(offsetMm) < GRID_SNAP_TOLERANCE_MM) return name;
            double offsetM = offsetMm / 1000.0;
            return $"{name}({offsetM:+0.#;-0.#}m)";
        }

        /// <summary>
        /// Find the nearest grid line to a coordinate.
        /// </summary>
        /// <param name="coord">Coordinate value (mm)</param>
        /// <param name="grids">List of grid lines</param>
        /// <returns>Name of nearest grid, or "?" if no grids</returns>
        public static string FindNearestGrid(double coord, List<SapUtils.GridLineRecord> grids)
        {
            if (grids == null || grids.Count == 0) return "?";
            var nearest = grids.OrderBy(g => Math.Abs(g.Coordinate - coord)).First();
            return nearest.Name;
        }

        /// <summary>
        /// Generate axis-based group display name.
        /// Format: "{Type}{Direction}-{AxisName} ({spanCount} spans)"
        /// Example: "GX-A (3 spans)", "BY-3 (2 spans)"
        /// </summary>
        /// <param name="groupType">Group type: "Girder" or "Beam"</param>
        /// <param name="direction">Direction: "X" or "Y"</param>
        /// <param name="axisName">Primary axis name (e.g., "A", "3")</param>
        /// <param name="spanCount">Number of spans in group</param>
        /// <returns>Formatted group name</returns>
        public static string GenerateGroupDisplayName(string groupType, string direction, string axisName, int spanCount)
        {
            string prefix = groupType == "Girder" ? "G" : "B";
            string dir = direction?.ToUpper() ?? "X";

            if (!string.IsNullOrEmpty(axisName))
            {
                return $"{prefix}{dir}-{axisName} ({spanCount} spans)";
            }
            return $"{prefix}{dir} ({spanCount} spans)";
        }

        /// <summary>
        /// Generate smart group name with cross-axis range.
        /// Format: "{Type} {AxisName} x ({CrossAxisStart}-{CrossAxisEnd})"
        /// Example: "Girder 3 x (A-D)", "Beam A x (1-5)"
        /// </summary>
        public static string GenerateSmartGroupName(
            string groupType,
            string primaryAxisName,
            double crossAxisMin,
            double crossAxisMax,
            List<SapUtils.GridLineRecord> crossAxisGrids)
        {
            string crossRange = FindAxisRange(crossAxisMin, crossAxisMax, crossAxisGrids);

            // Clean up offset notation for display
            string cleanStart = crossRange.Split('-')[0].Split('(')[0];
            string cleanEnd = crossRange.Contains("-")
                ? crossRange.Split('-').Last().Split('(')[0]
                : cleanStart;

            string rangeDisplay = cleanStart == cleanEnd ? cleanStart : $"{cleanStart}-{cleanEnd}";

            return $"{groupType} {primaryAxisName} x ({rangeDisplay})";
        }
    }
}
