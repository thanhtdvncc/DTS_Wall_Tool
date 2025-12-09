using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using DTS_Engine.Core.Engines;
using SAP2000v1;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DTS_Engine.Core.Utils
{
	/// <summary>
	/// Tiện ích kết nối và làm việc với SAP2000.
	/// Hỗ trợ đồng bộ 2 chiều: Đọc/Ghi tải trọng.
	/// 
	/// ⚠️ QUAN TRỌNG - ĐƠN VỊ:
	/// - Sử dụng UnitManager để đồng bộ đơn vị giữa CAD và SAP
	/// - Mọi giá trị chiều dài từ CAD đều qua UnitManager quy đổi
	/// - Tải trọng luôn xuất sang SAP theo đơn vị của UnitManager
	/// </summary>
	public static partial class SapUtils    
	{
		#region Connection

		private static cOAPI _sapObject = null;
		private static cSapModel _sapModel = null;

		/// <summary>
		/// Kết nối đến SAP2000 đang chạy (Sử dụng Helper chuẩn)
		/// 
		/// ⚠️ QUAN TRỌNG - KHÔNG SỬA HÀM NÀY:
		/// - PHẢI dùng cHelper.GetObject() - Cách DUY NHẤT ổn định cho SAP v26+
		/// - KHÔNG dùng Marshal.GetActiveObject() - Sẽ KHÔNG hoạt động với v26
		/// - KHÔNG thay đổi chuỗi "CSI.SAP2000.API.SapObject" (không có dấu cách)
		/// - Sau khi kết nối, tự động gọi SyncUnits() để đồng bộ đơn vị
		/// </summary>
		public static bool Connect(out string message)
		{
			_sapObject = null;
			_sapModel = null;
			message = "";

			try
			{
				// 1. Dùng Helper - Cách duy nhất ổn định cho SAP v26+
				cHelper myHelper = new Helper();

				// 2. Lấy object đang chạy (KHÔNG có dấu cách trong chuỗi)
				_sapObject = myHelper.GetObject("CSI.SAP2000.API.SapObject");

				if (_sapObject != null)
				{
					// 3. Lấy Model
					_sapModel = _sapObject.SapModel;

					// 4. Đồng bộ đơn vị với UnitManager (THAY ĐỔI QUAN TRỌNG)
					bool unitSet = false;
					try
					{
						eUnits sapUnit = (eUnits)(int)UnitManager.CurrentUnit;
						unitSet = _sapModel.SetPresentUnits(sapUnit) == 0;
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"SetPresentUnits failed: {ex.Message}");
					}

					bool syncOk = SyncUnits();
					if (unitSet || syncOk)
					{
						string modelName = "Unknown";
						try { modelName = System.IO.Path.GetFileName(_sapModel.GetModelFilename()); } catch { }

						message = $"Kết nối thành công: {modelName} | Đơn vị: {UnitManager.Info}";
					}
					else
					{
						message = "Kết nối OK nhưng không thể đồng bộ đơn vị SAP.";
					}

					return true;
				}
				else
				{
					message = "Không tìm thấy SAP2000 đang chạy. Hãy mở SAP2000 trước.";
					return false;
				}
			}
			catch (Exception ex)
			{
				message = $"Lỗi kết nối SAP: {ex.Message}";

				// Gợi ý fix lỗi COM phổ biến
				if (ex.Message.Contains("cast") || ex.Message.Contains("COM"))
				{
					message += "\n(Gợi ý: Chạy RegisterSAP2000.exe trong thư mục cài đặt SAP bằng quyền Admin)";
				}

				return false;
			}
		}

		/// <summary>
		/// Đồng bộ đơn vị: Set đơn vị của SAP model theo cài đặt trong UnitManager.
		/// 
		/// ⚠️ QUAN TRỌNG - LOGIC HOẠT ĐỘNG:
		/// - Ép kiểu enum DtsUnit sang SAP2000v1.eUnits (giá trị int giống nhau)
		/// - SAP sẽ sử dụng đơn vị này cho mọi thao tác tiếp theo
		/// - Đảm bảo tải trọng gán vào SAP có đơn vị đúng
		/// </summary>
		public static bool SyncUnits()
		{
			var model = GetModel();
			if (model == null) return false;

			try
			{
				// Ép kiểu Enum DTS sang Enum SAP
				// ⚠️ GIÁ TRỊ INT PHẢI KHỚP - xem DtsUnit enum trong UnitManager.cs
				eUnits sapUnit = (eUnits)(int)UnitManager.CurrentUnit;

				int ret = model.SetPresentUnits(sapUnit);
				return ret == 0;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"SyncUnits failed: {ex}");
				return false;
			}
		}

		/// <summary>
		/// Lấy đơn vị hiện tại của SAP model
		/// </summary>
		public static eUnits GetSapCurrentUnit()
		{
			var model = GetModel();
			if (model == null) return eUnits.kN_mm_C;

			try
			{
				return model.GetPresentUnits();
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"GetSapCurrentUnit failed: {ex}");
				return eUnits.kN_mm_C;
			}
		}

		public static void Disconnect()
		{
			_sapModel = null;
			_sapObject = null;
		}

		public static bool IsConnected => _sapModel != null;

		public static cSapModel GetModel()
		{
			if (_sapModel == null) Connect(out _);
			return _sapModel;
		}

		/// <summary>
		/// Safe helper to get current model filename (without path).
		/// Returns null if model is not available or an error occurs.
		/// </summary>
		public static string GetModelName()
		{
			var model = GetModel();
			if (model == null) return null;
			try
			{
				string fn = model.GetModelFilename();
				if (string.IsNullOrEmpty(fn)) return null;
				return System.IO.Path.GetFileName(fn);
			}
			catch { return null; }
		}

		#endregion

		#region Frame Geometry

		public static int CountFrames()
		{
			var model = GetModel();
			if (model == null) return -1;

			int count = 0;
			string[] names = null;
			int ret = model.FrameObj.GetNameList(ref count, ref names);
			return (ret == 0) ? count : 0;
		}

		public static List<SapFrame> GetAllFramesGeometry()
		{
			var listFrames = new List<SapFrame>();
			var model = GetModel();
			if (model == null) return listFrames;

			// BƯỚC 1: Lấy toàn bộ điểm một lần duy nhất (Siêu nhanh)
			var pointMap = GetAllPointsDictionary();

			int count = 0;
			string[] frameNames = null;
			// Lấy danh sách tên thanh
			model.FrameObj.GetNameList(ref count, ref frameNames);

			if (count == 0 || frameNames == null) return listFrames;

			// BƯỚC 2: Duyệt danh sách tên và tra cứu tọa độ
			foreach (var name in frameNames)
			{
				string p1Name = "", p2Name = "";
				// Gọi API lấy tên điểm nối (nhẹ hơn nhiều so với lấy tọa độ)
				int ret = model.FrameObj.GetPoints(name, ref p1Name, ref p2Name);
				if (ret != 0) continue;

				// Tra cứu từ RAM (tức thì)
				if (pointMap.TryGetValue(p1Name, out var pt1) && pointMap.TryGetValue(p2Name, out var pt2))
				{
					listFrames.Add(new SapFrame
					{
						Name = name,
						StartPt = new Point2D(pt1.X, pt1.Y),
						EndPt = new Point2D(pt2.X, pt2.Y),
						Z1 = pt1.Z,
						Z2 = pt2.Z
					});
				}
			}

			return listFrames;
		}

		public static SapFrame GetFrameGeometry(string frameName)
		{
			var model = GetModel();
			if (model == null) return null;

			string p1Name = "", p2Name = "";
			int ret = model.FrameObj.GetPoints(frameName, ref p1Name, ref p2Name);
			if (ret != 0) return null;

			double x1 = 0, y1 = 0, z1 = 0;
			ret = model.PointObj.GetCoordCartesian(p1Name, ref x1, ref y1, ref z1, "Global");
			if (ret != 0) return null;

			double x2 = 0, y2 = 0, z2 = 0;
			ret = model.PointObj.GetCoordCartesian(p2Name, ref x2, ref y2, ref z2, "Global");
			if (ret != 0) return null;

			return new SapFrame
			{
				Name = frameName,
				StartPt = new Point2D(x1, y1),
				EndPt = new Point2D(x2, y2),
				Z1 = z1,
				Z2 = z2
			};
		}

		public static List<SapFrame> GetBeamsAtElevation(double elevation, double tolerance = 200)
		{
			var result = new List<SapFrame>();
			var allFrames = GetAllFramesGeometry();

			foreach (var f in allFrames)
			{
				if (f.IsVertical) continue;

				double avgZ = (f.Z1 + f.Z2) / 2.0;
				if (Math.Abs(avgZ - elevation) <= tolerance)
				{
					result.Add(f);
				}
			}
			return result;
		}

		#endregion

		#region Load Reading - ĐỌC TẢI TRỌNG TỪ SAP

		/// <summary>
		/// REFACTORED: Đọc tất cả tải phân bố trên Frame.
		/// 
		/// ⚠️ CRITICAL FIX:
		/// - TRƯỚC KHI: Kiểm tra val > 0.001 TRƯỚC KHI convert → Bỏ sót tải nhỏ
		/// - SAU KHI: Convert NGAY LẬP TỨC bằng ConvertLoadToKnPerM() → Không bỏ sót
		/// 
		/// STRATEGY:
		/// 1. Đọc bảng "Frame Loads - Distributed" bằng SapTableReader
		/// 2. Convert giá trị SAP (kN/mm) sang kN/m NGAY LẬP TỨC
		/// 3. Lưu magnitude (|value|) để tránh bị cân bằng âm/dương
		/// 4. Cache Z-coordinate để tối ưu performance
		/// </summary>
		public static List<RawSapLoad> GetAllFrameDistributedLoads(string patternFilter = null)
		{
			var model = GetModel();
			if (model == null) return new List<RawSapLoad>();

			var inventory = new ModelInventory();
			inventory.Build();
			var reader = new SapDatabaseReader(model, inventory);
			return reader.ReadFrameDistributedLoads(patternFilter);
		}

		/// <summary>
		/// LEGACY METHOD: Kept for backward compatibility.
		/// Use GetAllFrameDistributedLoads() for better performance.
		/// </summary>
		[Obsolete("Use GetAllFrameDistributedLoads() instead", false)]
		private static List<RawSapLoad> GetAllFrameDistributedLoads_Legacy(string patternFilter = null)
		{
			var loads = new List<RawSapLoad>();
			var model = GetModel();
			if (model == null) return loads;
						
			var table = new SapTableReader(model, "Frame Loads - Distributed", patternFilter);
			if (table.RecordCount == 0) return loads;

			// Cache Z-coordinates để tránh gọi GetFrameGeometry nhiều lần
			var frameZCache = new Dictionary<string, double>();
			var allFrames = GetAllFramesGeometry();
			foreach (var f in allFrames) frameZCache[f.Name] = f.AverageZ;

			for (int i = 0; i < table.RecordCount; i++)
			{
				string frameName = table.GetString(i, "Frame");
				if (string.IsNullOrEmpty(frameName)) continue;

				string pattern = table.GetString(i, "LoadPat") ?? table.GetString(i, "OutputCase");
				if (string.IsNullOrEmpty(pattern)) continue;

				// ⚠️ FIX BUG #2: ĐỌC CẢ 2 ĐẦU TẢI HÌNH THANG
				double rawValueA = table.GetDouble(i, "FOverLA");
				double rawValueB = table.GetDouble(i, "FOverLB");
				
				// Nếu không có cột riêng A/B, thử đọc FOverL chung
				if (rawValueA == 0 && rawValueB == 0)
				{
					double fallback = table.GetDouble(i, "FOverL");
					rawValueA = fallback;
					rawValueB = fallback;
				}

				// Tính trung bình (cho tải hình thang) hoặc lấy giá trị duy nhất (tải đều)
				double rawValue = (rawValueA + rawValueB) / 2.0;

				// ⚠️ CRITICAL: Convert NGAY bằng UnitManager
				double normalizedValue = ConvertLoadToKnPerM(rawValue);

				// Đọc khoảng cách
				double distA = table.GetDouble(i, "AbsDistA");
				if (distA == 0) distA = table.GetDouble(i, "RelDistA");
				
				double distB = table.GetDouble(i, "AbsDistB");
				if (distB == 0) distB = table.GetDouble(i, "RelDistB");

				string direction = table.GetString(i, "Dir") ?? table.GetString(i, "Direction") ?? "Gravity";
				string coordSys = table.GetString(i, "CoordSys") ?? "GLOBAL";

				// Lấy Z từ cache
				double z = frameZCache.ContainsKey(frameName) ? frameZCache[frameName] : 0;

				loads.Add(new RawSapLoad
				{
					ElementName = frameName,
					LoadPattern = pattern,
					Value1 = normalizedValue, // Preserve sign from SAP
					LoadType = "FrameDistributed",
					Direction = direction,
					DistStart = distA,
					DistEnd = distB,
					CoordSys = coordSys,
					ElementZ = z,
					DirectionSign = Math.Sign(rawValue) // Giữ dấu nếu cần xử lý sau
				});
			}

			return loads;
		}

		/// <summary>
		/// BACKWARD COMPATIBILITY: Đọc tải phân bố trên một frame cụ thể.
		/// Wrapper gọi GetAllFrameDistributedLoads() rồi lọc theo frameName.
		/// 
		/// ⚠️ DEPRECATED: Nên dùng GetAllFrameDistributedLoads() cho performance tốt hơn.
		/// Method này giữ lại để không break existing code.
		/// </summary>
		public static List<SapLoadInfo> GetFrameDistributedLoads(string frameName, string loadPattern = null)
		{
			var results = new List<SapLoadInfo>();
			
			// Gọi method mới, rồi lọc và chuyển đổi sang format cũ
			var allLoads = GetAllFrameDistributedLoads(loadPattern);
			
			foreach (var load in allLoads)
			{
				if (!string.Equals(load.ElementName, frameName, StringComparison.OrdinalIgnoreCase))
					continue;

				results.Add(new SapLoadInfo
				{
					FrameName = load.ElementName,
					LoadPattern = load.LoadPattern,
					LoadValue = load.Value1, // Đã được convert sang kN/m
					DistanceI = load.DistStart,
					DistanceJ = load.DistEnd,
					Direction = load.Direction,
					LoadType = "Distributed"
				});
			}

			return results;
		}

		#region UNIT CONVERSION - CENTRALIZED STRATEGY (REFACTORED)

		/// <summary>
		/// REFACTORED: Chuyển đổi lực (Force) từ đơn vị SAP sang kN.
		/// Sử dụng UnitManager.Info.ForceScaleToKn làm Single Source of Truth.
		/// 
		/// Ví dụ: SAP dùng Ton -> sapValue * 9.80665 = kN
		/// </summary>
		public static double ConvertForceToKn(double sapValue)
		{
			return sapValue * UnitManager.Info.ForceScaleToKn;
		}

		/// <summary>
		/// REFACTORED: Chuyển đổi tải phân bố (Force/Length) từ SAP sang kN/m.
		/// Sử dụng UnitManager.Info.LineLoadScaleToKnPerM.
		/// 
		/// ⚠️ CRITICAL FIX:
		/// - TRƯỚC KHI: Kiểm tra sapValue > 0.001 RỒI MỚI convert → Bỏ sót tải nhỏ
		/// - SAU KHI: Convert NGAY, không threshold sớm → Không bỏ sót
		/// 
		/// Ví dụ: SAP kN_mm_C
		/// - Input: 0.008169 kN/mm (rất nhỏ)
		/// - Scale: 1.0 / 0.001 = 1000
		/// - Output: 8.169 kN/m (đúng)
		/// </summary>
		public static double ConvertLoadToKnPerM(double sapValue)
		{
			return sapValue * UnitManager.Info.LineLoadScaleToKnPerM;
		}

		/// <summary>
		/// REFACTORED: Chuyển đổi tải từ kN/m sang đơn vị SAP (dùng khi gán tải).
		/// Đảo ngược của ConvertLoadToKnPerM.
		/// </summary>
		private static double ConvertLoadFromKnPerM(double knPerMValue)
		{
			if (Math.Abs(UnitManager.Info.LineLoadScaleToKnPerM) < 1e-9) return 0;
			return knPerMValue / UnitManager.Info.LineLoadScaleToKnPerM;
		}

		/// <summary>
		/// REFACTORED: Chuyển đổi áp suất/tải diện tích (Force/Area) từ SAP sang kN/m².
		/// Sử dụng UnitManager.Info.PressureScaleToKnPerM2.
		/// 
		/// ⚠️ CRITICAL FIX:
		/// - TRƯỚC KHI: 8.169e-7 kN/mm² bị coi là 0 (quá nhỏ) → Báo "Chưa gán"
		/// - SAU KHI: Convert đúng → 0.8169 kN/m² (hiển thị chính xác)
		/// 
		/// Ví dụ: SAP kN_mm_C
		/// - Input: 8.169e-7 kN/mm²
		/// - Scale: 1.0 / (0.001)² = 1,000,000
		/// - Output: 0.8169 kN/m²
		/// </summary>
		public static double ConvertLoadToKnPerM2(double sapValue)
		{
			return sapValue * UnitManager.Info.PressureScaleToKnPerM2;
		}

		#endregion

		/// <summary>
		/// Đọc chi tiết các tải phân bố trên frame, nhóm theo pattern và kèm segments
		/// </summary>
		public static Dictionary<string, List<LoadEntry>> GetFrameDistributedLoadsDetailed(string frameName)
		{
			var result = new Dictionary<string, List<LoadEntry>>();
			var model = GetModel();
			if (model == null) return result;

			try
			{
				var rows = GetSapTableData("Frame Loads - Distributed");
				if (rows == null || rows.Count == 0) return result;

				foreach (var row in rows)
				{
					if (!row.ContainsKey("Frame")) continue;
					if (!string.Equals(row["Frame"], frameName, StringComparison.OrdinalIgnoreCase)) continue;
					string pattern = TryGetRowValue(row, "LoadPat") ?? TryGetRowValue(row, "OutputCase") ?? string.Empty;
					double val = ParseDouble(TryGetRowValue(row, "FOverLA") ?? TryGetRowValue(row, "FOverLB") ?? TryGetRowValue(row, "FOverL") ?? "0");
					val = ConvertLoadToKnPerM(val);

					double iPos = ParseDouble(TryGetRowValue(row, "AbsDistA") ?? TryGetRowValue(row, "RelDistA") ?? "0");
					double jPos = ParseDouble(TryGetRowValue(row, "AbsDistB") ?? TryGetRowValue(row, "RelDistB") ?? "0");
					string dir = TryGetRowValue(row, "Dir") ?? TryGetRowValue(row, "Direction") ?? "Gravity";

					var entry = new LoadEntry
					{
						Pattern = pattern,
						Value = val,
						Direction = dir,
						LoadType = "Distributed",
						Segments = new List<LoadSegment> { new LoadSegment { I = iPos, J = jPos } }
					};

					if (!result.ContainsKey(pattern)) result[pattern] = new List<LoadEntry>();
					result[pattern].Add(entry);
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"GetFrameDistributedLoadsDetailed failed: {ex}");
			}

			return result;
		}

		/// <summary>
		/// Đọc tổng tải trọng trên frame theo pattern
		/// </summary>
		public static double GetFrameTotalLoad(string frameName, string loadPattern)
		{
			var loads = GetFrameDistributedLoads(frameName, loadPattern);
			if (loads.Count == 0) return 0;

			double total = 0;
			foreach (var load in loads)
			{
				total += load.LoadValue;
			}
			return total;
		}

		/// <summary>
		/// Kiểm tra frame có tải trọng không
		/// </summary>
		public static bool FrameHasLoad(string frameName, string loadPattern)
		{
			var loads = GetFrameDistributedLoads(frameName, loadPattern);
			return loads.Count > 0;
		}

		/// <summary>
		/// Lấy danh sách frame đã có tải theo pattern
		/// </summary>
		public static Dictionary<string, List<SapLoadInfo>> GetAllFrameLoads(string loadPattern)
		{
			var result = new Dictionary<string, List<SapLoadInfo>>();
			var model = GetModel();
			if (model == null) return result;

			int count = 0;
			string[] frameNames = null;
			model.FrameObj.GetNameList(ref count, ref frameNames);

			if (frameNames == null) return result;

			foreach (var frameName in frameNames)
			{
				var loads = GetFrameDistributedLoads(frameName, loadPattern);
				if (loads.Count > 0)
				{
					result[frameName] = loads;
				}
			}

			return result;
		}

		private static string GetDirectionName(int dir)
		{
			switch (dir)
			{
				case 1: return "Local 1";
				case 2: return "Local 2";
				case 3: return "Local 3";
				case 4: return "X";
				case 5: return "Y";
				case 6: return "Z";
				case 7: return "X Projected";
				case 8: return "Y Projected";
				case 9: return "Z Projected";
				case 10: return "Gravity";
				case 11: return "Gravity Projected";
				default: return "Unknown";
			}
		}

		#endregion

		#region Load Writing - GHI TẢI TRỌNG VÀO SAP

		/// <summary>
		/// Gán tải phân bố lên frame.
		/// 
		/// ⚠️ XỬ LÝ ĐƠN VỊ:
		/// - loadValue phải là kN/m (đã chuẩn hóa)
		/// - Tự động quy đổi sang đơn vị SAP (kN/mm nếu dùng mm)
		/// </summary>
		public static bool AssignDistributedLoad(string frameName, string loadPattern,
	double loadValue, double distI = 0, double distJ = 0, bool isRelative = false)
		{
			var model = GetModel();
			if (model == null) return false;

			try
			{
				// Quy đổi từ kN/m sang đơn vị SAP
				double sapLoadValue = ConvertLoadFromKnPerM(loadValue);

				int ret = model.FrameObj.SetLoadDistributed(
					 frameName,
						 loadPattern,
						1,          // Force/Length
					10,         // Gravity Direction
				  distI, distJ,
					   sapLoadValue, sapLoadValue,
						 "Global",
				  isRelative,
					   true, // Replace existing
						 eItemType.Objects
							   );

				return ret == 0;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Gán tải từ MappingRecord
		/// </summary>
		public static bool AssignLoadFromMapping(MappingRecord mapping, string loadPattern, double loadValue)
		{
			if (string.IsNullOrEmpty(mapping.TargetFrame) || mapping.TargetFrame == "New")
				return false;

			return AssignDistributedLoad(
				mapping.TargetFrame,
		   loadPattern,
			 loadValue,
	  mapping.DistI,
  mapping.DistJ,
	   false
			);
		}

		/// <summary>
		/// Xóa tất cả tải trên frame theo pattern
		/// </summary>
		public static bool DeleteFrameLoads(string frameName, string loadPattern)
		{
			var model = GetModel();
			if (model == null) return false;

			try
			{
				int ret = model.FrameObj.DeleteLoadDistributed(frameName, loadPattern);
				return ret == 0;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Xóa và gán lại tải (đảm bảo clean)
		/// </summary>
		public static bool ReplaceFrameLoad(string frameName, string loadPattern,
	double loadValue, double distI = 0, double distJ = 0)
		{
			// Xóa tải cũ
			DeleteFrameLoads(frameName, loadPattern);

			// Gán tải mới
			return AssignDistributedLoad(frameName, loadPattern, loadValue, distI, distJ, false);
		}

		#endregion

		#region Frame Change Detection - PHÁT HIỆN THAY ĐỔI

		/// <summary>
		/// Tạo hash từ geometry frame để phát hiện thay đổi
		/// </summary>
		public static string GetFrameGeometryHash(string frameName)
		{
			var frame = GetFrameGeometry(frameName);
			if (frame == null) return null;

			string data = $"{frame.StartPt.X:0.0},{frame.StartPt.Y:0.0},{frame.Z1:0.0}|" +
		  $"{frame.EndPt.X:0.0},{frame.EndPt.Y:0.0},{frame.Z2:0.0}";

			return ComputeHash(data);
		}

		/// <summary>
		/// Tạo hash từ tải trọng frame
		/// </summary>
		public static string GetFrameLoadHash(string frameName, string loadPattern)
		{
			var loads = GetFrameDistributedLoads(frameName, loadPattern);
			if (loads.Count == 0) return "NOLOAD";

			var sortedLoads = loads.OrderBy(l => l.DistanceI).ToList();
			string data = string.Join("|", sortedLoads.Select(l =>
			$"{l.LoadValue:0.00},{l.DistanceI:0},{l.DistanceJ:0}"));

			return ComputeHash(data);
		}

		/// <summary>
		/// Kiểm tra frame có tồn tại trong SAP không
		/// </summary>
		public static bool FrameExists(string frameName)
		{
			var model = GetModel();
			if (model == null) return false;

			int count = 0;
			string[] names = null;
			model.FrameObj.GetNameList(ref count, ref names);

			if (names == null) return false;
			return names.Contains(frameName);
		}

		/// <summary>
		/// Tìm frame mới được tạo/merge gần vị trí cũ
		/// </summary>
		public static string FindReplacementFrame(Point2D oldStart, Point2D oldEnd, double elevation, double tolerance = 500)
		{
			var beams = GetBeamsAtElevation(elevation, 200);

			foreach (var beam in beams)
			{
				double dist1 = Math.Min(
		   oldStart.DistanceTo(beam.StartPt),
			oldStart.DistanceTo(beam.EndPt)
		   );
				double dist2 = Math.Min(
			  oldEnd.DistanceTo(beam.StartPt),
						oldEnd.DistanceTo(beam.EndPt)
				);

				if (dist1 < tolerance || dist2 < tolerance)
				{
					return beam.Name;
				}
			}

			return null;
		}

		private static string ComputeHash(string input)
		{
			using (var md5 = System.Security.Cryptography.MD5.Create())
			{
				byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
				byte[] hashBytes = md5.ComputeHash(inputBytes);
				return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
			}
		}

		#endregion

		#region ROBUST DATABASE TABLE READER (NEW CORE)

		/// <summary>
		/// Helper class để đọc bảng SAP an toàn, không sợ sai index cột.
		/// </summary>
		private class SapTableReader
		{
			private string[] _fields;
			private string[] _data;
			private int _numRecs;
			private int _colCount;
			private Dictionary<string, int> _colMap;

			public int RecordCount => _numRecs;

			public SapTableReader(cSapModel model, string tableName, string loadPatternFilter = null)
			{
				_colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				int tableVer = 0;
				string[] input = new string[] { };
				
				// Reset arrays
				_fields = null;
				_data = null;
				_numRecs = 0;

				// Cố gắng đọc bảng
				try
				{
					// Group "All" lấy toàn bộ, hoặc lọc theo Selection nếu cần
					// Ở đây ta dùng chiến lược: Đọc hết rồi lọc bằng C# để kiểm soát tốt hơn
					int ret = model.DatabaseTables.GetTableForDisplayArray(
						tableName, ref input, "All", ref tableVer, ref _fields, ref _numRecs, ref _data);
					if (ret == 0 && _numRecs > 0 && _fields != null && _data != null)
					{
						_colCount = _fields.Length;
						// Map tên cột sang index (trim và bỏ rỗng)
						for (int i = 0; i < _colCount; i++)
						{
							var f = _fields[i];
							if (!string.IsNullOrEmpty(f))
							{
								f = f.Trim();
								if (!_colMap.ContainsKey(f))
									_colMap[f] = i;
							}
						}
					}
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"SapTableReader: failed reading table '{tableName}': {ex.Message}");
				}
			}

			/// <summary>
			/// Kiểm tra xem cột có tồn tại không
			/// </summary>
			public bool HasColumn(string colName) => _colMap.ContainsKey(colName);

			/// <summary>
			/// Lấy giá trị chuỗi tại dòng row, cột colName
			/// </summary>
			public string GetString(int row, string colName)
			{
				if (row < 0 || row >= _numRecs) return null;
				if (_data == null || _colCount <= 0) return null;
				if (string.IsNullOrEmpty(colName)) return null;
				if (!_colMap.TryGetValue(colName, out int colIdx)) return null;
				int idx = row * _colCount + colIdx;
				if (idx < 0 || idx >= _data.Length) return null;
				return _data[idx];
			}

			/// <summary>
			/// Lấy giá trị double tại dòng row, cột colName. Trả về 0 nếu lỗi/rỗng.
			/// </summary>
			public double GetDouble(int row, string colName)
			{
				string val = GetString(row, colName);
				if (string.IsNullOrEmpty(val)) return 0.0;
				if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
					return result;
				return 0.0;
			}
		}

		#endregion
		#region AUDIT: Load Pattern Detection (REFACTORED - UNIFIED)

		public class PatternSummary
		{
			public string Name { get; set; }
			public double TotalEstimatedLoad { get; set; }
		}

		/// <summary>
		/// REFACTORED: Quét toàn bộ tải trọng để xác định Pattern hoạt động.
		///
		/// Implementation details:
		/// - Reads all load sources (frame, area, point, etc.) using existing methods with unit normalization.
		/// - Aggregates absolute magnitudes (Value1) per pattern name.
		/// - Ensures patterns that exist in the model but have zero total are still returned (with TotalEstimatedLoad = 0).
		/// - Returns results ordered by descending estimated load so the heaviest patterns come first.
		/// </summary>
		public static List<PatternSummary> GetActiveLoadPatterns()
		{
			var model = GetModel();
			if (model == null) return new List<PatternSummary>();

			var summaryMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

			try
			{
				foreach (var p in GetLoadPatterns())
				{
					if (!summaryMap.ContainsKey(p)) summaryMap[p] = 0.0;
				}
			}
			catch { }

			try
			{
				var inventory = new ModelInventory();
				inventory.Build();
				var reader = new SapDatabaseReader(model, inventory);
				var loads = reader.ReadAllLoads(null);

				foreach (var load in loads)
				{
					if (string.IsNullOrEmpty(load.LoadPattern)) continue;
					if (!summaryMap.TryGetValue(load.LoadPattern, out var current)) current = 0.0;
					summaryMap[load.LoadPattern] = current + Math.Abs(load.Value1);
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"Error reading loads via API: {ex.Message}");
			}

			return summaryMap
				.Select(kvp => new PatternSummary { Name = kvp.Key, TotalEstimatedLoad = kvp.Value })
				.OrderByDescending(ps => ps.TotalEstimatedLoad)
				.ToList();
		}

		#endregion

		#region Transformation Matrix & Local Axes API

		/// <summary>
		/// Kết quả GetElementVectors: 3 Vector trục địa phương trong hệ Global
		/// </summary>
		public struct ElementVectors
		{
			public Vector3D L1; // Local Axis 1
			public Vector3D L2; // Local Axis 2
			public Vector3D L3; // Local Axis 3
		}

		/// <summary>
		/// Lấy 3 vector trục địa phương của phần tử (Frame/Area/Point) trong hệ tọa độ Global.
		/// 
		/// CHIẾN LƯỢC:
		/// - Gọi API GetTransformationMatrix để lấy ma trận 3x3
		/// - Trích xuất 3 cột (mỗi cột = 1 vector trục)
		/// - Trả về ElementVectors chứa L1, L2, L3
		/// 
		/// SỬ DỤNG:
		/// - ModelInventory gọi hàm này 1 lần cho mỗi phần tử khi Build()
		/// - Cache kết quả để không phải gọi lại
		/// </summary>
		public static ElementVectors? GetElementVectors(string elementName)
		{
			var model = GetModel();
			if (model == null || string.IsNullOrEmpty(elementName))
				return null;

			try
			{
				double[] matrix = new double[9];
				int ret = -1;

				// Thử gọi API theo thứ tự: Frame -> Area -> Point
				// Frame
				try
				{
					ret = model.FrameObj.GetTransformationMatrix(elementName, ref matrix, true);
				}
				catch { }

				// Area
				if (ret != 0)
				{
					try
					{
						ret = model.AreaObj.GetTransformationMatrix(elementName, ref matrix, true);
					}
					catch { }
				}

				// Point
				if (ret != 0)
				{
					try
					{
						ret = model.PointObj.GetTransformationMatrix(elementName, ref matrix, true);
					}
					catch { }
				}

				if (ret != 0 || matrix == null || matrix.Length < 9)
					return null;

                // [SỬA LỖI QUAN TRỌNG]: Dựa trên tài liệu API SAP2000
                // Ma trận biến đổi Local -> Global:
                // [ c0 c1 c2 ]   [L1]   [GX]
                // [ c3 c4 c5 ] x [L2] = [GY]
                // [ c6 c7 c8 ]   [L3]   [GZ]
                //
                // Local 1 = (1,0,0) -> Global = (c0, c3, c6)
                // Local 2 = (0,1,0) -> Global = (c1, c4, c7)
                // Local 3 = (0,0,1) -> Global = (c2, c5, c8)

                return new ElementVectors
                {
                    // Cột 1: c0, c3, c6 (Index 0, 3, 6)
                    L1 = new Vector3D(matrix[0], matrix[3], matrix[6]),

                    // Cột 2: c1, c4, c7 (Index 1, 4, 7)
                    L2 = new Vector3D(matrix[1], matrix[4], matrix[7]),

                    // Cột 3: c2, c5, c8 (Index 2, 5, 8)
                    L3 = new Vector3D(matrix[2], matrix[5], matrix[8])
                };

            }
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"GetElementVectors failed for '{elementName}': {ex.Message}");
				return null;
			}
		}

		#endregion

		#region Load Patterns & Stories

		public static bool LoadPatternExists(string patternName)
		{
			var model = GetModel();
			if (model == null) return false;

			int count = 0;
			string[] names = null;
			model.LoadPatterns.GetNameList(ref count, ref names);

			if (names == null) return false;
			foreach (var n in names)
			{
				if (n.Equals(patternName, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		public static List<string> GetLoadPatterns()
		{
			var patterns = new List<string>();
			var model = GetModel();
			if (model == null) return patterns;

			int count = 0;
			string[] names = null;
			model.LoadPatterns.GetNameList(ref count, ref names);
			if (names != null) patterns.AddRange(names);
			return patterns;
		}

		// ...existing code for GridLines, Points, Stories...

		public class GridLineRecord
		{
			public string Name { get; set; }
			public string Orientation { get; set; }
			public double Coordinate { get; set; }
			public override string ToString() => $"{Name}: {Orientation}={Coordinate}";
		}

		public static List<GridLineRecord> GetGridLines()
		{
			var result = new List<GridLineRecord>();
			var model = GetModel();
			if (model == null) return result;

			string[] candidateTableKeys = new[] { "Grid Lines" };

			foreach (var tableKey in candidateTableKeys)
			{
				try
				{
					int tableVersion = 0;
					string[] fieldNames = null;
					string[] tableData = null;
					int numberRecords = 0;
					string[] fieldsKeysIncluded = null;

					string[] fieldKeyListInput = new string[] { "" };
					int ret = model.DatabaseTables.GetTableForDisplayArray(
					 tableKey,
				 ref fieldKeyListInput, "All", ref tableVersion, ref fieldsKeysIncluded,
					 ref numberRecords, ref tableData
					 );

					if (ret != 0 || tableData == null || fieldsKeysIncluded == null || numberRecords == 0)
						continue;

					int colCount = fieldsKeysIncluded.Length;
					if (colCount == 0) continue;

					int axisDirIdx = Array.IndexOf(fieldsKeysIncluded, "AxisDir");
					if (axisDirIdx < 0) axisDirIdx = Array.FindIndex(fieldsKeysIncluded, f => f != null && f.ToLowerInvariant().Contains("axis"));

					int gridIdIdx = Array.IndexOf(fieldsKeysIncluded, "GridID");
					if (gridIdIdx < 0) gridIdIdx = Array.FindIndex(fieldsKeysIncluded, f => f != null && (f.ToLowerInvariant().Contains("gridid") || f.ToLowerInvariant().Contains("grid")));

					int coordIdx = Array.IndexOf(fieldsKeysIncluded, "XRYZCoord");
					if (coordIdx < 0) coordIdx = Array.FindIndex(fieldsKeysIncluded, f => f != null && f.ToLowerInvariant().Contains("coord"));

					for (int r = 0; r < numberRecords; r++)
					{
						try
						{
							string axisDir = axisDirIdx >= 0 ? tableData[r * colCount + axisDirIdx]?.Trim() : null;
							string gridId = gridIdIdx >= 0 ? tableData[r * colCount + gridIdIdx]?.Trim() : null;
							string coordStr = coordIdx >= 0 ? tableData[r * colCount + coordIdx]?.Trim() : null;

							double coord = 0;
							if (!string.IsNullOrEmpty(coordStr))
							{
								var parts = coordStr.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
								if (parts.Length > 0)
								{
									var tryS = parts[0];
									if (!double.TryParse(tryS, NumberStyles.Any, CultureInfo.InvariantCulture, out coord))
									{
										double.TryParse(tryS, out coord);
									}
								}
							}

							if (!string.IsNullOrEmpty(axisDir))
								axisDir = axisDir.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries)[0];

							result.Add(new GridLineRecord
							{
								Name = gridId ?? string.Empty,
								Orientation = axisDir ?? string.Empty,
								Coordinate = coord
							});
						}
						catch { }
					}

					if (result.Count > 0) return result;
				}
				catch { }
			}

			return result;
		}

		public static void RefreshView()
		{
			GetModel()?.View.RefreshView();
		}

		#endregion

		#region Points

		public class SapPoint
		{
			public string Name { get; set; }
			public double X { get; set; }
			public double Y { get; set; }
			public double Z { get; set; }
		}

		public static List<SapPoint> GetAllPoints()
		{
			var result = new List<SapPoint>();
			var model = GetModel();
			if (model == null) return result;

			try
			{
				int tableVersion = 0;
				string[] tableData = null;
				int numberRecords = 0;
				string[] fieldsKeysIncluded = null;

				string[] fieldKeyListInput = new string[] { "" };

				int ret = model.DatabaseTables.GetTableForDisplayArray(
					 "Joint Coordinates",
					ref fieldKeyListInput, "All", ref tableVersion, ref fieldsKeysIncluded,
				ref numberRecords, ref tableData
					 );

				if (ret == 0 && numberRecords > 0 && fieldsKeysIncluded != null && tableData != null)
				{
					int idxName = Array.IndexOf(fieldsKeysIncluded, "Joint");
					int idxX = -1, idxY = -1, idxZ = -1;

					for (int i = 0; i < fieldsKeysIncluded.Length; i++)
					{
						var f = fieldsKeysIncluded[i] ?? string.Empty;
						var fl = f.ToLowerInvariant();
						if (fl.Contains("x") || fl.Contains("coord1") || fl.Contains("globalx") || fl.Contains("xor")) idxX = i;
						if (fl.Contains("y") || fl.Contains("coord2") || fl.Contains("globaly")) idxY = i;
						if (fl.Contains("z") || fl.Contains("coord3") || fl.Contains("globalz")) idxZ = i;
					}

					if (idxName >= 0 && idxX >= 0 && idxY >= 0 && idxZ >= 0)
					{
						int cols = fieldsKeysIncluded.Length;
						for (int r = 0; r < numberRecords; r++)
						{
							try
							{
								string name = tableData[r * cols + idxName] ?? string.Empty;
								double x = 0, y = 0, z = 0;
								double.TryParse(tableData[r * cols + idxX], NumberStyles.Any, CultureInfo.InvariantCulture, out x);
								double.TryParse(tableData[r * cols + idxY], NumberStyles.Any, CultureInfo.InvariantCulture, out y);
								double.TryParse(tableData[r * cols + idxZ], NumberStyles.Any, CultureInfo.InvariantCulture, out z);

								result.Add(new SapPoint { Name = name, X = x, Y = y, Z = z });
							}
							catch (Exception ex)
							{
								System.Diagnostics.Debug.WriteLine($"GetAllPoints: row parse failed: {ex}");
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"GetAllPoints failed: {ex}");
			}

			return result;
		}

		#endregion

		#region Stories

		public class GridStoryItem
		{
			public string AxisDir { get; set; }
			public string Name { get; set; }
			public double Coordinate { get; set; }
			public bool IsElevation => !string.IsNullOrEmpty(AxisDir) && AxisDir.Trim().StartsWith("Z", StringComparison.OrdinalIgnoreCase);

			public string StoryName
			{
				get => Name;
				set => Name = value;
			}

			public double Elevation
			{
				get => Coordinate;
				set => Coordinate = value;
			}

			public StoryData ToStoryData()
			{
				return new StoryData
				{
					StoryName = this.StoryName,
					Elevation = this.Elevation,
					StoryHeight = 3300
				};
			}

			public override string ToString() => $"{AxisDir}\t{Name}\t{Coordinate}";
		}

		public static List<GridStoryItem> GetStories()
		{
			var result = new List<GridStoryItem>();
			try
			{
				var grids = GetGridLines();
				foreach (var g in grids)
				{
					result.Add(new GridStoryItem
					{
						AxisDir = g.Orientation ?? string.Empty,
						Name = g.Name ?? string.Empty,
						Coordinate = g.Coordinate
					});
				}

				result = result.OrderBy(r => r.AxisDir).ThenBy(r => r.Coordinate).ToList();
			}
			catch { }
			return result;
		}

		#endregion

		#region DATABASE TABLE HELPERS (CORE FIX)

		// Cache ánh xạ Tên yêu cầu -> Tên chính xác (Name) và Mã (Key)
		private static Dictionary<string, TableInfo> _tableCache = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

		private class TableInfo { public string Name; public string Key; public bool IsEmpty; }

		/// <summary>
		/// Đọc bảng từ SAP2000 với quy trình "Đánh thức" (Wake-up) hệ thống.
		/// </summary>
		private static List<Dictionary<string, string>> GetSapTableData(string tableName, string patFilter = null)
		{
			var results = new List<Dictionary<string, string>>();
			var model = GetModel();
			if (model == null) return results;

			// BƯỚC 1: RESOLVE TABLE (Tìm tên chính xác & Key)
			var tableInfo = ResolveTableInfo(model, tableName);
			if (tableInfo == null)
			{
				// Nếu không tìm thấy, thử dùng nguyên tên gốc
				tableInfo = new TableInfo { Name = tableName, Key = tableName };
			}

				int ver = 0, num = 0;
			string[] fields = null, data = null;
			// Request 'All' group reliably across SAP versions
			string[] input = new string[] { "" };

			try
			{
				int ret = -1;

				// CHIẾN THUẬT 1: Đọc bằng TABLE NAME (Chuẩn)
				ret = model.DatabaseTables.GetTableForDisplayArray(tableInfo.Name, ref input, "All", ref ver, ref fields, ref num, ref data);

				// CHIẾN THUẬT 2: Nếu lỗi, Đọc bằng TABLE KEY (Dự phòng)
				if (ret != 0 && !string.IsNullOrEmpty(tableInfo.Key) && tableInfo.Key != tableInfo.Name)
				{
					ret = model.DatabaseTables.GetTableForDisplayArray(tableInfo.Key, ref input, "All", ref ver, ref fields, ref num, ref data);
				}

				// CHIẾN THUẬT 3: "KICKSTART" bằng Selection (Nếu vẫn lỗi)
				if (ret != 0 || num == 0)
				{
					// System.Diagnostics.Debug.WriteLine($"Kickstarting table '{tableInfo.Name}'...");
					model.SelectObj.All(false); // Chọn tất cả để ép SAP tính toán tham chiếu
					 
					// Thử đọc lại với group Selection
					ret = model.DatabaseTables.GetTableForDisplayArray(tableInfo.Name, ref input, "Selection", ref ver, ref fields, ref num, ref data);
					 
					model.SelectObj.ClearSelection(); // Dọn dẹp
				}

				if (ret != 0 || num == 0 || fields == null || data == null) return results;

				// --- Xử lý dữ liệu (Mapping & Filter) ---
				int cols = fields.Length;
				HashSet<string> pats = null;
				if (!string.IsNullOrEmpty(patFilter) && patFilter != "*")
					pats = new HashSet<string>(patFilter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
				 
				int patIdx = Array.FindIndex(fields, f => f == "LoadPat" || f == "OutputCase" || f == "LoadCase");

				for (int r = 0; r < num; r++)
				{
					if (pats != null && patIdx >= 0)
					{
						string val = data[r * cols + patIdx];
						if (string.IsNullOrEmpty(val) || !pats.Contains(val)) continue;
					}

					var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
					for (int c = 0; c < cols; c++)
					{
						row[fields[c]] = data[r * cols + c];
					}
					results.Add(row);
				}
			}
			catch (Exception ex) 
			{
				System.Diagnostics.Debug.WriteLine($"GetSapTableData Error: {ex.Message}");
				try { model.SelectObj.ClearSelection(); } catch { }
			}
			return results;
		}

		/// <summary>
		/// Tìm thông tin bảng, đồng thời gọi GetAvailableTables để "đánh thức" SAP.
		/// </summary>
		private static TableInfo ResolveTableInfo(cSapModel model, string reqName)
		{
			if (_tableCache.ContainsKey(reqName)) return _tableCache[reqName];

			int num = 0;
			string[] keys = null;
			string[] names = null;
			int[] types = null;
			bool[] empty = null;

			try
			{
				// QUAN TRỌNG: Gọi GetAvailableTables trước! 
				// Hàm này ép SAP kiểm tra trạng thái dữ liệu (giống ấn Ctrl+T), thay vì chỉ đọc định nghĩa như GetAllTables.
				int ret = model.DatabaseTables.GetAvailableTables(ref num, ref keys, ref names, ref types);
				 
				// Nếu GetAvailableTables trả về ít bảng (chưa load hết), gọi tiếp GetAllTables để lấy full list
				// (Nhưng việc gọi GetAvailableTables ở trên đã có tác dụng "mồi" hệ thống rồi)
				if (ret != 0 || num < 10) 
				{
					model.DatabaseTables.GetAllTables(ref num, ref keys, ref names, ref types, ref empty);
				}

				if (names != null && keys != null)
				{
					// 1. Tìm khớp chính xác
					for (int i = 0; i < names.Length; i++)
					{
						if (names[i].Equals(reqName, StringComparison.OrdinalIgnoreCase))
						{
							var info = new TableInfo { Name = names[i], Key = keys[i] };
							_tableCache[reqName] = info;
							return info;
						}
					}

					// 2. Tìm khớp gần đúng (bỏ khoảng trắng)
					string reqClean = reqName.Replace(" ", "").Replace("-", "").ToLowerInvariant();
					for (int i = 0; i < names.Length; i++)
					{
						string nameClean = names[i].Replace(" ", "").Replace("-", "").ToLowerInvariant();
						if (nameClean == reqClean)
						{
							var info = new TableInfo { Name = names[i], Key = keys[i] };
							_tableCache[reqName] = info;
							return info;
						}
					}
				}
			}
			catch { }

			return null;
		}

		private static string TryGetRowValue(Dictionary<string, string> row, params string[] keys)
		{
			foreach (var k in keys) if (row.TryGetValue(k, out string v)) return v;
			return null;
		}
		private static double ParseDouble(string s) { if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double d)) return d; return 0; }

		#endregion

		#region Extended Load Reading - AUDIT FEATURES

		/// <summary>
		/// Đọc tải tập trung (Point Load) trên Frame
		/// </summary>
		public static List<RawSapLoad> GetAllFramePointLoads(string patternFilter = null)
		{
			var results = new List<RawSapLoad>();
			var model = GetModel();
			if (model == null) return results;

			try
			{
				int frameCount = 0;
				string[] frameNames = null;
				model.FrameObj.GetNameList(ref frameCount, ref frameNames);
				if (frameCount == 0 || frameNames == null) return results;

				var frameGeometryCache = new Dictionary<string, SapFrame>();
				foreach (var fn in frameNames)
				{
					var geo = GetFrameGeometry(fn);
					if (geo != null) frameGeometryCache[fn] = geo;
				}

				foreach (var frameName in frameNames)
				{
					int numberItems = 0;
					string[] fNames = null;
					string[] loadPatterns = null;
					int[] myTypes = null;
					string[] csys = null;
					int[] dirs = null;
					double[] rd = null;
					double[] dist = null;
					double[] val = null;

					int ret = model.FrameObj.GetLoadPoint(
						frameName, ref numberItems, ref fNames, ref loadPatterns,
						ref myTypes, ref csys, ref dirs, ref rd, ref dist, ref val,
						eItemType.Objects);

					if (ret != 0 || numberItems == 0) continue;

					for (int i = 0; i < numberItems; i++)
					{
						if (!string.IsNullOrEmpty(patternFilter) &&
							!loadPatterns[i].Equals(patternFilter, StringComparison.OrdinalIgnoreCase))
							continue;

						double avgZ = 0;
						if (frameGeometryCache.TryGetValue(frameName, out var geo))
							avgZ = geo.AverageZ;

						results.Add(new RawSapLoad
						{
							ElementName = frameName,
							LoadPattern = loadPatterns[i],
							Value1 = ConvertForceToKn(val[i]),
							LoadType = "FramePoint",
							Direction = GetDirectionName(dirs[i]),
							DistStart = dist[i],
							IsRelative = rd[i] > 0.5,
							CoordSys = csys[i],
							ElementZ = avgZ
						});
					}
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"GetAllFramePointLoads failed: {ex}");
			}

			return results;
		}

		/// <summary>
		/// Đọc tải đều trên Area (Shell Uniform Load - kN/m²)
		/// SỬ DỤNG DATABASE TABLE "Area Loads - Uniform"
		/// </summary>
		public static List<RawSapLoad> GetAllAreaUniformLoads(string patternFilter = null)
		{
			var model = GetModel();
			if (model == null) return new List<RawSapLoad>();

			var inventory = new ModelInventory();
			inventory.Build();
			var reader = new SapDatabaseReader(model, inventory);
			return reader.ReadAllLoads(patternFilter)
				.Where(l => l.LoadType != null && l.LoadType.IndexOf("AreaUniform", StringComparison.OrdinalIgnoreCase) >= 0)
				.ToList();
		}

		/// <summary>
		/// Đọc tải Area Uniform To Frame (1-way/2-way distribution)
		/// SỬ DỤNG DATABASE TABLE "Area Loads - Uniform To Frame"
		/// </summary>
		public static List<RawSapLoad> GetAllAreaUniformToFrameLoads(string patternFilter = null)
		{
			var model = GetModel();
			if (model == null) return new List<RawSapLoad>();

			var inventory = new ModelInventory();
			inventory.Build();
			var reader = new SapDatabaseReader(model, inventory);
			return reader.ReadAllLoads(patternFilter)
				.Where(l => l.LoadType != null && l.LoadType.IndexOf("AreaUniformToFrame", StringComparison.OrdinalIgnoreCase) >= 0)
				.ToList();
		}

		/// <summary>
		/// Đọc tải tập trung trên Point/Joint
		/// SỬ DỤNG DATABASE TABLE "Joint Loads - Force"
		/// CORRECTS: Đọc đầy đủ F1, F2, F3 để không bỏ sót tải ngang
		/// </summary>
		public static List<RawSapLoad> GetAllPointLoads(string patternFilter = null)
		{
			var model = GetModel();
			if (model == null) return new List<RawSapLoad>();

			var inventory = new ModelInventory();
			inventory.Build();
			var reader = new SapDatabaseReader(model, inventory);
			return reader.ReadAllLoads(patternFilter)
				.Where(l => l.LoadType != null && l.LoadType.IndexOf("Point", StringComparison.OrdinalIgnoreCase) >= 0)
				.ToList();
		}

		/// <summary>
		/// Đọc Joint Mass (khối lượng tham gia dao động)
		/// </summary>
		public static List<RawSapLoad> GetAllJointMasses()
		{
			var results = new List<RawSapLoad>();
			var model = GetModel();
			if (model == null) return results;

			try
			{
				int pointCount = 0;
				string[] pointNames = null;
				model.PointObj.GetNameList(ref pointCount, ref pointNames);
				if (pointCount == 0 || pointNames == null) return results;

				var pointCache = GetAllPoints().ToDictionary(p => p.Name, p => p);

				foreach (var pointName in pointNames)
				{
					double[] m = new double[6];

					int ret = model.PointObj.GetMass(pointName, ref m);

					if (ret != 0) continue;

					// m[0], m[1], m[2] = mass in X, Y, Z directions
					double totalMass = m[0] + m[1] + m[2];
					if (totalMass < 0.001) continue;

					double z = 0;
					if (pointCache.TryGetValue(pointName, out var pt))
						z = pt.Z;

					results.Add(new RawSapLoad
					{
						ElementName = pointName,
						LoadPattern = "MASS",
						Value1 = totalMass, // kg or kN*s²/m depending on units
						LoadType = "JointMass",
						Direction = "All",
						ElementZ = z
					});
				}
			}
			catch { }

			return results;
		}

		/// <summary>
		/// Lấy tất cả Area Geometry (boundary points)
		/// </summary>
		public static List<SapArea> GetAllAreasGeometry()
		{
			var results = new List<SapArea>();
			var model = GetModel();
			if (model == null) return results;

			try
			{
				// BƯỚC 1: Lấy toàn bộ điểm vào bộ nhớ
				var pointMap = GetAllPointsDictionary();

				int areaCount = 0;
				string[] areaNames = null;
				model.AreaObj.GetNameList(ref areaCount, ref areaNames);
				 
				if (areaCount == 0 || areaNames == null) return results;

				// BƯỚC 2: Duyệt qua từng Area
				foreach (var areaName in areaNames)
				{
					int numPoints = 0;
					string[] pointNames = null;

					// Chỉ lấy TÊN các điểm (API này rất ổn định)
					int ret = model.AreaObj.GetPoints(areaName, ref numPoints, ref pointNames);
					 
					if (ret != 0 || numPoints < 3 || pointNames == null) continue;

					var area = new SapArea
					{
						Name = areaName,
						BoundaryPoints = new List<Point2D>(),
						ZValues = new List<double>(),
						JointNames = pointNames.ToList()
					};

					bool fullGeometry = true;
					foreach (var pName in pointNames)
					{
						// Tra cứu tọa độ từ RAM (Không gọi API nữa -> Không lỗi)
						if (pointMap.TryGetValue(pName, out var pt))
						{
							area.BoundaryPoints.Add(new Point2D(pt.X, pt.Y));
							area.ZValues.Add(pt.Z);
						}
						else
						{
							fullGeometry = false; // Mất điểm -> Bỏ qua Area này
							break;
						}
					}

					if (fullGeometry)
					{
						results.Add(area);
					}
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"GetAllAreasGeometry Error: {ex.Message}");
			}

			return results;
		}

		/// <summary>
		/// Helper: Biến danh sách điểm thành Dictionary để tra cứu O(1)
		/// </summary>
		private static Dictionary<string, SapPoint> GetAllPointsDictionary()
		{
			var dict = new Dictionary<string, SapPoint>(StringComparer.OrdinalIgnoreCase);
			var points = GetAllPoints(); // Hàm này đã có sẵn trong code của bạn (đọc bảng Joint Coordinates)
			foreach (var p in points)
			{
				if (!dict.ContainsKey(p.Name))
					dict[p.Name] = p;
			}
			return dict;
		}

		#endregion
	}
}