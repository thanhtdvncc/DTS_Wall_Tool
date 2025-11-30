using DTS_Wall_Tool.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using static DTS_Wall_Tool.Core.Geometry;

namespace DTS_Wall_Tool.DataStructures
{
    /// <summary>
    /// Spatial hash grid for fast 2D proximity queries
    /// O(1) average lookup vs O(n) linear search
    /// </summary>
    public class SpatialHash<T>
    {
        private readonly double _cellSize;
        private readonly Dictionary<long, List<SpatialEntry<T>>> _grid;
        private readonly Func<T, Point2D> _getPosition;
        private readonly Func<T, GeoAlgo.BoundingBox> _getBounds;

        public int Count { get; private set; }

        /// <summary>
        /// Create spatial hash with given cell size
        /// </summary>
        /// <param name="cellSize">Size of each grid cell (should match typical query radius)</param>
        /// <param name="getPosition">Function to get position of an item (for point queries)</param>
        /// <param name="getBounds">Function to get bounding box (for line segments)</param>
        public SpatialHash(double cellSize,
            Func<T, Point2D> getPosition = null,
            Func<T, GeoAlgo.BoundingBox> getBounds = null)
        {
            _cellSize = cellSize > 0 ? cellSize : 1000;
            _grid = new Dictionary<long, List<SpatialEntry<T>>>();
            _getPosition = getPosition;
            _getBounds = getBounds;
            Count = 0;
        }

        /// <summary>
        /// Insert item at position
        /// </summary>
        public void Insert(T item, Point2D position)
        {
            long key = GetCellKey(position);

            if (!_grid.ContainsKey(key))
                _grid[key] = new List<SpatialEntry<T>>();

            _grid[key].Add(new SpatialEntry<T> { Item = item, Position = position });
            Count++;
        }

        /// <summary>
        /// Insert item with bounding box (for line segments)
        /// </summary>
        public void InsertWithBounds(T item, GeoAlgo.BoundingBox bounds)
        {
            // Get all cells that the bounds overlaps
            int minCellX = (int)Math.Floor(bounds.MinX / _cellSize);
            int maxCellX = (int)Math.Floor(bounds.MaxX / _cellSize);
            int minCellY = (int)Math.Floor(bounds.MinY / _cellSize);
            int maxCellY = (int)Math.Floor(bounds.MaxY / _cellSize);

            var center = new Point2D((bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2);

            for (int cx = minCellX; cx <= maxCellX; cx++)
            {
                for (int cy = minCellY; cy <= maxCellY; cy++)
                {
                    long key = GetCellKey(cx, cy);

                    if (!_grid.ContainsKey(key))
                        _grid[key] = new List<SpatialEntry<T>>();

                    _grid[key].Add(new SpatialEntry<T>
                    {
                        Item = item,
                        Position = center,
                        Bounds = bounds
                    });
                }
            }

            Count++;
        }

        /// <summary>
        /// Query all items within radius of a point
        /// </summary>
        public List<T> QueryRadius(Point2D center, double radius)
        {
            var result = new List<T>();
            var visited = new HashSet<T>();

            int cellRadius = (int)Math.Ceiling(radius / _cellSize);
            int centerCellX = (int)Math.Floor(center.X / _cellSize);
            int centerCellY = (int)Math.Floor(center.Y / _cellSize);

            for (int dx = -cellRadius; dx <= cellRadius; dx++)
            {
                for (int dy = -cellRadius; dy <= cellRadius; dy++)
                {
                    long key = GetCellKey(centerCellX + dx, centerCellY + dy);

                    if (_grid.TryGetValue(key, out var entries))
                    {
                        foreach (var entry in entries)
                        {
                            if (visited.Contains(entry.Item))
                                continue;

                            double dist = center.DistanceTo(entry.Position);
                            if (dist <= radius)
                            {
                                result.Add(entry.Item);
                                visited.Add(entry.Item);
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Query all items whose bounds intersect with given bounds
        /// </summary>
        public List<T> QueryBounds(GeoAlgo.BoundingBox queryBounds)
        {
            var result = new List<T>();
            var visited = new HashSet<T>();

            int minCellX = (int)Math.Floor(queryBounds.MinX / _cellSize);
            int maxCellX = (int)Math.Floor(queryBounds.MaxX / _cellSize);
            int minCellY = (int)Math.Floor(queryBounds.MinY / _cellSize);
            int maxCellY = (int)Math.Floor(queryBounds.MaxY / _cellSize);

            for (int cx = minCellX; cx <= maxCellX; cx++)
            {
                for (int cy = minCellY; cy <= maxCellY; cy++)
                {
                    long key = GetCellKey(cx, cy);

                    if (_grid.TryGetValue(key, out var entries))
                    {
                        foreach (var entry in entries)
                        {
                            if (visited.Contains(entry.Item))
                                continue;

                            if (entry.Bounds.Intersects(queryBounds))
                            {
                                result.Add(entry.Item);
                                visited.Add(entry.Item);
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Clear all entries
        /// </summary>
        public void Clear()
        {
            _grid.Clear();
            Count = 0;
        }

        #region Private Helpers

        private long GetCellKey(Point2D pos)
        {
            int cellX = (int)Math.Floor(pos.X / _cellSize);
            int cellY = (int)Math.Floor(pos.Y / _cellSize);
            return GetCellKey(cellX, cellY);
        }

        private long GetCellKey(int cellX, int cellY)
        {
            // Combine two 32-bit ints into one 64-bit long
            return ((long)cellX << 32) | (uint)cellY;
        }

        #endregion
    }

    /// <summary>
    /// Entry in the spatial hash
    /// </summary>
    public struct SpatialEntry<T>
    {
        public T Item;
        public Point2D Position;
        public GeoAlgo.BoundingBox Bounds;
    }
}