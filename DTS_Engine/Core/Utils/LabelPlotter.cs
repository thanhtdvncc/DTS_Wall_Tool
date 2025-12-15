using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DTS_Engine.Core.Primitives;
using System;

namespace DTS_Engine.Core.Utils
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
    /// Hỗ trợ6 vị trí: Đầu/Giữa/Cuối × Trên/Dưới
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
        /// Vẽ MText Label hỗ trợ3D đầy đủ (Vertical/Inclined)
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
            if (btr == null || tr == null || string.IsNullOrWhiteSpace(content)) return ObjectId.Null;

            // Tính toán Vector hướng3D
            Vector3d delta = endPt - startPt;
            double length3d = delta.Length;
            double length2d = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            double deltaZ = Math.Abs(delta.Z);

            if (length3d < GeometryConstants.EPSILON) return ObjectId.Null; // Bỏ qua nếu length=0

            Point3d insertPoint;
            double rotation = 0;
            AttachmentPoint attachment = AttachmentPoint.MiddleCenter;

            // Vectors for text orientation
            Vector3d textNormal = new Vector3d(0, 0, 1);
            Vector3d textDir = new Vector3d(1, 0, 0);

            // CASE1: Horizontal element (flat) -> place text on XY plane
            if (deltaZ < 10.0)
            {
                textNormal = new Vector3d(0, 0, 1);

                Point2D s2 = new Point2D(startPt.X, startPt.Y);
                Point2D e2 = new Point2D(endPt.X, endPt.Y);

                var geo = CalculateLabelGeometry2D(s2, e2, position, textHeight);

                // Keep average Z
                double z = (startPt.Z + endPt.Z) / 2.0;
                insertPoint = new Point3d(geo.InsertPoint.X, geo.InsertPoint.Y, z);
                rotation = geo.Rotation;
                attachment = geo.Attachment;

                // Direction from rotation on XY
                textDir = new Vector3d(Math.Cos(rotation), Math.Sin(rotation), 0);
            }
            // CASE2: Vertical (column)
            else if (length2d < 10.0)
            {
                // Derive orientation from axis so text baseline aligns with column axis without extra rotation
                Vector3d axisDir = delta.GetNormal();
                Vector3d upVec = new Vector3d(0, 0, 1);

                // Compute a horizontal normal perpendicular to the column axis projection on XY
                var proj = new Vector3d(axisDir.X, axisDir.Y, 0);
                if (proj.Length < 1e-6)
                {
                    textNormal = new Vector3d(1, 0, 0);
                }
                else
                {
                    textNormal = new Vector3d(-proj.Y, proj.X, 0).GetNormal();
                }

                // Text up direction follows the column axis
                Vector3d textUp = axisDir.GetNormal();

                // Baseline (Direction) must satisfy: textUp = textNormal.Cross(textDir)
                // => textDir = textUp.Cross(textNormal)
                textDir = textUp.CrossProduct(textNormal).GetNormal();

                // FORCE rotate baseline by90 degrees in-plane (some CAD views require this)
                try
                {
                    textDir = textDir.RotateBy(Math.PI / 2.0, textNormal).GetNormal();
                }
                catch { /* fallback: ignore if rotate fails */ }

                // Use current view to determine screen-right vector so text reads left-to-right on screen
                try
                {
                    var view = AcadUtils.Ed.GetCurrentView();
                    Vector3d viewDir = view.ViewDirection.GetNormal();
                    // screenRight = viewDir cross worldUp (approx)
                    Vector3d screenRight = viewDir.CrossProduct(new Vector3d(0, 0, 1));
                    if (screenRight.Length < 1e-6)
                    {
                        // fallback: use world X
                        screenRight = new Vector3d(1, 0, 0);
                    }
                    screenRight = screenRight.GetNormal();

                    if (textDir.DotProduct(screenRight) < 0)
                    {
                        textDir = (-textDir).GetNormal();
                        // Flip horizontal attachments: Left <-> Right
                        switch (attachment)
                        {
                            case AttachmentPoint.BottomLeft: attachment = AttachmentPoint.BottomRight; break;
                            case AttachmentPoint.BottomRight: attachment = AttachmentPoint.BottomLeft; break;
                            case AttachmentPoint.TopLeft: attachment = AttachmentPoint.TopRight; break;
                            case AttachmentPoint.TopRight: attachment = AttachmentPoint.TopLeft; break;
                            case AttachmentPoint.MiddleLeft: attachment = AttachmentPoint.MiddleRight; break;
                            case AttachmentPoint.MiddleRight: attachment = AttachmentPoint.MiddleLeft; break;
                        }

                        // Additionally consider flipping along the short-edge (textNormal) vs screenUp
                        // so text flips vertically when the short side faces away from camera.
                        Vector3d screenUp = screenRight.CrossProduct(viewDir).GetNormal();
                        if (textNormal.DotProduct(screenUp) < 0)
                        {
                            // Flip vertical attachments: Top <-> Bottom
                            switch (attachment)
                            {
                                case AttachmentPoint.BottomLeft: attachment = AttachmentPoint.TopLeft; break;
                                case AttachmentPoint.BottomRight: attachment = AttachmentPoint.TopRight; break;
                                case AttachmentPoint.TopLeft: attachment = AttachmentPoint.BottomLeft; break;
                                case AttachmentPoint.TopRight: attachment = AttachmentPoint.BottomRight; break;
                                case AttachmentPoint.BottomCenter: attachment = AttachmentPoint.TopCenter; break;
                                case AttachmentPoint.TopCenter: attachment = AttachmentPoint.BottomCenter; break;
                            }
                        }
                    }
                }
                catch
                {
                    // view unavail -> fallback to world X/Y as before
                    Vector3d refAxis = Math.Abs(textDir.X) >= Math.Abs(textDir.Y) ? new Vector3d(1, 0, 0) : new Vector3d(0, 1, 0);
                    if (textDir.DotProduct(refAxis) < 0)
                    {
                        textDir = (-textDir).GetNormal();
                        switch (attachment)
                        {
                            case AttachmentPoint.BottomLeft: attachment = AttachmentPoint.BottomRight; break;
                            case AttachmentPoint.BottomRight: attachment = AttachmentPoint.BottomLeft; break;
                            case AttachmentPoint.TopLeft: attachment = AttachmentPoint.TopRight; break;
                            case AttachmentPoint.TopRight: attachment = AttachmentPoint.TopLeft; break;
                            case AttachmentPoint.MiddleLeft: attachment = AttachmentPoint.MiddleRight; break;
                            case AttachmentPoint.MiddleRight: attachment = AttachmentPoint.MiddleLeft; break;
                        }
                    }
                }

                Point3d midPt = new Point3d(startPt.X, startPt.Y, (startPt.Z + endPt.Z) / 2.0);
                double offset = (textHeight / 2.0) + TEXT_GAP + 100.0; // extra gap for columns
                insertPoint = midPt + textNormal * offset;

                // Ensure MText.Rotation is90 degrees on text plane so visual matches baseline orientation
                rotation = Math.PI / 2.0;
                attachment = AttachmentPoint.MiddleLeft;
            }
            // CASE3: Slanted/Inclined beam -> text stands upright (normal perpendicular to beam and Z)
            else
            {
                Vector3d axisDir = delta.GetNormal();
                Vector3d upVec = new Vector3d(0, 0, 1);

                // If axis is nearly vertical, avoid flattening text by fallback to Z.
                // Instead pick a horizontal normal perpendicular to the axis projection,
                // so the text baseline still follows the (possibly tilted) axis direction.
                bool nearVertical = Math.Abs(axisDir.DotProduct(upVec)) > 0.98;
                if (nearVertical)
                {
                    textDir = axisDir.GetNormal();

                    // Project axis onto XY and compute a perpendicular horizontal vector
                    var proj = new Vector3d(axisDir.X, axisDir.Y, 0);
                    if (proj.Length < 1e-6)
                    {
                        // Axis purely Z -> choose X as normal
                        textNormal = new Vector3d(1, 0, 0);
                    }
                    else
                    {
                        // Perp in XY: (-y, x, 0)
                        textNormal = new Vector3d(-proj.Y, proj.X, 0).GetNormal();
                    }
                }
                else
                {
                    textNormal = axisDir.CrossProduct(upVec);
                    if (textNormal.Length < 0.001)
                    {
                        // fallback
                        textNormal = new Vector3d(0, 0, 1);
                    }
                    else
                    {
                        textNormal = textNormal.GetNormal();
                    }

                    textDir = axisDir;
                }

                Point3d basePt;
                if (position == LabelPosition.StartTop || position == LabelPosition.StartBottom) basePt = startPt;
                else if (position == LabelPosition.EndTop || position == LabelPosition.EndBottom) basePt = endPt;
                else basePt = startPt + axisDir * (length3d / 2.0);

                Vector3d textUp = textNormal.CrossProduct(textDir).GetNormal();
                bool isTop = (position == LabelPosition.StartTop || position == LabelPosition.MiddleTop || position == LabelPosition.EndTop);
                double sign = isTop ? 1.0 : -1.0;
                double offsetDist = (textHeight / 2.0) + TEXT_GAP;

                insertPoint = basePt + textUp * (offsetDist * sign);

                if (position == LabelPosition.MiddleTop || position == LabelPosition.MiddleBottom)
                    attachment = isTop ? AttachmentPoint.BottomCenter : AttachmentPoint.TopCenter;
                else
                    attachment = isTop ? AttachmentPoint.BottomLeft : AttachmentPoint.TopLeft;
            }

            // Tạo MText
            MText mtext = new MText();
            mtext.Contents = content;
            mtext.Location = insertPoint;
            mtext.TextHeight = textHeight;
            mtext.Layer = layer;
            mtext.ColorIndex = DEFAULT_COLOR;

            // Gán thuộc tính3D
            mtext.Normal = textNormal;
            mtext.Direction = textDir.GetNormal();
            mtext.Rotation = rotation; // hữu dụng khi Normal = Z
            mtext.Attachment = attachment;

            ObjectId id = btr.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);

            return id;
        }

        public static ObjectId PlotPointLabel(
            BlockTableRecord btr,
            Transaction tr,
            Point2D center,
            string content,
            double textHeight = DEFAULT_TEXT_HEIGHT,
            string layer = DEFAULT_LAYER)
        {
            if (btr == null || tr == null) return ObjectId.Null;
            MText m = new MText();
            m.Contents = content;
            m.Location = new Point3d(center.X, center.Y + TEXT_GAP, 0);
            m.TextHeight = textHeight;
            m.Layer = layer;
            m.ColorIndex = DEFAULT_COLOR;
            m.Attachment = AttachmentPoint.BottomCenter;

            ObjectId id = btr.AppendEntity(m);
            tr.AddNewlyCreatedDBObject(m, true);
            return id;
        }

        // Logic tính toán hình học2D (giữ nguyên logic cũ cho trường hợp nằm ngang)
        private struct LabelGeometry2D
        {
            public Point2D InsertPoint;
            public double Rotation;
            public AttachmentPoint Attachment;
        }

        private static LabelGeometry2D CalculateLabelGeometry2D(
            Point2D startPt, Point2D endPt,
            LabelPosition position, double textHeight)
        {
            var result = new LabelGeometry2D();
            double dx = endPt.X - startPt.X;
            double dy = endPt.Y - startPt.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);

            if (length < GeometryConstants.EPSILON)
            {
                result.InsertPoint = new Point2D(startPt.X + TEXT_GAP, startPt.Y);
                result.Rotation = 0;
                result.Attachment = AttachmentPoint.MiddleLeft;
                return result;
            }

            double ux = dx / length;
            double uy = dy / length;
            double angle = Math.Atan2(dy, dx);
            if (angle < 0) angle += 2 * Math.PI;

            bool isFlipped = (angle > Math.PI / 2 && angle <= 3 * Math.PI / 2);
            double readableAngle = isFlipped ? angle + Math.PI : angle;

            double perpX = -uy;
            double perpY = ux;

            Point2D basePoint;
            switch (position)
            {
                case LabelPosition.StartTop:
                case LabelPosition.StartBottom: basePoint = startPt; break;
                case LabelPosition.EndTop:
                case LabelPosition.EndBottom: basePoint = endPt; break;
                default: basePoint = new Point2D((startPt.X + endPt.X) / 2, (startPt.Y + endPt.Y) / 2); break;
            }

            bool isTopPos = (position == LabelPosition.StartTop || position == LabelPosition.MiddleTop || position == LabelPosition.EndTop);
            double offsetDist = (textHeight / 2.0) + TEXT_GAP;
            double directionMultiplier = isTopPos ? 1.0 : -1.0;
            if (isFlipped) directionMultiplier *= -1.0;

            result.InsertPoint = new Point2D(
                basePoint.X + perpX * offsetDist * directionMultiplier,
                basePoint.Y + perpY * offsetDist * directionMultiplier
            );

            result.Rotation = readableAngle;
            result.Attachment = isTopPos ? AttachmentPoint.BottomCenter : AttachmentPoint.TopCenter;

            // Tinh chỉnh căn lề trái/phải nếu ở đầu/cuối
            if (position.ToString().StartsWith("Start"))
                result.Attachment = isTopPos ? AttachmentPoint.BottomLeft : AttachmentPoint.TopLeft;
            if (position.ToString().StartsWith("End"))
                result.Attachment = isTopPos ? AttachmentPoint.BottomRight : AttachmentPoint.TopRight;

            if (isFlipped) // Đảo ngược lề nếu bị lật
            {
                if (result.Attachment == AttachmentPoint.BottomLeft) result.Attachment = AttachmentPoint.BottomRight;
                else if (result.Attachment == AttachmentPoint.BottomRight) result.Attachment = AttachmentPoint.BottomLeft;
                else if (result.Attachment == AttachmentPoint.TopLeft) result.Attachment = AttachmentPoint.TopRight;
                else if (result.Attachment == AttachmentPoint.TopRight) result.Attachment = AttachmentPoint.TopLeft;
            }

            return result;
        }
        #endregion

        /// <summary>
        /// Vẽ Label thép tại vị trí cụ thể (Start/Mid/End) và phía (Top/Bot).
        /// </summary>
        /// <param name="posIndex">0=Start, 1=Mid, 2=End</param>
        /// <param name="isTop">True=Vẽ mặt trên, False=Vẽ mặt dưới</param>
        public static void PlotRebarLabel(
            BlockTableRecord btr,
            Transaction tr,
            Point3d startPt,
            Point3d endPt,
            string content,
            int posIndex,
            bool isTop,
            double textHeight = 200.0)
        {
            // Xác định vị trí Base Point trên đường thẳng
            Point3d basePt;
            LabelPosition labelPos;

            // Logic mapping Index + Side -> Enum Position
            if (posIndex == 0) // Start
            {
                basePt = startPt;
                labelPos = isTop ? LabelPosition.StartTop : LabelPosition.StartBottom;
            }
            else if (posIndex == 2) // End
            {
                basePt = endPt;
                labelPos = isTop ? LabelPosition.EndTop : LabelPosition.EndBottom;
            }
            else // Mid
            {
                basePt = startPt + (endPt - startPt) * 0.5;
                labelPos = isTop ? LabelPosition.MiddleTop : LabelPosition.MiddleBottom;
            }

            // Gọi hàm vẽ PlotLabel cơ bản
            PlotLabel(btr, tr, startPt, endPt, content, labelPos, textHeight, "dts_rebar_text");
        }

        #endregion
    }
}