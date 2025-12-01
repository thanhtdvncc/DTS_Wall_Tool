using Autodesk.AutoCAD.DatabaseServices;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Engines
{
    /// <summary>
    /// Kết quả đồng bộ cho một phần tử
    /// </summary>
    public class SyncResult
    {
        public string Handle { get; set; }
        public SyncState State { get; set; }
        public string Message { get; set; }
        public bool Success { get; set; }

        // Thông tin thay đổi
        public string OldFrameName { get; set; }
        public string NewFrameName { get; set; }
        public double? OldLoadValue { get; set; }
        public double? NewLoadValue { get; set; }
    }

    /// <summary>
    /// Engine đồng bộ 2 chiều giữa AutoCAD và SAP2000
    /// 
    /// Quy trình đồng bộ:
    /// 1.  PULL: Đọc thay đổi từ SAP → Cập nhật CAD
    /// 2. PUSH: Ghi thay đổi từ CAD → SAP
    /// 3.  DETECT: Phát hiện xung đột và thay đổi
    /// </summary>
    public static class SyncEngine
    {
        #region Configuration

        /// <summary>Tự động tạo frame mới trong SAP khi mapping = NEW</summary>
        public static bool AutoCreateSapFrame = false;

        /// <summary>Cho phép ghi đè tải trọng trong SAP</summary>
        public static bool AllowOverwriteSapLoad = true;

        /// <summary>Load pattern mặc định</summary>
        public static string DefaultLoadPattern = "DL";

        #endregion

        #region PULL: SAP → CAD

        /// <summary>
        /// Đồng bộ thay đổi từ SAP2000 vào CAD (PULL)
        /// - Phát hiện frame bị xóa/chia/merge
        /// - Cập nhật tải trọng từ SAP
        /// </summary>
        public static List<SyncResult> PullFromSap(List<ObjectId> elementIds, Transaction tr)
        {
            var results = new List<SyncResult>();

            if (!SapUtils.IsConnected)
            {
                SapUtils.Connect(out _);
                if (!SapUtils.IsConnected)
                {
                    results.Add(new SyncResult
                    {
                        Success = false,
                        Message = "Không thể kết nối SAP2000"
                    });
                    return results;
                }
            }

            foreach (ObjectId elemId in elementIds)
            {
                var result = PullSingleElement(elemId, tr);
                if (result != null)
                {
                    results.Add(result);
                }
            }

            return results;
        }

        /// <summary>
        /// Đồng bộ một phần tử từ SAP
        /// </summary>
        private static SyncResult PullSingleElement(ObjectId elemId, Transaction tr)
        {
            var result = new SyncResult
            {
                Handle = elemId.Handle.ToString(),
                Success = true
            };

            try
            {
                Entity ent = tr.GetObject(elemId, OpenMode.ForWrite) as Entity;
                if (ent == null) return null;

                // Đọc ElementData
                var elemData = XDataUtils.ReadElementData(ent);
                if (elemData == null || !elemData.HasMapping)
                {
                    result.State = SyncState.NotSynced;
                    result.Message = "Chưa có mapping";
                    return result;
                }

                // Kiểm tra từng mapping
                foreach (var mapping in elemData.Mappings.ToList())
                {
                    if (mapping.TargetFrame == "New") continue;

                    // Kiểm tra frame còn tồn tại không
                    if (!SapUtils.FrameExists(mapping.TargetFrame))
                    {
                        result.State = SyncState.SapDeleted;
                        result.OldFrameName = mapping.TargetFrame;

                        // Thử tìm frame thay thế
                        if (ent is Line line)
                        {
                            var startPt = new Point2D(line.StartPoint.X, line.StartPoint.Y);
                            var endPt = new Point2D(line.EndPoint.X, line.EndPoint.Y);
                            double elevation = elemData.BaseZ ?? 0;

                            string newFrame = SapUtils.FindReplacementFrame(startPt, endPt, elevation);
                            if (!string.IsNullOrEmpty(newFrame))
                            {
                                mapping.TargetFrame = newFrame;
                                result.NewFrameName = newFrame;
                                result.State = SyncState.SapModified;
                                result.Message = $"Frame đã đổi: {result.OldFrameName} → {newFrame}";
                            }
                            else
                            {
                                mapping.TargetFrame = "New";
                                result.Message = $"Frame {result.OldFrameName} đã bị xóa";
                            }
                        }
                    }
                    else
                    {
                        // Frame vẫn tồn tại - kiểm tra tải trọng
                        var sapLoads = SapUtils.GetFrameDistributedLoads(mapping.TargetFrame, DefaultLoadPattern);

                        if (sapLoads.Count > 0)
                        {
                            var sapLoad = sapLoads.First();
                            result.NewLoadValue = sapLoad.LoadValue;

                            // Cập nhật cache
                            if (elemData is WallData wallData)
                            {
                                result.OldLoadValue = wallData.LoadValue;

                                // Kiểm tra xung đột
                                if (wallData.LoadValue.HasValue &&
                                    Math.Abs(wallData.LoadValue.Value - sapLoad.LoadValue) > 0.01)
                                {
                                    // SAP có tải khác với CAD
                                    result.State = SyncState.SapModified;
                                    result.Message = $"Tải SAP: {sapLoad.LoadValue:0.00} kN/m (CAD: {wallData.LoadValue:0.00})";
                                }
                                else
                                {
                                    result.State = SyncState.Synced;
                                }
                            }
                        }
                    }
                }

                // Lưu thay đổi
                XDataUtils.WriteElementData(ent, elemData, tr);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }

            return result;
        }

        #endregion

        #region PUSH: CAD → SAP

        /// <summary>
        /// Đồng bộ thay đổi từ CAD vào SAP2000 (PUSH)
        /// - Gán tải trọng đã tính từ CAD
        /// - Tạo frame mới nếu cần
        /// </summary>
        public static List<SyncResult> PushToSap(List<ObjectId> elementIds, string loadPattern, Transaction tr)
        {
            var results = new List<SyncResult>();

            if (!SapUtils.IsConnected)
            {
                SapUtils.Connect(out _);
                if (!SapUtils.IsConnected)
                {
                    results.Add(new SyncResult
                    {
                        Success = false,
                        Message = "Không thể kết nối SAP2000"
                    });
                    return results;
                }
            }

            // Kiểm tra load pattern
            if (!SapUtils.LoadPatternExists(loadPattern))
            {
                results.Add(new SyncResult
                {
                    Success = false,
                    Message = $"Load pattern '{loadPattern}' không tồn tại trong SAP"
                });
                return results;
            }

            foreach (ObjectId elemId in elementIds)
            {
                var result = PushSingleElement(elemId, loadPattern, tr);
                if (result != null)
                {
                    results.Add(result);
                }
            }

            SapUtils.RefreshView();
            return results;
        }

        /// <summary>
        /// Gán tải một phần tử vào SAP
        /// </summary>
        private static SyncResult PushSingleElement(ObjectId elemId, string loadPattern, Transaction tr)
        {
            var result = new SyncResult
            {
                Handle = elemId.Handle.ToString()
            };

            try
            {
                DBObject obj = tr.GetObject(elemId, OpenMode.ForRead);

                var elemData = XDataUtils.ReadElementData(obj);
                if (elemData == null)
                {
                    result.Success = false;
                    result.Message = "Không có dữ liệu DTS";
                    return result;
                }

                // Chỉ xử lý WallData có tải
                if (!(elemData is WallData wallData) || !wallData.LoadValue.HasValue)
                {
                    result.Success = false;
                    result.Message = "Chưa tính tải trọng";
                    return result;
                }

                if (!wallData.HasMapping)
                {
                    result.Success = false;
                    result.State = SyncState.NewElement;
                    result.Message = "Chưa mapping với SAP";
                    return result;
                }

                // Gán tải cho từng mapping
                int successCount = 0;
                foreach (var mapping in wallData.Mappings)
                {
                    if (mapping.TargetFrame == "New")
                    {
                        // TODO: Tạo frame mới nếu AutoCreateSapFrame = true
                        continue;
                    }

                    bool assigned = SapUtils.ReplaceFrameLoad(
                        mapping.TargetFrame,
                        loadPattern,
                        wallData.LoadValue.Value,
                        mapping.DistI,
                        mapping.DistJ
                    );

                    if (assigned)
                    {
                        successCount++;
                    }
                }

                result.Success = successCount > 0;
                result.State = result.Success ? SyncState.Synced : SyncState.CadModified;
                result.Message = $"Gán {successCount}/{wallData.Mappings.Count} frame";
                result.NewLoadValue = wallData.LoadValue;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }

            return result;
        }

        #endregion

        #region DETECT: Phát hiện thay đổi

        /// <summary>
        /// Phát hiện trạng thái đồng bộ của phần tử
        /// </summary>
        public static SyncState DetectSyncState(ObjectId elemId, Transaction tr)
        {
            try
            {
                DBObject obj = tr.GetObject(elemId, OpenMode.ForRead);
                var elemData = XDataUtils.ReadElementData(obj);

                if (elemData == null)
                    return SyncState.NotSynced;

                if (!elemData.HasMapping)
                    return SyncState.NewElement;

                // Kiểm tra từng mapping
                foreach (var mapping in elemData.Mappings)
                {
                    if (mapping.TargetFrame == "New")
                        continue;

                    // Frame có tồn tại không? 
                    if (!SapUtils.FrameExists(mapping.TargetFrame))
                        return SyncState.SapDeleted;

                    // So sánh tải trọng
                    if (elemData is WallData wallData && wallData.LoadValue.HasValue)
                    {
                        var sapLoads = SapUtils.GetFrameDistributedLoads(mapping.TargetFrame, DefaultLoadPattern);

                        if (sapLoads.Count == 0)
                        {
                            // SAP chưa có tải, CAD đã có
                            return SyncState.CadModified;
                        }

                        double sapLoad = sapLoads.Sum(l => l.LoadValue);
                        if (Math.Abs(wallData.LoadValue.Value - sapLoad) > 0.01)
                        {
                            // Tải khác nhau - cần xác định ai thay đổi
                            // TODO: So sánh timestamp
                            return SyncState.Conflict;
                        }
                    }
                }

                return SyncState.Synced;
            }
            catch
            {
                return SyncState.NotSynced;
            }
        }

        /// <summary>
        /// Quét và phát hiện thay đổi cho danh sách phần tử
        /// </summary>
        public static Dictionary<ObjectId, SyncState> DetectAllChanges(List<ObjectId> elementIds, Transaction tr)
        {
            var result = new Dictionary<ObjectId, SyncState>();

            foreach (var elemId in elementIds)
            {
                result[elemId] = DetectSyncState(elemId, tr);
            }

            return result;
        }

        #endregion

        #region Utility

        /// <summary>
        /// Màu hiển thị theo trạng thái đồng bộ
        /// </summary>
        public static int GetSyncStateColor(SyncState state)
        {
            switch (state)
            {
                case SyncState.Synced: return 3;      // Xanh lá
                case SyncState.CadModified: return 2; // Vàng
                case SyncState.SapModified: return 5; // Xanh dương
                case SyncState.Conflict: return 6;    // Magenta
                case SyncState.SapDeleted: return 1;  // Đỏ
                case SyncState.NewElement: return 4;  // Cyan
                default: return 7;                     // Trắng
            }
        }

        #endregion
    }
}