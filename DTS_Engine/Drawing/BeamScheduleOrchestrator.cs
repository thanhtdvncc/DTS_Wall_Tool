using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DTS_Engine.Core.Data;
using DTS_Engine.Drawing.Models;
using DTS_Engine.Drawing.Renderers;
using System.Collections.Generic;

namespace DTS_Engine.Drawing
{
    /// <summary>
    /// Điều phối việc vẽ toàn bộ bảng thống kê mặt cắt dầm.
    /// Xử lý phân trang (Pagination) và tiêu đề bảng.
    /// </summary>
    public class BeamScheduleOrchestrator
    {
        private readonly BeamBlockRenderer _blockRenderer;
        private readonly TableLayoutConfig _config;

        public BeamScheduleOrchestrator(TableLayoutConfig config)
        {
            _config = config;
            _blockRenderer = new BeamBlockRenderer(config);
        }

        public void OrchestrateDrawing(BlockTableRecord btr, List<BeamScheduleRowData> beams, Point3d startPoint, DrawingSettings settings)
        {
            // 0. Đảm bảo toàn bộ Layer tồn tại (Khử lỗi eKeyNotFound)
            EnsureLayers(btr.Database, settings);

            double accumulatedHeight = 0;
            double maxAllowedHeight = settings.MaxTableHeight;
            int colIndex = 0;
            Point3d currentPos = startPoint;

            // 1. Vẽ Header cho cột đầu tiên
            DrawTableHeader(btr, currentPos, settings);
            currentPos = new Point3d(currentPos.X, currentPos.Y - _config.RowHeight_Header, 0);
            accumulatedHeight = _config.RowHeight_Header;

            foreach (var beam in beams)
            {
                double blockHeight = beam.Cells.Count * _config.TotalRowHeight;

                // 2. Kiểm tra quy tắc ngắt cột (Pagination theo chiều cao)
                if (accumulatedHeight + blockHeight > maxAllowedHeight)
                {
                    // Vẽ đường kẻ đáy cho cột cũ trước khi ngắt
                    DrawTableBottom(btr, currentPos, settings);

                    colIndex++;
                    double totalTableWidth = GetTotalTableWidth();
                    double newX = startPoint.X + colIndex * (totalTableWidth + _config.ColumnMarginX);

                    currentPos = new Point3d(newX, startPoint.Y, 0);

                    // Vẽ lại Header cho cột bảng mới
                    DrawTableHeader(btr, currentPos, settings);
                    currentPos = new Point3d(currentPos.X, currentPos.Y - _config.RowHeight_Header, 0);
                    accumulatedHeight = _config.RowHeight_Header;
                }

                // 3. Vẽ một khối dầm (Beam Block)
                _blockRenderer.DrawBeamBlock(btr, currentPos, beam, settings);

                // Cập nhật vị trí Y đi xuống cho dầm tiếp theo
                currentPos = new Point3d(currentPos.X, currentPos.Y - blockHeight, 0);
                accumulatedHeight += blockHeight;
            }

            // Vẽ đường kẻ đáy cho cột cuối cùng
            DrawTableBottom(btr, currentPos, settings);
        }

        private void DrawTableBottom(BlockTableRecord btr, Point3d currentPos, DrawingSettings settings)
        {
            double totalWidth = GetTotalTableWidth();
            var pl = new Polyline(2);
            pl.AddVertexAt(0, new Point2d(currentPos.X, currentPos.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(currentPos.X + totalWidth, currentPos.Y), 0, 0, 0);
            pl.Layer = settings.LayerDim;
            pl.ColorIndex = settings.ColorDim;
            pl.ConstantWidth = 2.0;
            btr.AppendEntity(pl);
        }

        private void DrawTableHeader(BlockTableRecord btr, Point3d origin, DrawingSettings settings)
        {
            double h = _config.RowHeight_Header;
            double x = origin.X;

            // Danh sách các cột và tiêu đề
            var headers = new List<(string Text, double Width)>
            {
                ("MARK(B X H)", _config.ColWidth_Mark),
                ("LOCATION", _config.ColWidth_Loc),
                ("SECTION", _config.ColWidth_Section),
                ("TOP", _config.ColWidth_Rebar),
                ("BOTTOM", _config.ColWidth_Rebar),
                ("STIRRUP", _config.ColWidth_Stirrup),
                ("WEB BAR", _config.ColWidth_Web)
            };

            foreach (var header in headers)
            {
                DrawHeaderCell(btr, new Point3d(x, origin.Y, 0), header.Width, h, header.Text, settings);
                x += header.Width;
            }
        }

        private void DrawHeaderCell(BlockTableRecord btr, Point3d topLeft, double width, double height, string text, DrawingSettings settings)
        {
            // Vẽ khung ô header (Nét đậm)
            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(topLeft.X, topLeft.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(topLeft.X + width, topLeft.Y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(topLeft.X + width, topLeft.Y - height), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(topLeft.X, topLeft.Y - height), 0, 0, 0);
            pl.Closed = true;
            pl.Layer = settings.LayerDim;
            pl.ColorIndex = settings.ColorDim;
            pl.ConstantWidth = 2.0;
            btr.AppendEntity(pl);

            // Điền chữ header
            var mtext = new MText();
            mtext.Contents = text;
            mtext.Location = new Point3d(topLeft.X + width / 2.0, topLeft.Y - height / 2.0, 0);
            mtext.Attachment = AttachmentPoint.MiddleCenter;
            mtext.TextHeight = settings.TextHeight;
            mtext.Layer = settings.LayerText;
            mtext.ColorIndex = settings.ColorText;
            btr.AppendEntity(mtext);
        }

        private double GetTotalTableWidth()
        {
            return _config.ColWidth_Mark + _config.ColWidth_Loc + _config.ColWidth_Section +
                   (_config.ColWidth_Rebar * 2) + _config.ColWidth_Stirrup + _config.ColWidth_Web;
        }

        private void EnsureLayers(Database db, DrawingSettings settings)
        {
            DTS_Engine.Core.Utils.AcadUtils.UsingTransaction(tr =>
            {
                DTS_Engine.Core.Utils.AcadUtils.EnsureLayerExists(settings.LayerConcrete, tr, (short)settings.ColorConcrete);
                DTS_Engine.Core.Utils.AcadUtils.EnsureLayerExists(settings.LayerMainRebar, tr, (short)settings.ColorMainRebar);
                DTS_Engine.Core.Utils.AcadUtils.EnsureLayerExists(settings.LayerStirrup, tr, (short)settings.ColorStirrup);
                DTS_Engine.Core.Utils.AcadUtils.EnsureLayerExists(settings.LayerSideBar, tr, (short)settings.ColorSideBar);
                DTS_Engine.Core.Utils.AcadUtils.EnsureLayerExists(settings.LayerDim, tr, (short)settings.ColorDim);
                DTS_Engine.Core.Utils.AcadUtils.EnsureLayerExists(settings.LayerText, tr, (short)settings.ColorText);
            });
        }
    }
}
