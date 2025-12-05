using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using System;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
  /// Enum ??n v? ??ng b? v?i SAP2000 eUnits.
    /// ?? QUAN TR?NG - KHÔNG THAY ??I GIÁ TR? INT:
    /// - Giá tr? int ph?i KH?P CHÍNH XÁC v?i SAP2000v1.eUnits
    /// - Sai l?ch s? gây l?i ??n v? khi gán t?i sang SAP
    /// </summary>
    public enum DtsUnit
 {
        lb_in_F = 1,
    lb_ft_F = 2,
        kip_in_F = 3,
        kip_ft_F = 4,
        kN_mm_C = 5,    // M?c ??nh cho Vi?t Nam (AutoCAD v? mm, SAP dùng kN)
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
    /// Thông tin chi ti?t v? ??n v? hi?n t?i.
    /// Cung c?p h? s? quy ??i và tên ??n v? ?? hi?n th?.
    /// </summary>
    public class UnitInfo
    {
        /// <summary>
        /// ??n v? g?c (enum)
        /// </summary>
        public DtsUnit Unit { get; private set; }

  /// <summary>
        /// ??n v? l?c (kN, kgf, N, Ton, lb, kip)
        /// </summary>
        public string ForceUnit { get; private set; }

        /// <summary>
        /// ??n v? chi?u dài (mm, cm, m, in, ft)
        /// </summary>
        public string LengthUnit { get; private set; }

     /// <summary>
        /// H? s? nhân ?? ??i t? ??n v? CAD sang Mét.
      /// Ví d?: CAD v? mm -> Scale = 0.001
   ///        CAD v? m  -> Scale = 1.0
     /// 
   /// ?? CÔNG TH?C TÍNH T?I:
        /// Load (kN/m) = Thickness(mm) * LengthScaleToMeter * Height(mm) * LengthScaleToMeter * UnitWeight(kN/m³)
        /// </summary>
      public double LengthScaleToMeter { get; private set; }

        /// <summary>
        /// H? s? nhân ?? ??i t? ??n v? CAD sang Milimet.
  /// Dùng khi c?n xu?t sang SAP v?i ??n v? mm.
        /// </summary>
        public double LengthScaleToMm { get; private set; }

        public UnitInfo(DtsUnit unit)
        {
  Unit = unit;
          ParseUnit();
        }

        /// <summary>
        /// Phân tích chu?i enum ?? l?y ??n v? l?c và chi?u dài.
/// Ví d?: "kN_mm_C" -> ForceUnit = "kN", LengthUnit = "mm"
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

     // Xác ??nh h? s? quy ??i d?a trên ??n v? chi?u dài
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
         // Fallback: gi? ??nh mm (ph? bi?n nh?t ? VN)
           LengthScaleToMeter = 0.001;
             LengthScaleToMm = 1.0;
             break;
            }
        }

   /// <summary>
        /// Hi?n th? ??n v? d?ng "kN-mm" cho UI
        /// </summary>
        public override string ToString() => $"{ForceUnit}-{LengthUnit}";

     /// <summary>
        /// Hi?n th? ??n v? t?i phân b?, ví d?: "kN/m"
     /// </summary>
    public string GetLineLoadUnit() => $"{ForceUnit}/m";

        /// <summary>
      /// Hi?n th? ??n v? t?i di?n tích, ví d?: "kN/m²"
   /// </summary>
        public string GetAreaLoadUnit() => $"{ForceUnit}/m²";
    }

    /// <summary>
    /// Qu?n lý ??n v? toàn c?c cho DTS Tool.
    /// 
    /// ?? QUAN TR?NG - LOGIC HO?T ??NG:
    /// 1. ??n v? ???c l?u vào Named Object Dictionary c?a file DWG
