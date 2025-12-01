using DTS_Wall_Tool.Core.Primitives;
using System;
using System.Collections.Generic;

namespace DTS_Wall_Tool.DataStructures
{
    /// <summary>
    /// Spatial hash grid cho truy vấn không gian nhanh O(1)
    /// </summary>
    public class SpatialHash<T>
    {
        private readonly double _cellSize;
        private readonly Dictionary<long, List<SpatialEntry<T>>> _grid;

        public int Count { get; private set; }

        public SpatialHash(double cellSize = 1000)
        {
            _cellSize = cellSize > 0 ? cellSize : 1000;
            _grid = new Dictionary<long, List<SpatialEntry<T>>>();
            Count = 0;
        }

        /// <summary>
        /// Thêm item tại vị trí
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
        /// Thêm item với bounding box
        /// </summary>
        public void InsertWithBounds(T item, BoundingBox bounds)
        {
            int minCellX = (int)Math.Floor(bounds.MinX / _cellSize);
            int maxCellX = (int)Math.Floor(bounds.MaxX / _cellSize);
            int minCellY = (int)Math.Floor(bounds.MinY / _cellSize);
            int maxCellY = (int)Math.Floor(bounds.MaxY / _cellSize);

            var center = bounds.Center;

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
        /// Truy vấn tất cả items trong bán kính
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

                            if (center.DistanceTo(entry.Position) <= radius)
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
        /// Truy vấn tất cả items có bounds giao với bounds cho trước
        /// </summary>
        public List<T> QueryBounds(BoundingBox queryBounds)
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
        /// Xóa tất cả
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
            return ((long)cellX << 32) | (uint)cellY;
        }

        #endregion
    }

    /// <summary>
    /// Entry trong spatial hash
    /// </summary>
    public struct SpatialEntry<T>
    {
        public T Item;
        public Point2D Position;
        public BoundingBox Bounds;
    }
}