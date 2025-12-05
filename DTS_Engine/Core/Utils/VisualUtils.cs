using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using System;
using System.Collections.Generic;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Quan ly hien thi tam thoi (Transient Graphics) de tranh lam "ban" ban ve.
    /// Su dung TransientManager de ve overlay mau ao len doi tuong.
    /// Overlay se bien mat khi goi ClearAll() hoac Regen, tra lai nguyen trang ban ve.
    /// 
    /// Tuan thu ISO/IEC 25010: Maintainability, Non-destructive visualization.
    /// </summary>
    public static class VisualUtils
    {
        private static readonly List<DBObject> _transients = new List<DBObject>();
        private static readonly object _syncLock = new object();

        // Tracking cancel count cho auto-clear
        private static int _cancelCount = 0;
        private static DateTime _lastCancelTime = DateTime.MinValue;
        private const int CANCEL_THRESHOLD = 5;
        private const double CANCEL_TIMEOUT_SECONDS = 3.0;

        #region Cancel Tracking for Auto-Clear

        /// <summary>
        /// Goi khi user cancel (Esc) mot lenh.
        /// Neu cancel 5 lan trong 3 giay va co transient, tu dong clear.
        /// </summary>
        /// <returns>True neu da tu dong clear, False neu chua</returns>
        public static bool TrackCancelAndAutoClear()
        {
            var now = DateTime.Now;

            // Reset counter neu qua timeout
            if ((now - _lastCancelTime).TotalSeconds > CANCEL_TIMEOUT_SECONDS)
            {
                _cancelCount = 0;
            }

            _cancelCount++;
            _lastCancelTime = now;

            // Neu dat threshold va co transient, tu dong clear
            if (_cancelCount >= CANCEL_THRESHOLD && TransientCount > 0)
            {
                ClearAll();
                _cancelCount = 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reset cancel counter (goi khi bat dau lenh moi).
        /// </summary>
        public static void ResetCancelTracking()
        {
            _cancelCount = 0;
        }

        #endregion

        #region Highlight Objects

        /// <summary>
        /// Highlight đối tượng bằng màu tạm thời (không đổi màu gốc của Entity).
        /// Clone entity và vẽ overlay với màu chỉ định.
        /// </summary>
        /// <param name="id">ObjectId của entity cần highlight</param>
        /// <param name="colorIndex">Mã màu AutoCAD (1=Red, 2=Yellow, 3=Green, 4=Cyan, 5=Blue, 6=Magenta, 7=White)</param>
        public static void HighlightObject(ObjectId id, int colorIndex)
        {
            if (id == ObjectId.Null || id.IsErased) return;

            try
            {
                Entity entClone = null;

                AcadUtils.UsingTransaction(tr =>
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent != null && !ent.IsErased)
                    {
                        entClone = ent.Clone() as Entity;
                        if (entClone != null)
                        {
                            entClone.ColorIndex = colorIndex;
                        }
                    }
                });

                if (entClone != null)
                {
                    AddTransientInternal(entClone, TransientDrawingMode.Highlight);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VisualUtils] HighlightObject error: {ex.Message}");
            }
        }

        /// <summary>
        /// Highlight danh sách đối tượng với cùng một màu.
        /// </summary>
        public static void HighlightObjects(List<ObjectId> ids, int colorIndex)
        {
            if (ids == null || ids.Count == 0) return;

            foreach (var id in ids)
            {
                HighlightObject(id, colorIndex);
            }
        }

        /// <summary>
        /// Highlight đối tượng với màu dựa trên trạng thái đồng bộ.
        /// </summary>
        public static void HighlightBySyncState(ObjectId id, SyncState state)
        {
            int color = GetColorForSyncState(state);
            HighlightObject(id, color);
        }

        #endregion

        #region Draw Transient Geometry

        /// <summary>
        /// Vẽ đường Line tạm thời (không tạo entity thật trong bản vẽ).
        /// </summary>
        public static void DrawTransientLine(Point3d start, Point3d end, int colorIndex)
        {
            try
            {
                var line = new Line(start, end);
                line.ColorIndex = colorIndex;
                AddTransientInternal(line, TransientDrawingMode.Main);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VisualUtils] DrawTransientLine error: {ex.Message}");
            }
        }

        /// <summary>
        /// Vẽ đường Line tạm thời từ Point2D (Z=0).
        /// </summary>
        public static void DrawTransientLine(Point2D start, Point2D end, int colorIndex)
        {
            DrawTransientLine(
                new Point3d(start.X, start.Y, 0),
                new Point3d(end.X, end.Y, 0),
                colorIndex
            );
        }

        /// <summary>
        /// Vẽ vòng tròn tạm thời.
        /// </summary>
        public static void DrawTransientCircle(Point3d center, double radius, int colorIndex)
        {
            try
            {
                var circle = new Circle(center, Vector3d.ZAxis, radius);
                circle.ColorIndex = colorIndex;
                AddTransientInternal(circle, TransientDrawingMode.Main);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VisualUtils] DrawTransientCircle error: {ex.Message}");
            }
        }

        /// <summary>
        /// Thêm đối tượng tùy ý vào danh sách transient để hiển thị.
        /// </summary>
        /// <param name="obj">DBObject để hiển thị (phải là Entity)</param>
        /// <param name="colorIndex">Mã màu (256 = ByLayer)</param>
        public static void AddTransient(DBObject obj, int colorIndex = 256)
        {
            if (obj == null) return;

            try
            {
                if (obj is Entity ent)
                {
                    if (colorIndex != 256)
                    {
                        ent.ColorIndex = colorIndex;
                    }
                    AddTransientInternal(ent, TransientDrawingMode.Main);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VisualUtils] AddTransient error: {ex.Message}");
            }
        }

        #endregion

        #region Draw Link Lines

        /// <summary>
        /// Vẽ các đường link tạm thời từ Parent đến danh sách Children.
        /// Thay thế cho việc tạo Line thật trên layer dts_linkmap.
        /// </summary>
        public static void DrawLinkLines(ObjectId parentId, List<ObjectId> childIds, int colorIndex = 2)
        {
            if (parentId == ObjectId.Null || childIds == null || childIds.Count == 0) return;

            try
            {
                Point3d parentCenter = Point3d.Origin;

                AcadUtils.UsingTransaction(tr =>
                {
                    var parentEnt = tr.GetObject(parentId, OpenMode.ForRead) as Entity;
                    if (parentEnt != null)
                    {
                        parentCenter = AcadUtils.GetEntityCenter3d(parentEnt);
                    }
                });

                if (parentCenter == Point3d.Origin) return;

                foreach (var childId in childIds)
                {
                    if (childId == ObjectId.Null || childId.IsErased) continue;

                    Point3d childCenter = Point3d.Origin;

                    AcadUtils.UsingTransaction(tr =>
                    {
                        var childEnt = tr.GetObject(childId, OpenMode.ForRead) as Entity;
                        if (childEnt != null)
                        {
                            childCenter = AcadUtils.GetEntityCenter3d(childEnt);
                        }
                    });

                    if (childCenter != Point3d.Origin)
                    {
                        DrawTransientLine(parentCenter, childCenter, colorIndex);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VisualUtils] DrawLinkLines error: {ex.Message}");
            }
        }

        #endregion

        #region Scan Link Visualization

        /// <summary>
        /// Vẽ các đường scan link tạm thời từ điểm gốc đến các item.
        /// Thay thế cho DrawScanLinks trong ScanCommands.
        /// </summary>
        public static int DrawScanLinks(Point3d originPt, List<ScanLinkItem> items)
        {
            if (items == null || items.Count == 0) return 0;

            int count = 0;

            foreach (var item in items)
            {
                try
                {
                    var line = new Line(originPt, item.Center);
                    line.ColorIndex = item.ColorIndex;
                    AddTransientInternal(line, TransientDrawingMode.Main);
                    count++;
                }
                catch
                {
                    // Skip failed items
                }
            }

            return count;
        }

        #endregion

        #region Clear & Cleanup

        /// <summary>
        /// Xóa toàn bộ các hiển thị tạm thời (trả lại nguyên trạng màn hình).
        /// </summary>
        public static void ClearAll()
        {
            lock (_syncLock)
            {
                try
                {
                    var tm = TransientManager.CurrentTransientManager;

                    foreach (var obj in _transients)
                    {
                        if (obj != null)
                        {
                            try
                            {
                                tm.EraseTransient(obj, new IntegerCollection());
                            }
                            catch
                            {
                                // Ignore erase errors
                            }

                            try
                            {
                                obj.Dispose();
                            }
                            catch
                            {
                                // Ignore dispose errors
                            }
                        }
                    }

                    _transients.Clear();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VisualUtils] ClearAll error: {ex.Message}");
                    _transients.Clear();
                }
            }
        }

        /// <summary>
        /// Lấy số lượng transient đang được hiển thị.
        /// </summary>
        public static int TransientCount
        {
            get
            {
                lock (_syncLock)
                {
                    return _transients.Count;
                }
            }
        }

        #endregion

        #region Internal Helpers

        private static void AddTransientInternal(Entity entity, TransientDrawingMode mode)
        {
            if (entity == null) return;

            lock (_syncLock)
            {
                try
                {
                    var tm = TransientManager.CurrentTransientManager;
                    tm.AddTransient(entity, mode, 128, new IntegerCollection());
                    _transients.Add(entity);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VisualUtils] AddTransientInternal error: {ex.Message}");
                    entity.Dispose();
                }
            }
        }

        private static int GetColorForSyncState(SyncState state)
        {
            switch (state)
            {
                case SyncState.Synced: return 3;        // Green
                case SyncState.CadModified: return 4;   // Cyan
                case SyncState.SapModified: return 5;   // Blue
                case SyncState.Conflict: return 6;      // Magenta
                case SyncState.SapDeleted: return 1;    // Red
                case SyncState.NewElement: return 2;    // Yellow
                default: return 7;                      // White
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Item dùng cho scan link visualization.
    /// </summary>
    public class ScanLinkItem
    {
        public ObjectId ObjId { get; set; }
        public Point3d Center { get; set; }
        public int ColorIndex { get; set; }
        public string Type { get; set; }
    }

    #endregion
}
