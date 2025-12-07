using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using System;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Enum đơn vị đồng bộ với SAP2000 eUnits.
    /// QUAN TRỌNG - KHÔNG THAY ĐỔI GIÁ TRỊ INT:
    /// - Giá trị int phải KHỚP CHÍNH XÁC với SAP2000v1.eUnits
    /// - Sai lệch sẽ gây lỗi đơn vị khi gán tải sang SAP
    /// </summary>
    public enum DtsUnit
    {
        lb_in_F = 1,
        lb_ft_F = 2,
        kip_in_F = 3,
        kip_ft_F = 4,
        kN_mm_C = 5,    // Mặc định cho Việt Nam (AutoCAD vẽ mm, SAP dùng kN)
        kN_m_C = 6,
        kgf_mm_C = 7,
        kgf_m_C = 8,
        N_mm_C = 9,
        N_m_C = 10,
        Ton_mm_C = 11,
        Ton_m_C = 12,
        kN_cm_C = 13,
        kgf_cm_C = 14,
        N_cm_C = 15,
        Ton_cm_C = 16
    }

    /// <summary>
    /// Thông tin chi tiết về đơn vị hiện tại.
    /// Cung cấp hệ số quy đổi và tên đơn vị để hiển thị.
    /// 
    /// REFACTORED: Thêm hệ số quy đổi chuẩn hóa cho Force và Pressure
    /// Đảm bảo tất cả các phần mềm đọc tải từ SAP đều nhất quán.
    /// </summary>
    public class UnitInfo
    {
        /// <summary>
        /// Đơn vị gốc (enum)
        /// </summary>
        public DtsUnit Unit { get; private set; }

        /// <summary>
        /// Đơn vị lực (kN, kgf, N, Ton, lb, kip)
        /// </summary>
        public string ForceUnit { get; private set; }

        /// <summary>
        /// Đơn vị chiều dài (mm, cm, m, in, ft)
        /// </summary>
        public string LengthUnit { get; private set; }

        /// <summary>
        /// Hệ số nhân để đổi từ đơn vị CAD sang Mét.
        /// Ví dụ: CAD vẽ mm -> Scale = 0.001
        ///        CAD vẽ m  -> Scale = 1.0
        /// 
        /// CÔNG THỨC TÍNH TẢI:
        /// Load (kN/m) = Thickness(mm) * LengthScaleToMeter * Height(mm) * LengthScaleToMeter * UnitWeight(kN/m³)
        /// </summary>
        public double LengthScaleToMeter { get; private set; }

        /// <summary>
        /// Hệ số nhân để đổi từ đơn vị CAD sang Milimet.
        /// Dùng khi cần xuất sang SAP với đơn vị mm.
        /// </summary>
        public double LengthScaleToMm { get; private set; }

        /// <summary>
        /// Hệ số nhân để đổi lực từ đơn vị SAP sang kN (chuẩn hóa).
        /// Ví dụ: SAP dùng Ton -> Scale = 9.80665
        ///        SAP dùng kN  -> Scale = 1.0
        /// 
        /// CRITICAL: Dùng để chuyển đổi tải trọng tập trung (Point Loads)
        /// </summary>
        public double ForceScaleToKn { get; private set; }

        /// <summary>
        /// Hệ số quy đổi tải phân bố (Force/Length) từ SAP sang kN/m.
        /// = ForceScaleToKn / LengthScaleToMeter
        /// 
        /// Ví dụ: SAP kN_mm_C:
        /// - Value từ SAP: 0.008169 kN/mm
        /// - LineLoadScaleToKnPerM = 1.0 / 0.001 = 1000
        /// - Result: 0.008169 * 1000 = 8.169 kN/m
        /// 
        /// CRITICAL: Sửa lỗi "Số quá nhỏ" trong GetActiveLoadPatterns
        /// </summary>
        public double LineLoadScaleToKnPerM => ForceScaleToKn / LengthScaleToMeter;

        /// <summary>
        /// Hệ số quy đổi áp suất (Force/Area) từ SAP sang kN/m².
        /// = ForceScaleToKn / (LengthScaleToMeter)²
        /// 
        /// Ví dụ: SAP kN_mm_C:
        /// - Value từ SAP: 8.169e-7 kN/mm²
        /// - PressureScaleToKnPerM2 = 1.0 / (0.001)² = 1,000,000
        /// - Result: 8.169e-7 * 1,000,000 = 0.8169 kN/m²
        /// 
        /// CRITICAL: Sửa lỗi "Số quá nhỏ" cho Area Loads
        /// </summary>
        public double PressureScaleToKnPerM2 => ForceScaleToKn / Math.Pow(LengthScaleToMeter, 2);

        public UnitInfo(DtsUnit unit)
        {
            Unit = unit;
            ParseUnit();
        }

        /// <summary>
        /// Phân tích chuỗi enum để lấy đơn vị lực và chiều dài.
        /// Ví dụ: "kN_mm_C" -> ForceUnit = "kN", LengthUnit = "mm"
        /// 
        /// REFACTORED: Thêm logic tính ForceScaleToKn cho tất cả các đơn vị lực.
        /// </summary>
        private void ParseUnit()
        {
            string s = Unit.ToString();
            var parts = s.Split('_');

            if (parts.Length >= 2)
            {
                ForceUnit = parts[0];
                LengthUnit = parts[1];
            }
            else
            {
                // Fallback an toàn
                ForceUnit = "kN";
                LengthUnit = "mm";
            }

            // 1. Xác định hệ số quy đổi chiều dài
            switch (LengthUnit.ToLowerInvariant())
            {
                case "mm":
                    LengthScaleToMeter = 0.001;
                    LengthScaleToMm = 1.0;
                    break;
                case "cm":
                    LengthScaleToMeter = 0.01;
                    LengthScaleToMm = 10.0;
                    break;
                case "m":
                    LengthScaleToMeter = 1.0;
                    LengthScaleToMm = 1000.0;
                    break;
                case "in":
                    LengthScaleToMeter = 0.0254;
                    LengthScaleToMm = 25.4;
                    break;
                case "ft":
                    LengthScaleToMeter = 0.3048;
                    LengthScaleToMm = 304.8;
                    break;
                default:
                    // Fallback: giả định mm (phổ biến nhất ở VN)
                    LengthScaleToMeter = 0.001;
                    LengthScaleToMm = 1.0;
                    break;
            }

            // 2. Xác định hệ số quy đổi lực (từ đơn vị SAP sang kN)
            switch (ForceUnit.ToLowerInvariant())
            {
                case "kn":
                    ForceScaleToKn = 1.0;
                    break;
                case "n":
                    ForceScaleToKn = 0.001; // 1 N = 0.001 kN
                    break;
                case "kgf":
                    ForceScaleToKn = 0.00980665; // 1 kgf = 0.00980665 kN
                    break;
                case "ton":
                    ForceScaleToKn = 9.80665; // 1 Tấn (metric) = 9.80665 kN
                    break;
                case "lb":
                    ForceScaleToKn = 0.00444822; // 1 lb = 0.00444822 kN
                    break;
                case "kip":
                    ForceScaleToKn = 4.44822; // 1 kip = 4.44822 kN
                    break;
                default:
                    ForceScaleToKn = 1.0; // Fallback: giả định kN
                    break;
            }
        }

        /// <summary>
        /// Hiển thị đơn vị dạng "kN-mm" cho UI
        /// </summary>
        public override string ToString() => $"{ForceUnit}-{LengthUnit}";

        /// <summary>
        /// Hiển thị đơn vị tải phân bố, ví dụ: "kN/m"
        /// </summary>
        public string GetLineLoadUnit() => $"{ForceUnit}/m";

        /// <summary>
        /// Hiển thị đơn vị tải diện tích, ví dụ: "kN/m²"
        /// </summary>
        public string GetAreaLoadUnit() => $"{ForceUnit}/m²";
    }

    /// <summary>
    /// Quản lý đơn vị toàn cục cho DTS Tool.
    /// 
    /// QUAN TRỌNG - LOGIC HOẠT ĐỘNG:
    /// 1. Đơn vị được lưu vào Named Object Dictionary của file DWG
    /// 2. Khi mở bản vẽ mới, gọi Initialize() để đọc đơn vị đã lưu
    /// 3. Khi kết nối SAP, SapUtils.SyncUnits() sẽ ép SAP dùng cùng đơn vị
    /// 4. Tất cả tính toán tải trọng đều dùng Info.LengthScaleToMeter
    /// 
    /// KHÔNG SỬA ĐỔI:
    /// - Tên dictionary DICT_NAME và KEY_UNIT (sẽ mất data cũ)
    /// - Logic SaveToDwg/LoadFromDwg (ảnh hưởng persistence)
    /// </summary>
    public static class UnitManager
    {
        #region Constants - KHÔNG THAY ĐỔI

        /// <summary>
        /// Tên Dictionary lưu settings trong DWG.
        /// KHÔNG ĐỔI TÊN - sẽ mất dữ liệu đã lưu trong các bản vẽ cũ.
        /// </summary>
        private const string DICT_NAME = "DTS_SETTINGS";

        /// <summary>
        /// Key lưu đơn vị hiện tại.
        /// KHÔNG ĐỔI TÊN - sẽ mất dữ liệu đã lưu trong các bản vẽ cũ.
        /// </summary>
        private const string KEY_UNIT = "CurrentUnit";

        #endregion

        #region State

        /// <summary>
        /// Đơn vị hiện tại. Mặc định: kN_mm_C (phổ biến nhất ở VN)
        /// </summary>
        private static DtsUnit _currentUnit = DtsUnit.kN_mm_C;

        /// <summary>
        /// Cache thông tin đơn vị để tránh tạo object mới mỗi lần truy cập
        /// </summary>
        private static UnitInfo _info = new UnitInfo(_currentUnit);

        /// <summary>
        /// Flag đánh dấu đã khởi tạo từ DWG chưa
        /// </summary>
        private static bool _initialized = false;

        #endregion

        #region Public Properties

        /// <summary>
        /// Đơn vị hiện tại. Set sẽ tự động lưu vào DWG.
        /// </summary>
        public static DtsUnit CurrentUnit
        {
            get => _currentUnit;
            set
            {
                if (_currentUnit != value)
                {
                    _currentUnit = value;
                    _info = new UnitInfo(_currentUnit);
                    SaveToDwg();
                }
            }
        }

        /// <summary>
        /// Thông tin chi tiết về đơn vị hiện tại.
        /// Dùng để lấy hệ số quy đổi và tên đơn vị.
        /// </summary>
        public static UnitInfo Info => _info;

        /// <summary>
        /// Kiểm tra đã khởi tạo từ DWG chưa
        /// </summary>
        public static bool IsInitialized => _initialized;

        #endregion

        #region Initialization

        /// <summary>
        /// Khởi tạo UnitManager từ file DWG hiện tại.
        /// GỌI HÀM NÀY KHI:
        /// - Plugin được load (IExtensionApplication.Initialize)
        /// - Mở bản vẽ mới (Document.BeginDocumentClose event)
        /// 
        /// Nếu DWG không có thông tin đơn vị -> dùng mặc định kN_mm_C
        /// </summary>
        public static void Initialize()
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                Initialize(doc.Database);
            }
            catch
            {
                // Fallback: giữ nguyên đơn vị mặc định
                _initialized = true;
            }
        }

        /// <summary>
        /// Khởi tạo UnitManager từ Database cụ thể.
        /// </summary>
        public static void Initialize(Database db)
        {
            if (db == null) return;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);

                    if (nod.Contains(DICT_NAME))
                    {
                        var dtsDict = (DBDictionary)tr.GetObject(nod.GetAt(DICT_NAME), OpenMode.ForRead);

                        if (dtsDict.Contains(KEY_UNIT))
                        {
                            var xRec = (Xrecord)tr.GetObject(dtsDict.GetAt(KEY_UNIT), OpenMode.ForRead);

                            if (xRec.Data != null)
                            {
                                foreach (TypedValue tv in xRec.Data)
                                {
                                    if (tv.TypeCode == (int)DxfCode.Int16 || tv.TypeCode == (int)DxfCode.Int32)
                                    {
                                        int unitValue = Convert.ToInt32(tv.Value);

                                        // Validate enum value
                                        if (Enum.IsDefined(typeof(DtsUnit), unitValue))
                                        {
                                            _currentUnit = (DtsUnit)unitValue;
                                            _info = new UnitInfo(_currentUnit);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    tr.Commit();
                }

                _initialized = true;
            }
            catch
            {
                // Fallback: giữ nguyên đơn vị mặc định
                _initialized = true;
            }
        }

        #endregion

        #region Persistence

        /// <summary>
        /// Lưu đơn vị hiện tại vào DWG.
        /// KHÔNG SỬA ĐỔI LOGIC NÀY:
        /// - Sử dụng Named Object Dictionary để lưu persistent data
        /// - Xrecord chứa TypedValue với DxfCode.Int16
        /// - Tự động tạo dictionary nếu chưa có
        /// </summary>
        private static void SaveToDwg()
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                var db = doc.Database;

                using (var docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);

                    DBDictionary dtsDict;

                    // Tạo hoặc lấy dictionary DTS_SETTINGS
                    if (nod.Contains(DICT_NAME))
                    {
                        dtsDict = (DBDictionary)tr.GetObject(nod.GetAt(DICT_NAME), OpenMode.ForWrite);
                    }
                    else
                    {
                        dtsDict = new DBDictionary();
                        nod.SetAt(DICT_NAME, dtsDict);
                        tr.AddNewlyCreatedDBObject(dtsDict, true);
                    }

                    // Tạo Xrecord chứa giá trị đơn vị
                    var xRec = new Xrecord();
                    xRec.Data = new ResultBuffer(
                      new TypedValue((int)DxfCode.Int16, (short)_currentUnit)
                        );

                    // Ghi đè hoặc tạo mới entry
                    if (dtsDict.Contains(KEY_UNIT))
                    {
                        var oldRec = tr.GetObject(dtsDict.GetAt(KEY_UNIT), OpenMode.ForWrite);
                        oldRec.Erase();
                    }

                    dtsDict.SetAt(KEY_UNIT, xRec);
                    tr.AddNewlyCreatedDBObject(xRec, true);

                    tr.Commit();
                }
            }
            catch
            {
                // Silent fail - không ảnh hưởng workflow chính
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Chuyển đổi chiều dài từ đơn vị CAD sang Mét.
        /// </summary>
        /// <param name="value">Giá trị trong đơn vị CAD (mm/cm/m...)</param>
        /// <returns>Giá trị trong Mét</returns>
        public static double ToMeter(double value)
        {
            return value * _info.LengthScaleToMeter;
        }

        /// <summary>
        /// Chuyển đổi chiều dài từ đơn vị CAD sang Milimet.
        /// Dùng khi xuất sang SAP với setting kN_mm_C.
        /// </summary>
        /// <param name="value">Giá trị trong đơn vị CAD</param>
        /// <returns>Giá trị trong mm</returns>
        public static double ToMm(double value)
        {
            return value * _info.LengthScaleToMm;
        }

        /// <summary>
        /// Reset về đơn vị mặc định (kN_mm_C).
        /// Dùng cho testing hoặc khi cần reset.
        /// </summary>
        public static void Reset()
        {
            _currentUnit = DtsUnit.kN_mm_C;
            _info = new UnitInfo(_currentUnit);
            _initialized = false;
        }

        /// <summary>
        /// Lấy danh sách tất cả đơn vị có sẵn (cho UI dropdown)
        /// </summary>
        public static DtsUnit[] GetAllUnits()
        {
            return (DtsUnit[])Enum.GetValues(typeof(DtsUnit));
        }

        #endregion
    }
}
