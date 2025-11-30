using System;
using SAP2000v1;
using static DTS_Wall_Tool.Core.Geometry; // Thư viện chuẩn

namespace DTS_Wall_Tool.Core
{
    public static class SapUtils
    {
        // Sử dụng đúng kiểu dữ liệu trong tài liệu hướng dẫn
        private static cOAPI _sapObject = null;
        private static cSapModel _sapModel = null;

        /// <summary>
        /// Kết nối với SAP2000 đang mở
        /// </summary>
        public static bool Connect(out string message)
        {
            _sapObject = null;
            _sapModel = null;
            message = "";

            try
            {
                // 1. Khởi tạo Helper (Theo tài liệu hướng dẫn)
                cHelper myHelper = new Helper();

                // 2. Lấy đối tượng SAP đang chạy (Attach to instance)
                // Lưu ý: SAP2000 phải đang mở và đã được Run as Admin một lần để đăng ký
                _sapObject = myHelper.GetObject("CSI.SAP2000.API.SapObject");

                if (_sapObject != null)
                {
                    // 3. Lấy Model
                    _sapModel = _sapObject.SapModel;

                    // 4. Khởi tạo đơn vị (kN, mm, C) -> Enum số 5
                    _sapModel.SetPresentUnits(eUnits.kN_mm_C);

                    message = "Kết nối SAP2000 v26 thành công!";
                    return true;
                }
                else
                {
                    message = "Không tìm thấy SAP2000 đang mở (GetObject trả về null).";
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = "Lỗi kết nối SAP2000.\nChi tiết: " + ex.Message;
                // Gợi ý sửa lỗi nếu gặp Invalid Cast
                if (ex.Message.Contains("cast") || ex.Message.Contains("COM"))
                {
                    message += "\n\nHãy thử chạy 'RegisterSAP2000.exe' trong thư mục cài đặt SAP bằng quyền Admin.";
                }
                return false;
            }
        }

        /// <summary>
        /// Lấy Model để dùng
        /// </summary>
        public static cSapModel GetModel()
        {
            if (_sapModel == null)
            {
                string msg;
                if (!Connect(out msg)) return null;
            }
            return _sapModel;
        }

        /// <summary>
        /// Đếm số lượng Frame để test
        /// </summary>
        public static int CountFrames()
        {
            var model = GetModel();
            if (model == null) return -1;

            int count = 0;
            string[] names = null;

            // Gọi API
            int ret = model.FrameObj.GetNameList(ref count, ref names);

            return (ret == 0) ? count : 0;
        }


        /// <summary>
        /// Lấy toàn bộ danh sách Dầm kèm tọa độ từ SAP2000
        /// </summary>
        public static System.Collections.Generic.List<SapFrame> GetAllFramesGeometry()
        {
            var listFrames = new System.Collections.Generic.List<SapFrame>();
            var model = GetModel();
            if (model == null) return listFrames;

            int count = 0;
            string[] frameNames = null;
            model.FrameObj.GetNameList(ref count, ref frameNames);

            if (count == 0) return listFrames;

            foreach (var name in frameNames)
            {
                string p1Name = "", p2Name = "";
                model.FrameObj.GetPoints(name, ref p1Name, ref p2Name);

                double x1 = 0, y1 = 0, z1 = 0;
                double x2 = 0, y2 = 0, z2 = 0;

                model.PointObj.GetCoordCartesian(p1Name, ref x1, ref y1, ref z1);
                model.PointObj.GetCoordCartesian(p2Name, ref x2, ref y2, ref z2);

                SapFrame frame = new SapFrame();
                frame.Name = name;
                frame.StartPt = new Point2D(x1, y1);
                frame.EndPt = new Point2D(x2, y2);

                // --- CẬP NHẬT MỚI: Lấy thêm Z ---
                frame.Z1 = z1;
                frame.Z2 = z2;

                listFrames.Add(frame);
            }

            return listFrames;
        }

        // --- HÀM MỚI: GÁN TẢI TRỌNG PHÂN BỐ (kN/m) ---
        public static bool AssignDistributedLoad(string frameName, string loadPattern, double startDist, double endDist, double loadVal_kNm)
        {
            var model = GetModel();
            if (model == null) return false;

            // Chuyển đổi đơn vị: kN/m -> kN/mm (Vì SAP đang set đơn vị chiều dài là mm)
            double val_kN_mm = loadVal_kNm / 1000.0;

            // Gọi API SetLoadDistributed
            // 1: Force/Length, 10: Gravity Direction
            // RelDist = false (Dùng khoảng cách tuyệt đối mm)
            // Replace = true (Ghi đè tải cũ)
            int ret = model.FrameObj.SetLoadDistributed(
                frameName, loadPattern, 1, 10,
                startDist, endDist, val_kN_mm, val_kN_mm,
                "Global", false, true, 0
            );

            return ret == 0;
        }


    }



}