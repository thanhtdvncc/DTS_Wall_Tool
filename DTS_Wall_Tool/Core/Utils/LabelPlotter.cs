using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DTS_Wall_Tool.Core.Primitives;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
    /// Vị trí chèn Label trên Frame
    /// </summary>
    public enum LabelPosition
    {
        /// <summary>Đầu frame, phía trên</summary>
        StartTop = 0,
        /// <summary>Đầu frame, phía dưới</summary>
        StartBottom = 1,
        /// <summary>Giữa frame, phía trên</summary>
        MiddleTop = 2,
        /// <summary>Giữa frame, phía dưới</summary>
        MiddleBottom = 3,
        /// <summary>Cuối frame, phía trên</summary>
        EndTop = 4,
        /// <summary>Cuối frame, phía dưới</summary>
        EndBottom = 5
    }

    /// <summary>
    /// Module vẽ MText Label thông minh cho Frame và Area
    /// Hỗ trợ 6 vị trí: Đầu/Giữa/Cuối × Trên/Dưới
    /// Text căn chỉnh tự động để không lẹm sang frame khác
    /// </summary>
    public static class LabelPlotter
    {
        #region Constants

        /// <summary>Khoảng cách từ text đến frame (mm)</summary>
        public const double TEXT_GAP = 50.0;

        /// <summary>Chiều cao text mặc định (mm)</summary>
        public const double DEFAULT_TEXT_HEIGHT = 80.0;

        /// <summary>Layer mặc định cho label</summary>
        public const string DEFAULT_LAYER = "dts_frame_label";

        /// <summary>Màu mặc định (254 = gray)</summary>
        public const int DEFAULT_COLOR = 254;

        #endregion

        #region Main API

        /// <summary>
        /// Vẽ MText Label trên Frame với vị trí và căn chỉnh thông minh
        /// </summary>
        /// <param name="btr">BlockTableRecord để thêm entity</param>
        /// <param name="tr">Transaction hiện tại</param>
        /// <param name="startPt">Điểm đầu của frame (CAD coordinates)</param>
        /// <param name="endPt">Điểm cuối của frame (CAD coordinates)</param>
        /// <param name="content">Nội dung MText (có thể chứa format codes như {\C1;text})</param>
        /// <param name="position">Vị trí chèn (6 vị trí)</param>
        /// <param name="textHeight">Chiều cao text (mm)</param>
        /// <param name="layer">Tên layer</param>
        /// <returns>ObjectId của MText đã tạo</returns>
        public static ObjectId PlotLabel(
            BlockTableRecord btr,
            Transaction tr,
            Point2D startPt,
            Point2D endPt,
            string content,
            LabelPosition position,
            double textHeight = DEFAULT_TEXT_HEIGHT,
            string layer = DEFAULT_LAYER)
        {
            if (btr == null || tr == null) return ObjectId.Null;
            if (string.IsNullOrWhiteSpace(content)) return ObjectId.Null;

            // Tính toán geometry
            var geo = CalculateLabelGeometry(startPt, endPt, position, textHeight);

            // Tạo MText
            MText mtext = new MText();
            mtext.Contents = content;
            mtext.Location = new Point3d(geo.InsertPoint.X, geo.InsertPoint.Y, 0);
            mtext.TextHeight = textHeight;
            mtext.Rotation = geo.Rotation;
            mtext.Attachment = geo.Attachment;
            mtext.Layer = layer;
            mtext.ColorIndex = DEFAULT_COLOR;

            // Thêm vào drawing
            ObjectId id = btr.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);

            return id;
        }

        /// <summary>
        /// Vẽ MText Label với Point3d input
        /// </summary>
        public static ObjectId PlotLabel(
            BlockTableRecord btr,
            Transaction tr,
            Point3d startPt,
            Point3d endPt,
            string content,
            LabelPosition position,
            double textHeight = DEFAULT_TEXT_HEIGHT,
            string layer = DEFAULT_LAYER)
        {
            return PlotLabel(btr, tr,
                new Point2D(startPt.X, startPt.Y),
                new Point2D(endPt.X, endPt.Y),
                content, position, textHeight, layer);
        }

        /// <summary>
        /// Cập nhật nội dung MText hiện có
        /// </summary>
        public static void UpdateLabel(
            MText mtext,
            Point2D startPt,
            Point2D endPt,
            string content,
            LabelPosition position,
            double textHeight = DEFAULT_TEXT_HEIGHT)
        {
            if (mtext == null) return;

            var geo = CalculateLabelGeometry(startPt, endPt, position, textHeight);

            mtext.Contents = content;
            mtext.Location = new Point3d(geo.InsertPoint.X, geo.InsertPoint.Y, 0);
            mtext.TextHeight = textHeight;
            mtext.Rotation = geo.Rotation;
            mtext.Attachment = geo.Attachment;
        }

        #endregion

        #region Geometry Calculation

        /// <summary>
        /// Kết quả tính toán geometry cho label
        /// </summary>
        private struct LabelGeometry
        {
            public Point2D InsertPoint;
            public double Rotation;
            public AttachmentPoint Attachment;
        }

        /// <summary>
        /// Tính toán vị trí, góc xoay và attachment point cho label
        /// </summary>
        private static LabelGeometry CalculateLabelGeometry(
                    Point2D startPt, Point2D endPt,
                    LabelPosition position, double textHeight)
        {
            var result = new LabelGeometry();

            // 1. Vector hướng của đoạn tường
            double dx = endPt.X - startPt.X;
            double dy = endPt.Y - startPt.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);

            if (length < GeometryConstants.EPSILON)
            {
                result.InsertPoint = startPt;
                return result;
            }

            // Vector đơn vị
            double ux = dx / length;
            double uy = dy / length;

            // 2. Tính góc cơ sở (0 đến 2PI)
            double angle = Math.Atan2(dy, dx);
            if (angle < 0) angle += 2 * Math.PI;

            // 3. Logic Readability (Text luôn đọc được)
            // AutoCAD tự lật text nếu góc > 90 và <= 270.
            // Ta cần mô phỏng logic đó để tính toán điểm chèn.
            bool isFlipped = (angle > Math.PI / 2 && angle <= 3 * Math.PI / 2);
            double readableAngle = isFlipped ? angle + Math.PI : angle;

            // 4. Vector vuông góc ("Hướng lên" so với text)
            // Nếu text bị lật, vector "lên" cũng bị lật ngược lại so với hệ tọa độ
            double perpX = -uy;
            double perpY = ux;

            // 5. Xác định điểm neo cơ sở (Base Point trên đường line)
            Point2D basePoint;
            switch (position)
            {
                case LabelPosition.StartTop:
                case LabelPosition.StartBottom: basePoint = startPt; break;
                case LabelPosition.EndTop:
                case LabelPosition.EndBottom: basePoint = endPt; break;
                default: basePoint = new Point2D((startPt.X + endPt.X) / 2, (startPt.Y + endPt.Y) / 2); break;
            }

            // 6. Tính toán Offset và Attachment Point
            // Quy ước: "Top" là phía trên dòng chữ, "Bottom" là phía dưới dòng chữ.

            bool isTopPos = (position == LabelPosition.StartTop || position == LabelPosition.MiddleTop || position == LabelPosition.EndTop);

            // Khoảng cách dịch chuyển từ tim tường ra
            double offsetDist = (textHeight / 2.0) + TEXT_GAP;

            // Logic quan trọng:
            // Nếu vị trí là Top -> Text nằm trên Line -> Attachment phải là BottomCenter -> Dịch chuyển +Perp
            // Nếu vị trí là Bot -> Text nằm dưới Line -> Attachment phải là TopCenter -> Dịch chuyển -Perp

            // Tuy nhiên, nếu Line bị Flip (vẽ ngược chiều), thì +Perp lại trở thành hướng xuống.
            // Do đó cần kết hợp isTopPos và isFlipped.

            // Hướng dịch chuyển thực tế so với vector pháp tuyến (perp)
            // Nếu chưa flip: Top -> +Perp, Bot -> -Perp
            // Nếu đã flip: Top -> -Perp, Bot -> +Perp (Vì hệ trục text đã xoay 180)
            double directionMultiplier = isTopPos ? 1.0 : -1.0;
            if (isFlipped) directionMultiplier *= -1.0;

            result.InsertPoint = new Point2D(
                basePoint.X + perpX * offsetDist * directionMultiplier,
                basePoint.Y + perpY * offsetDist * directionMultiplier
            );

            result.Rotation = readableAngle;

            // 7. Xác định Attachment Point
            // Luôn neo vào cạnh gần đường Line nhất để Text "mọc" ra xa đường Line
            if (isTopPos)
            {
                // Vị trí trên -> Neo đáy text (Bottom)
                result.Attachment = AttachmentPoint.BottomCenter;
            }
            else
            {
                // Vị trí dưới -> Neo đỉnh text (Top)
                result.Attachment = AttachmentPoint.TopCenter;
            }

            return result;
        }

        /// <summary>
        /// Xác định AttachmentPoint dựa trên vị trí
        /// </summary>
        private static AttachmentPoint GetAttachmentPoint(LabelPosition position, bool isFlipped)
        {
            // Logic:
            // - Đầu (Start): căn LEFT để text mở rộng về phía giữa
            // - Giữa (Middle): căn CENTER
            // - Cuối (End): căn RIGHT để text mở rộng về phía giữa
            // - Trên: căn BOTTOM (chân text nằm gần frame)
            // - Dưới: căn TOP (đỉnh text nằm gần frame)

            bool isTop = (position == LabelPosition.StartTop ||
                          position == LabelPosition.MiddleTop ||
                          position == LabelPosition.EndTop);

            // Nếu đã flip, đảo logic top/bottom
            if (isFlipped) isTop = !isTop;

            switch (position)
            {
                case LabelPosition.StartTop:
                case LabelPosition.StartBottom:
                    return isTop ? AttachmentPoint.BottomLeft : AttachmentPoint.TopLeft;

                case LabelPosition.MiddleTop:
                case LabelPosition.MiddleBottom:
                    return isTop ? AttachmentPoint.BottomCenter : AttachmentPoint.TopCenter;

                case LabelPosition.EndTop:
                case LabelPosition.EndBottom:
                    return isTop ? AttachmentPoint.BottomRight : AttachmentPoint.TopRight;

                default:
                    return AttachmentPoint.MiddleCenter;
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Tạo chuỗi MText với màu chỉ định
        /// Ví dụ: FormatWithColor("Hello", 1) => "{\C1;Hello}"
        /// </summary>
        public static string FormatWithColor(string text, int colorIndex)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return $"{{\\C{colorIndex};{text}}}";
        }

        /// <summary>
        /// Tạo chuỗi MText xuống dòng
        /// </summary>
        public static string CombineLines(params string[] lines)
        {
            return string.Join("\\P", lines);
        }

        /// <summary>
        /// Format nội dung mapping cho hiển thị
        /// </summary>
        public static string FormatMappingLabel(string frameName, string matchType, int colorIndex)
        {
            string coloredName = FormatWithColor(frameName, colorIndex);

            if (matchType == "FULL")
                return $"to {coloredName} (full)";
            else if (matchType == "NEW")
                return FormatWithColor("NEW", 1); // Đỏ
            else
                return $"to {coloredName}";
        }

        #endregion
    }
}