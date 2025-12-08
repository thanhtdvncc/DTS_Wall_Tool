using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using DTS_Engine.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Engines
{
    /// <summary>
    /// Stage 3: Load Grouping - Groups enriched loads by Story, Location, LoadType, and Vector.
    /// 
    /// GROUPING HIERARCHY:
    /// 1. Story (by elevation)
    /// 2. LoadType (AreaUniform, FrameDistributed, PointForce)
    /// 3. Location (PreCalculatedGridLoc from enrichment)
    /// 4. UnitLoad + Direction (to separate push/pull wind, etc.)
    /// 
    /// Each group calculates vector sums (Fx, Fy, Fz) for accurate force reporting.
    /// </summary>
    public class LoadGrouper
    {
        #region Constants

        private const double STORY_TOLERANCE = 500.0; // mm
        private const double LOAD_VALUE_TOLERANCE = 0.01; // kN/m or kN/m²

        #endregion

        #region Fields

        private List<SapUtils.GridStoryItem> _stories;

        #endregion

        #region Public API

        /// <summary>
        /// Group loads into hierarchical structure for reporting.
        /// </summary>
        public List<StoryBucket> GroupLoads(List<RawSapLoad> loads)
        {
            if (loads == null || loads.Count == 0)
                return new List<StoryBucket>();

            // Cache stories
            _stories = SapUtils.GetStories();

            // Step 1: Group by Story/Elevation
            var storyBuckets = GroupByStory(loads);

            // Step 2: For each story, group by LoadType
            foreach (var storyBucket in storyBuckets)
            {
                storyBucket.LoadTypeBuckets = GroupByLoadType(storyBucket.Loads);
            }

            return storyBuckets;
        }

        #endregion

        #region Private Methods - Story Grouping

        private List<StoryBucket> GroupByStory(List<RawSapLoad> loads)
        {
            var result = new List<StoryBucket>();

            // Determine story elevations from loads
            var elevationGroups = loads
                .GroupBy(l => RoundToStoryElevation(l.ElementZ))
                .OrderByDescending(g => g.Key)
                .ToList();

            foreach (var group in elevationGroups)
            {
                double elevation = group.Key;
                string storyName = GetStoryNameForElevation(elevation);

                result.Add(new StoryBucket
                {
                    StoryName = storyName,
                    Elevation = elevation,
                    Loads = group.ToList()
                });
            }

            return result;
        }

        private double RoundToStoryElevation(double z)
        {
            if (_stories == null || _stories.Count == 0)
            {
                // Round to nearest 100mm
                return Math.Round(z / 100.0) * 100.0;
            }

            // Find closest story
            double closest = z;
            double minDiff = double.MaxValue;

            foreach (var story in _stories)
            {
                double diff = Math.Abs(story.Elevation - z);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = story.Elevation;
                }
            }

            return minDiff < STORY_TOLERANCE ? closest : Math.Round(z / 100.0) * 100.0;
        }

        private string GetStoryNameForElevation(double elevation)
        {
            if (_stories == null || _stories.Count == 0)
            {
                return $"Z={elevation:0}";
            }

            var match = _stories.FirstOrDefault(s => Math.Abs(s.Elevation - elevation) < STORY_TOLERANCE);
            return match != null ? match.StoryName : $"Z={elevation:0}";
        }

        #endregion

        #region Private Methods - LoadType Grouping

        private List<LoadTypeBucket> GroupByLoadType(List<RawSapLoad> loads)
        {
            var result = new List<LoadTypeBucket>();

            var typeGroups = loads.GroupBy(l => NormalizeLoadType(l.LoadType));

            foreach (var typeGroup in typeGroups)
            {
                var bucket = new LoadTypeBucket
                {
                    LoadType = typeGroup.Key,
                    DisplayName = GetLoadTypeDisplayName(typeGroup.Key)
                };

                // Group by Location + UnitLoad + Direction
                bucket.LocationBuckets = GroupByLocationAndValue(typeGroup.ToList());

                result.Add(bucket);
            }

            // Sort by type order: Area > Frame > Point
            result = result.OrderBy(b => GetLoadTypeOrder(b.LoadType)).ToList();

            return result;
        }

        private string NormalizeLoadType(string loadType)
        {
            if (string.IsNullOrEmpty(loadType)) return "Unknown";
            if (loadType.StartsWith("Area")) return "Area";
            if (loadType.StartsWith("Frame") && !loadType.Contains("Point")) return "Frame";
            if (loadType.Contains("Point") || loadType.Contains("Joint")) return "Point";
            return loadType;
        }

        private string GetLoadTypeDisplayName(string loadType)
        {
            switch (loadType)
            {
                case "Area": return "Area Loads";
                case "Frame": return "Frame Distributed";
                case "Point": return "Point Loads";
                default: return loadType;
            }
        }

        private int GetLoadTypeOrder(string loadType)
        {
            switch (loadType)
            {
                case "Area": return 1;
                case "Frame": return 2;
                case "Point": return 3;
                default: return 99;
            }
        }

        #endregion

        #region Private Methods - Location + Value Grouping

        private List<LocationBucket> GroupByLocationAndValue(List<RawSapLoad> loads)
        {
            var result = new List<LocationBucket>();

            // REFACTORED: Group ONLY by Location + Direction (ignore minor UnitLoad differences)
            // This ensures all elements at same location are merged together
            var groups = loads
                .GroupBy(l => CreateLocationDirectionKey(l))
                .ToList();

            foreach (var group in groups)
            {
                var loadList = group.ToList();
                var firstLoad = loadList[0];

                // Calculate weighted average UnitLoad
                double totalWeight = 0;
                double weightedSum = 0;
                foreach (var load in loadList)
                {
                    double weight = Math.Abs(load.Value1);
                    weightedSum += load.Value1 * weight;
                    totalWeight += weight;
                }
                double avgUnitLoad = totalWeight > 0 ? Math.Abs(weightedSum / totalWeight) : Math.Abs(firstLoad.Value1);

                // Calculate vector sums
                double sumFx = 0, sumFy = 0, sumFz = 0;
                var elements = new HashSet<string>();

                foreach (var load in loadList)
                {
                    sumFx += load.DirectionX;
                    sumFy += load.DirectionY;
                    sumFz += load.DirectionZ;
                    elements.Add(load.ElementName);
                }

                result.Add(new LocationBucket
                {
                    GridLocation = firstLoad.PreCalculatedGridLoc ?? "Unknown",
                    UnitLoad = avgUnitLoad,
                    Direction = GetDirectionDisplay(firstLoad),
                    DirectionSign = GetDirectionSign(firstLoad),
                    Loads = loadList,
                    Elements = elements.ToList(),
                    VectorFx = sumFx,
                    VectorFy = sumFy,
                    VectorFz = sumFz
                });
            }

            // Sort by location (natural sort)
            return result.OrderBy(b => b.GridLocation).ToList();
        }

        /// <summary>
        /// NEW: Create key based on Location + Direction ONLY.
        /// Ignores minor UnitLoad differences to allow proper merging of elements at same location.
        /// </summary>
        private string CreateLocationDirectionKey(RawSapLoad load)
        {
            string location = load.PreCalculatedGridLoc ?? "Unknown";
            
            // Determine primary direction axis
            double absX = Math.Abs(load.DirectionX);
            double absY = Math.Abs(load.DirectionY);
            double absZ = Math.Abs(load.DirectionZ);
            
            string dirAxis;
            if (absZ >= absX && absZ >= absY)
                dirAxis = load.DirectionZ < 0 ? "Z-" : "Z+";
            else if (absX >= absY)
                dirAxis = load.DirectionX < 0 ? "X-" : "X+";
            else
                dirAxis = load.DirectionY < 0 ? "Y-" : "Y+";
            
            return $"{location}|{dirAxis}";
        }

        private string GetDirectionDisplay(RawSapLoad load)
        {
            // Determine primary axis
            double absX = Math.Abs(load.DirectionX);
            double absY = Math.Abs(load.DirectionY);
            double absZ = Math.Abs(load.DirectionZ);

            if (absZ >= absX && absZ >= absY)
            {
                return load.DirectionZ < 0 ? "-Z (Gravity)" : "+Z (Uplift)";
            }
            if (absX >= absY)
            {
                return load.DirectionX < 0 ? "-X" : "+X";
            }
            return load.DirectionY < 0 ? "-Y" : "+Y";
        }

        private double GetDirectionSign(RawSapLoad load)
        {
            // Return sign of dominant component
            double absX = Math.Abs(load.DirectionX);
            double absY = Math.Abs(load.DirectionY);
            double absZ = Math.Abs(load.DirectionZ);

            if (absZ >= absX && absZ >= absY)
                return Math.Sign(load.DirectionZ);
            if (absX >= absY)
                return Math.Sign(load.DirectionX);
            return Math.Sign(load.DirectionY);
        }

        #endregion
    }

    #region Data Structures

    /// <summary>
    /// Bucket for loads at a specific story/elevation
    /// </summary>
    public class StoryBucket
    {
        public string StoryName { get; set; }
        public double Elevation { get; set; }
        public List<RawSapLoad> Loads { get; set; } = new List<RawSapLoad>();
        public List<LoadTypeBucket> LoadTypeBuckets { get; set; } = new List<LoadTypeBucket>();

        // Calculated totals
        public double TotalFx => LoadTypeBuckets.Sum(b => b.TotalFx);
        public double TotalFy => LoadTypeBuckets.Sum(b => b.TotalFy);
        public double TotalFz => LoadTypeBuckets.Sum(b => b.TotalFz);
        public double TotalForce => Math.Sqrt(TotalFx * TotalFx + TotalFy * TotalFy + TotalFz * TotalFz);
    }

    /// <summary>
    /// Bucket for loads of a specific type (Area/Frame/Point)
    /// </summary>
    public class LoadTypeBucket
    {
        public string LoadType { get; set; }
        public string DisplayName { get; set; }
        public List<LocationBucket> LocationBuckets { get; set; } = new List<LocationBucket>();

        // Calculated totals
        public double TotalFx => LocationBuckets.Sum(b => b.TotalFx);
        public double TotalFy => LocationBuckets.Sum(b => b.TotalFy);
        public double TotalFz => LocationBuckets.Sum(b => b.TotalFz);
        public double TotalForce => Math.Sqrt(TotalFx * TotalFx + TotalFy * TotalFy + TotalFz * TotalFz);
    }

    /// <summary>
    /// Bucket for loads at a specific location with same UnitLoad and Direction
    /// </summary>
    public class LocationBucket
    {
        public string GridLocation { get; set; }
        public double UnitLoad { get; set; }
        public string Direction { get; set; }
        public double DirectionSign { get; set; }
        public List<RawSapLoad> Loads { get; set; } = new List<RawSapLoad>();
        public List<string> Elements { get; set; } = new List<string>();

        // Pre-calculated vector sums (from raw data direction vectors)
        public double VectorFx { get; set; }
        public double VectorFy { get; set; }
        public double VectorFz { get; set; }

        // These will be calculated after geometry processing
        public double Quantity { get; set; } // Area m² or Length m
        public string QuantityUnit { get; set; }
        public string Explanation { get; set; }

        // Final force components: Quantity × UnitLoad × DirectionVector
        public double TotalFx { get; set; }
        public double TotalFy { get; set; }
        public double TotalFz { get; set; }
        public double TotalForce => Math.Sqrt(TotalFx * TotalFx + TotalFy * TotalFy + TotalFz * TotalFz);
    }

    #endregion
}
