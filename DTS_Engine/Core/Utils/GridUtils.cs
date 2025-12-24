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
                return $"~{minVal / 1000.0:F1}m";

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
            string startPart = FormatGridWithOffset(startGrid.Name, startDiff);
            string endPart = FormatGridWithOffset(endGrid.Name, endDiff);
            
            // Clean up: if both are simple names, just show range
            if (!startPart.Contains("(") && !endPart.Contains("("))
                return $"{startPart}-{endPart}";
            
            // Otherwise show full format
            return $"{startPart}-{endPart}";
        }

        /// <summary>
        /// Format grid name with offset if significant.
        /// </summary>
        /// <param name="name">Grid name (e.g., "A", "1")</param>
        /// <param name="offsetMm">Offset from grid line in mm</param>
        /// <returns>Formatted name like "A" or "A(-1.2m)"</returns>
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
        /// Generate UNIQUE group display name using Grid System from SAP.
        /// Format: "{Type} {PrimaryAxis} x {CrossAxisRange} @Z={LevelZ}"
        /// 
        /// Example outputs (like AuditEngine):
        /// - "Girder D x 1-12 @Z=19500"        (Girder on axis D, spanning grids 1-12)
        /// - "Girder D(-1.5m) x 2-11 @Z=19500" (Girder offset from D, spanning 2-11)
        /// - "Beam 3 x A-E @Z=19500"           (Beam on axis 3, spanning A-E)
        /// - "Beam x(A-C) @Z=19500"            (Off-axis beam spanning A-C)
        /// 
        /// CRITICAL: This ensures groups on same axis but different locations get UNIQUE names.
        /// </summary>
        /// <param name="groupType">Group type: "Girder" or "Beam"</param>
        /// <param name="direction">Direction: "X" or "Y"</param>
        /// <param name="axisName">Primary axis name (e.g., "E", "3") - may be empty for off-grid beams</param>
        /// <param name="crossAxisMin">Minimum cross-axis coordinate (mm)</param>
        /// <param name="crossAxisMax">Maximum cross-axis coordinate (mm)</param>
        /// <param name="levelZ">Elevation in mm</param>
        /// <param name="crossAxisGrids">Cross-axis grid lines for range naming (REQUIRED for proper naming)</param>
        /// <param name="primaryAxisGrids">Primary axis grid lines (for offset calculation)</param>
        /// <param name="primaryAxisCoord">Coordinate on primary axis (for offset calculation)</param>
        /// <returns>Unique formatted group name</returns>
        public static string GenerateUniqueGroupName(
            string groupType,
            string direction,
            string axisName,
            double crossAxisMin,
            double crossAxisMax,
            double levelZ,
            List<SapUtils.GridLineRecord> crossAxisGrids,
            List<SapUtils.GridLineRecord> primaryAxisGrids = null,
            double primaryAxisCoord = 0)
        {
            string prefix = groupType ?? "Beam";

            // === BUILD CROSS-AXIS RANGE ===
            string crossRange = FindAxisRange(crossAxisMin, crossAxisMax, crossAxisGrids);
            
            // Clean the range for display
            if (crossRange.Contains("-"))
            {
                // Already a range like "1-12" or "A-E"
            }
            else if (!crossRange.Contains("("))
            {
                // Single grid like "3" - still show as is
            }

            // === BUILD PRIMARY AXIS WITH OFFSET ===
            string axisDisplay;
            if (!string.IsNullOrEmpty(axisName))
            {
                // Check if we need to show offset from named axis
                if (primaryAxisGrids != null && primaryAxisGrids.Count > 0)
                {
                    var matchedGrid = primaryAxisGrids.FirstOrDefault(g => 
                        g.Name.Equals(axisName, StringComparison.OrdinalIgnoreCase));
                    
                    if (matchedGrid != null)
                    {
                        double offset = primaryAxisCoord - matchedGrid.Coordinate;
                        axisDisplay = FormatGridWithOffset(axisName, offset);
                    }
                    else
                    {
                        axisDisplay = axisName;
                    }
                }
                else
                {
                    axisDisplay = axisName;
                }
            }
            else
            {
                // No axis name - this is an off-grid beam
                // Find nearest grid on primary axis
                if (primaryAxisGrids != null && primaryAxisGrids.Count > 0)
                {
                    var nearest = primaryAxisGrids.OrderBy(g => Math.Abs(g.Coordinate - primaryAxisCoord)).First();
                    double offset = primaryAxisCoord - nearest.Coordinate;
                    axisDisplay = FormatGridWithOffset(nearest.Name, offset);
                }
                else
                {
                    // Fallback: use coordinate
                    axisDisplay = $"~{primaryAxisCoord / 1000.0:F1}m";
                }
            }

            // === BUILD FINAL NAME ===
            // Format: "{Type} {PrimaryAxis} x {CrossRange} @Z={Level}"
            string name;
            if (!string.IsNullOrEmpty(axisDisplay) && axisDisplay != "?")
            {
                name = $"{prefix} {axisDisplay} x {crossRange} @Z={levelZ:F0}";
            }
            else
            {
                // Fallback for completely off-grid
                name = $"{prefix} x({crossRange}) @Z={levelZ:F0}";
            }

            return name;
        }

        /// <summary>
        /// Ensure GroupName is unique within a collection by appending counter if needed.
        /// Called after initial name generation to handle edge cases where geometry ranges overlap.
        /// </summary>
        /// <param name="baseName">Initial generated name</param>
        /// <param name="existingNames">Set of already-used names</param>
        /// <returns>Unique name (baseName or baseName #2, #3, etc.)</returns>
        public static string EnsureUniqueGroupName(string baseName, HashSet<string> existingNames)
        {
            if (existingNames == null || !existingNames.Contains(baseName))
                return baseName;

            // Append counter
            int counter = 2;
            string candidate;
            do
            {
                candidate = $"{baseName} #{counter}";
                counter++;
            } while (existingNames.Contains(candidate));

            return candidate;
        }

        /// <summary>
        /// Generate axis-based group display name.
        /// Format: "{Type}{Direction}-{AxisName} ({spanCount} spans)"
        /// Example: "GX-A (3 spans)", "BY-3 (2 spans)"
        /// 
        /// NOTE: This is the LEGACY format - use GenerateUniqueGroupName for new code.
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