/// 2. Khi m? b?n v? m?i, g?i Initialize() ?? ??c ??n v? ?ã l?u
    /// 3. Khi k?t n?i SAP, SapUtils.SyncUnits() s? ép SAP dùng cùng ??n v?
    /// 4. T?t c? tính toán t?i tr?ng ??u dùng Info.LengthScaleToMeter
    /// 
    /// ?? KHÔNG S?A ??I:
    /// - Tên dictionary DICT_NAME và KEY_UNIT (s? m?t data c?)
    /// - Logic SaveToDwg/LoadFromDwg (?nh h??ng persistence)
    /// </summary>
    public static class UnitManager
    {
        #region Constants - KHÔNG THAY ??I

        /// <summary>
        /// Tên Dictionary l?u settings trong DWG.
  /// ?? KHÔNG ??I TÊN - s? m?t d? li?u ?ã l?u trong các b?n v? c?.
 /// </summary>
        private const string DICT_NAME = "DTS_SETTINGS";

        /// <summary>
        /// Key l?u ??n v? hi?n t?i.
      /// ?? KHÔNG ??I TÊN - s? m?t d? li?u ?ã l?u trong các b?n v? c?.
        /// </summary>
        private const string KEY_UNIT = "CurrentUnit";

    #endregion

        #region State

        /// <summary>
        /// ??n v? hi?n t?i. M?c ??nh: kN_mm_C (ph? bi?n nh?t ? VN)
 /// </summary>
    private static DtsUnit _currentUnit = DtsUnit.kN_mm_C;

     /// <summary>
  /// Cache thông tin ??n v? ?? tránh t?o object m?i m?i l?n truy c?p
  /// </summary>
      private static UnitInfo _info = new UnitInfo(_currentUnit);

        /// <summary>
        /// Flag ?ánh d?u ?ã kh?i t?o t? DWG ch?a
        /// </summary>
        private static bool _initialized = false;

        #endregion

        #region Public Properties

        /// <summary>
      /// ??n v? hi?n t?i. Set s? t? ??ng l?u vào DWG.
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
   /// Thông tin chi ti?t v? ??n v? hi?n t?i.
    /// Dùng ?? l?y h? s? quy ??i và tên ??n v?.
        /// </summary>
    public static UnitInfo Info => _info;

        /// <summary>
        /// Ki?m tra ?ã kh?i t?o t? DWG ch?a
        /// </summary>
 public static bool IsInitialized => _initialized;

     #endregion

        #region Initialization

/// <summary>
        /// Kh?i t?o UnitManager t? file DWG hi?n t?i.
    /// ?? G?I HÀM NÀY KHI:
 /// - Plugin ???c load (IExtensionApplication.Initialize)
     /// - M? b?n v? m?i (Document.BeginDocumentClose event)
        /// 
    /// N?u DWG không có thông tin ??n v? -> dùng m?c ??nh kN_mm_C
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
// Fallback: gi? nguyên ??n v? m?c ??nh
     _initialized = true;
  }
      }

     /// <summary>
        /// Kh?i t?o UnitManager t? Database c? th?.
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
  // Fallback: gi? nguyên ??n v? m?c ??nh
    _initialized = true;
       }
   }

        #endregion

      #region Persistence

        /// <summary>
        /// L?u ??n v? hi?n t?i vào DWG.
     /// ?? KHÔNG S?A ??I LOGIC NÀY:
        /// - S? d?ng Named Object Dictionary ?? l?u persistent data
        /// - Xrecord ch?a TypedValue v?i DxfCode.Int16
      /// - T? ??ng t?o dictionary n?u ch?a có
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

            // T?o ho?c l?y dictionary DTS_SETTINGS
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

   // T?o Xrecord ch?a giá tr? ??n v?
       var xRec = new Xrecord();
  xRec.Data = new ResultBuffer(
    new TypedValue((int)DxfCode.Int16, (short)_currentUnit)
      );

             // Ghi ?è ho?c t?o m?i entry
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
         // Silent fail - không ?nh h??ng workflow chính
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
    /// Chuy?n ??i chi?u dài t? ??n v? CAD sang Mét.
      /// </summary>
        /// <param name="value">Giá tr? trong ??n v? CAD (mm/cm/m...)</param>
        /// <returns>Giá tr? trong Mét</returns>
        public static double ToMeter(double value)
        {
            return value * _info.LengthScaleToMeter;
        }

        /// <summary>
        /// Chuy?n ??i chi?u dài t? ??n v? CAD sang Milimet.
        /// Dùng khi xu?t sang SAP v?i setting kN_mm_C.
        /// </summary>
      /// <param name="value">Giá tr? trong ??n v? CAD</param>
        /// <returns>Giá tr? trong mm</returns>
    public static double ToMm(double value)
        {
    return value * _info.LengthScaleToMm;
  }

     /// <summary>
        /// Reset v? ??n v? m?c ??nh (kN_mm_C).
        /// Dùng cho testing ho?c khi c?n reset.
        /// </summary>
    public static void Reset()
        {
   _currentUnit = DtsUnit.kN_mm_C;
  _info = new UnitInfo(_currentUnit);
            _initialized = false;
  }

        /// <summary>
        /// L?y danh sách t?t c? ??n v? có s?n (cho UI dropdown)
        /// </summary>
        public static DtsUnit[] GetAllUnits()
        {
   return (DtsUnit[])Enum.GetValues(typeof(DtsUnit));
        }

        #endregion
    }
}
