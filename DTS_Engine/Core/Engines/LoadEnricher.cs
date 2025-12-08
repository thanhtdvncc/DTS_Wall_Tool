using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using DTS_Engine.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Engines
{
    /// <summary>
    /// Stage 1: Load Enrichment - Calculates geometry properties immediately after reading from SAP.
    /// 
    /// RESPONSIBILITIES (ISO/IEC 25010 - Single Responsibility):
    /// - Calculate GlobalCenter (Centroid for areas, Midpoint for frames, Exact for points)
    /// - Determine Grid Location based on GlobalCenter
    /// - Update VectorKey for grouping
    /// 
    /// PERFORMANCE:
    /// - Uses cached geometry dictionaries for O(1) lookup
    /// - Single pass through all loads
    /// - Grids cached once per enrichment session
    /// </summary>
    public class LoadEnricher
    {
        #region Constants

        private const double GRID_SNAP_TOLERANCE = 250.0; // mm

        #endregion

        #region Cached Data

        private Dictionary<string, SapArea> _areaCache;
        private Dictionary<string, SapFrame> _frameCache;
        private Dictionary<string, SapUtils.SapPoint> _pointCache;
        private List<SapUtils.GridLineRecord> _xGrids;
        private List<SapUtils.GridLineRecord> _yGrids;
        private bool _isCacheBuilt;

        #endregion

        #region Public API

        /// <summary>
        /// Enrich all loads with geometry data and grid locations.
        /// This should be called immediately after reading raw loads from SAP.
        /// </summary>
        /// <param name="loads">Raw loads from SapDatabaseReader</param>
        public void EnrichLoads(List<RawSapLoad> loads)
        {
            if (loads == null || loads.Count == 0) return;

            BuildCacheIfNeeded();

            foreach (var load in loads)
            {
                EnrichSingleLoad(load);
            }
        }

        /// <summary>
        /// Clear cached data to force rebuild on next enrichment.
        /// </summary>
        public void ClearCache()
        {
            _areaCache = null;
            _frameCache = null;
            _pointCache = null;
            _xGrids = null;
            _yGrids = null;
            _isCacheBuilt = false;
        }

        #endregion

        #region Private Methods

        private void BuildCacheIfNeeded()
        {
            if (_isCacheBuilt) return;

            // Cache grids
            var allGrids = SapUtils.GetGridLines();
            _xGrids = allGrids.Where(g => g.Orientation == "X").OrderBy(g => g.Coordinate).ToList();
            _yGrids = allGrids.Where(g => g.Orientation == "Y").OrderBy(g => g.Coordinate).ToList();

            // Cache geometry with case-insensitive keys
            _areaCache = SapUtils.GetAllAreasGeometry()
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            _frameCache = SapUtils.GetAllFramesGeometry()
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            _pointCache = SapUtils.GetAllPoints()
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            _isCacheBuilt = true;
        }

        private void EnrichSingleLoad(RawSapLoad load)
        {
            if (load == null) return;

            // 1. Calculate GlobalCenter
            load.GlobalCenter = CalculateGlobalCenter(load);

            // 2. Calculate Grid Location
            if (load.GlobalCenter != null &&
                (Math.Abs(load.GlobalCenter.X) > 1e-6 || Math.Abs(load.GlobalCenter.Y) > 1e-6))
            {
                load.PreCalculatedGridLoc = CalculateGridLocation(load.GlobalCenter);
            }
            else
            {
                load.PreCalculatedGridLoc = "Unknown";
            }

            // 3. Update VectorKey if not already set
            if (string.IsNullOrEmpty(load.VectorKey))
            {
                load.UpdateVectorKey();
            }
        }

        private Point2D CalculateGlobalCenter(RawSapLoad load)
        {
            string name = load.ElementName;

            switch (load.LoadType)
            {
                case "AreaUniform":
                case "AreaUniformToFrame":
                    if (_areaCache.TryGetValue(name, out var area))
                    {
                        return CalculateAreaCentroid(area);
                    }
                    break;

                case "FrameDistributed":
                case "FramePoint":
                    if (_frameCache.TryGetValue(name, out var frame))
                    {
                        return frame.Midpoint;
                    }
                    break;

                case "PointForce":
                    if (_pointCache.TryGetValue(name, out var pt))
                    {
                        return new Point2D(pt.X, pt.Y);
                    }
                    break;
            }

            return new Point2D(0, 0);
        }

        private Point2D CalculateAreaCentroid(SapArea area)
        {
            if (area.BoundaryPoints == null || area.BoundaryPoints.Count == 0)
                return new Point2D(0, 0);

            double sumX = 0, sumY = 0;
            foreach (var p in area.BoundaryPoints)
            {
                sumX += p.X;
                sumY += p.Y;
            }
            return new Point2D(sumX / area.BoundaryPoints.Count, sumY / area.BoundaryPoints.Count);
        }

        private string CalculateGridLocation(Point2D center)
        {
            if (_xGrids.Count == 0 && _yGrids.Count == 0) return "No Grid";

            string xRange = _xGrids.Count > 0 ? FindClosestGridName(_xGrids, center.X) : "?";
            string yRange = _yGrids.Count > 0 ? FindClosestGridName(_yGrids, center.Y) : "?";

            return $"{xRange}/{yRange}";
        }

        private string FindClosestGridName(List<SapUtils.GridLineRecord> grids, double value)
        {
            if (grids.Count == 0) return "?";

            SapUtils.GridLineRecord closest = null;
            double minDiff = double.MaxValue;

            foreach (var g in grids)
            {
                double diff = Math.Abs(g.Coordinate - value);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = g;
                }
            }

            if (closest == null) return "?";

            double offset = value - closest.Coordinate;
            return FormatGridWithOffset(closest.Name, offset);
        }

        private string FormatGridWithOffset(string name, double offsetMm)
        {
            if (Math.Abs(offsetMm) < GRID_SNAP_TOLERANCE) return name;
            double offsetM = offsetMm / 1000.0;
            return $"{name}({offsetM:+0.#;-0.#}m)";
        }

        #endregion

        #region Public Accessors for Grid Information

        /// <summary>
        /// Get X grids for external use (e.g., bounding box calculation)
        /// </summary>
        public IReadOnlyList<SapUtils.GridLineRecord> XGrids
        {
            get
            {
                BuildCacheIfNeeded();
                return _xGrids;
            }
        }

        /// <summary>
        /// Get Y grids for external use
        /// </summary>
        public IReadOnlyList<SapUtils.GridLineRecord> YGrids
        {
            get
            {
                BuildCacheIfNeeded();
                return _yGrids;
            }
        }

        /// <summary>
        /// Get area geometry by name
        /// </summary>
        public SapArea GetAreaGeometry(string name)
        {
            BuildCacheIfNeeded();
            _areaCache.TryGetValue(name, out var area);
            return area;
        }

        /// <summary>
        /// Get frame geometry by name
        /// </summary>
        public SapFrame GetFrameGeometry(string name)
        {
            BuildCacheIfNeeded();
            _frameCache.TryGetValue(name, out var frame);
            return frame;
        }

        /// <summary>
        /// Get grid range description for a bounding box
        /// </summary>
        public string GetGridRangeForBoundingBox(double minX, double maxX, double minY, double maxY)
        {
            BuildCacheIfNeeded();

            string xRange = FormatAxisRange(minX, maxX, _xGrids);
            string yRange = FormatAxisRange(minY, maxY, _yGrids);

            if (string.IsNullOrEmpty(xRange) && string.IsNullOrEmpty(yRange)) return "No Grid";
            if (string.IsNullOrEmpty(xRange)) return yRange;
            if (string.IsNullOrEmpty(yRange)) return xRange;

            return $"{xRange}/{yRange}";
        }

        private string FormatAxisRange(double minVal, double maxVal, List<SapUtils.GridLineRecord> grids)
        {
            if (grids == null || grids.Count == 0) return "?";

            var startGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - minVal)).First();
            var endGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - maxVal)).First();

            double startDiff = minVal - startGrid.Coordinate;
            double endDiff = maxVal - endGrid.Coordinate;

            if (startGrid.Name == endGrid.Name)
            {
                return FormatGridWithOffset(startGrid.Name, startDiff);
            }

            string startStr = FormatGridWithOffset(startGrid.Name, startDiff);
            string endStr = FormatGridWithOffset(endGrid.Name, endDiff);

            // Clean up for ranges - remove offset if small
            string cleanStart = Math.Abs(startDiff) < GRID_SNAP_TOLERANCE ? startGrid.Name : startStr;
            string cleanEnd = Math.Abs(endDiff) < GRID_SNAP_TOLERANCE ? endGrid.Name : endStr;

            return $"{cleanStart}-{cleanEnd}";
        }

        #endregion
    }
}
