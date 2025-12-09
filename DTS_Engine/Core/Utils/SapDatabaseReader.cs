using DTS_Engine.Core.Data;
using DTS_Engine.Core.Engines;
using DTS_Engine.Core.Interfaces;
using DTS_Engine.Core.Primitives;
using SAP2000v1;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DTS_Engine.Core.Utils
{
	/// <summary>
	/// ROBUST SAP2000 DATA READER (HYBRID MODE) v3.0 - FIXED VERSION
	/// 
	/// FIX BUG #3: ElementZ calculation - use proper elevation for columns vs beams
	/// FIX BUG #4: CalculateForceVector - correct priority order for direction parsing
	/// FIX BUG #6: Always prefer Direct API for reliability
	/// </summary>
	public class SapDatabaseReader : ISapLoadReader
	{
		private readonly cSapModel _model;
		private readonly Dictionary<string, TableSchema> _schemaCache;
		private readonly ModelInventory _inventory;

		private bool _useFallbackApi = false;
		private bool _preferApi = true;

		public SapDatabaseReader(cSapModel model, ModelInventory inventory = null)
		{
			_model = model ?? throw new ArgumentNullException(nameof(model));
			_schemaCache = new Dictionary<string, TableSchema>();
			_inventory = inventory;

			CheckDataSourceHealth();
		}

		private void CheckDataSourceHealth()
		{
			try
			{
				int num = 0;
				string[] keys = null;
				string[] names = null;
				int[] types = null;

				int ret = _model.DatabaseTables.GetAvailableTables(ref num, ref keys, ref names, ref types);

				if (ret != 0 || num == 0)
				{
					System.Diagnostics.Debug.WriteLine($"[SapDatabaseReader] Database Tables unavailable. Switching to API Mode.");
					_useFallbackApi = true;
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[SapDatabaseReader] Health check failed: {ex.Message}. Switching to API Mode.");
				_useFallbackApi = true;
			}
		}

		public List<RawSapLoad> ReadAllLoads(string patternFilter)
		{
			// FIX BUG #6: Always prefer Direct API for reliability
			if (_preferApi || _useFallbackApi)
			{
				try
				{
					var apiLoads = ReadAllLoads_ViaDirectAPI(patternFilter);
					if (apiLoads != null && apiLoads.Count > 0)
					{
						return apiLoads;
					}
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[SapDatabaseReader] API Mode failed: {ex.Message}.");
				}
			}

			// Table Mode fallback (rarely used now)
			if (_useFallbackApi)
			{
				return ReadAllLoads_ViaDirectAPI(patternFilter);
			}

			var loads = new List<RawSapLoad>();
			try
			{
				var frameLoads = ReadFrameDistributedLoads(patternFilter);
				if (frameLoads.Count == 0 && SapUtils.CountFrames() > 0)
				{
					if (ReadFrameDistributedLoads(null).Count == 0)
					{
						_useFallbackApi = true;
						return ReadAllLoads_ViaDirectAPI(patternFilter);
					}
				}

				loads.AddRange(frameLoads);
				loads.AddRange(ReadAreaUniformLoads(patternFilter));
				loads.AddRange(ReadAreaUniformToFrameLoads(patternFilter));
				loads.AddRange(ReadJointLoads(patternFilter));
				try { loads.AddRange(SapUtils.GetAllFramePointLoads(patternFilter)); } catch { }
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[SapDatabaseReader] Table Read Error: {ex.Message}. Switching to API.");
				_useFallbackApi = true;
				return ReadAllLoads_ViaDirectAPI(patternFilter);
			}

			return loads;
		}

		#region Schema Detection

		public class TableSchema
		{
			public string TableName { get; set; }
			public Dictionary<string, int> ColumnMap { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			public string[] FieldKeys { get; set; }
			public string[] TableData { get; set; }
			public int RecordCount { get; set; }
			public int ColumnCount { get; set; }

			public string GetString(int row, string columnName)
			{
				if (row < 0 || row >= RecordCount) return null;
				if (!ColumnMap.TryGetValue(columnName, out int colIdx)) return null;
				int idx = row * ColumnCount + colIdx;
				if (TableData == null || idx < 0 || idx >= TableData.Length) return null;
				return TableData[idx];
			}

			public double GetDouble(int row, string columnName)
			{
				var s = GetString(row, columnName);
				if (string.IsNullOrEmpty(s)) return 0.0;
				if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double d)) return d;
				return 0.0;
			}

			public string FindColumn(params string[] patterns)
			{
				foreach (var pattern in patterns)
				{
					foreach (var colName in ColumnMap.Keys)
					{
						if (colName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
							return colName;
					}
				}

				foreach (var pattern in patterns)
				{
					foreach (var colName in ColumnMap.Keys)
					{
						if (colName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
							return colName;
					}
				}

				return null;
			}
		}

		public TableSchema GetTableSchema(string tableName, string patternFilter = null)
		{
			string cacheKey = $"{tableName}|{patternFilter ?? "ALL"}";
			if (_schemaCache.TryGetValue(cacheKey, out var cached)) return cached;

			var schema = new TableSchema { TableName = tableName };
			try
			{
				int tableVer = 0;
				string[] fields = null;
				int numRec = 0;
				string[] tableData = null;
				string[] input = new string[] { "" };

				int ret = _model.DatabaseTables.GetTableForDisplayArray(tableName, ref input, "All", ref tableVer, ref fields, ref numRec, ref tableData);

				if (ret != 0)
				{
					_useFallbackApi = true;
					return schema;
				}

				if (numRec == 0 || fields == null || tableData == null)
				{
					return schema;
				}

				schema.FieldKeys = fields;
				schema.TableData = tableData;
				schema.RecordCount = numRec;
				schema.ColumnCount = fields.Length;

				for (int i = 0; i < fields.Length; i++)
					if (!string.IsNullOrEmpty(fields[i]))
						schema.ColumnMap[fields[i].Trim()] = i;

				_schemaCache[cacheKey] = schema;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[SapDatabaseReader] Exception reading table '{tableName}': {ex.Message}.");
				_useFallbackApi = true;
			}
			return schema;
		}

		#endregion

		#region Vector Calculation Helpers - FIX BUG #4

		private int NormalizeDirectionCode(string directionText, int fallback = 10)
		{
			if (int.TryParse(directionText, out var code)) return code;

			var dirUpper = directionText?.Trim().ToUpperInvariant();
			if (string.IsNullOrEmpty(dirUpper)) return fallback;
			
			// Check specific keywords first
			if (dirUpper.Contains("GRAV")) return 10;
			if (dirUpper.Contains("LOCAL"))
			{
				if (dirUpper.Contains("1")) return 1;
				if (dirUpper.Contains("2")) return 2;
				if (dirUpper.Contains("3")) return 3;
			}
			
			// Global check needs to be strict to avoid matching "Local X" if that exists
			if (dirUpper == "X" || dirUpper.Contains("GLOBAL X") || dirUpper.Contains("GLOBALX")) return 4;
			if (dirUpper == "Y" || dirUpper.Contains("GLOBAL Y") || dirUpper.Contains("GLOBALY")) return 5;
			if (dirUpper == "Z" || dirUpper.Contains("GLOBAL Z") || dirUpper.Contains("GLOBALZ")) return 6;

			return fallback;
		}

		private string GetDirectionDisplayName(int directionCode, string elementName)
		{
			// For Local axes (1,2,3), try to resolve to Global if element is vertical
			if ((directionCode == 1 || directionCode == 2 || directionCode == 3) && _inventory != null)
			{
				var info = _inventory.GetElement(elementName);
				if (info != null && info.IsVertical && directionCode == 3)
				{
					// Local 3 on vertical element = normal to wall surface
					// Determine if it's more X or Y aligned
					var axis3 = info.LocalAxis3;
					double absX = Math.Abs(axis3.X);
					double absY = Math.Abs(axis3.Y);
					
					if (absX > absY)
					{
						return axis3.X > 0 ? "+X" : "-X";
					}
					else
					{
						return axis3.Y > 0 ? "+Y" : "-Y";
					}
				}
			}

			// Standard mapping
			switch (directionCode)
			{
				case 1: return "Local 1";
				case 2: return "Local 2";
				case 3: return "Local 3";
				case 4: return "X";
				case 5: return "Y";
				case 6: return "Z";
				case 10:
				case 11: return "Gravity";
				default: return "Unknown";
			}
		}

		/// <summary>
		/// Overload: Legacy support without elementName parameter
		/// </summary>
		private string GetDirectionDisplayName(int directionCode)
		{
			return GetDirectionDisplayName(directionCode, null);
		}

        /// <summary>
        /// Calculate force vector using SAP direction codes with proper transformation matrix.
        /// FIX BUG #2: Use GetTransformationMatrix from SAP API for accurate Local→Global conversion
        /// ⚠️ CRITICAL: This fixes the most severe bug - incorrect force summation due to wrong vector direction
        /// </summary>
        private Vector3D CalculateForceVector(string elementName, double rawValue, int directionCode, string coordSys)
        {
            double signedVal = rawValue;

            // GLOBAL AXES (4=X, 5=Y, 6=Z, 10=Gravity...)
            if (directionCode >= 4)
            {
                switch (directionCode)
                {
                    case 4: return new Vector3D(signedVal, 0, 0);
                    case 5: return new Vector3D(0, signedVal, 0);
                    case 6: return new Vector3D(0, 0, signedVal);
                    case 10:
                    case 11: return new Vector3D(0, 0, -signedVal); // Gravity = -Z
                    default: return new Vector3D(0, 0, -signedVal);
                }
            }

            // LOCAL AXES (1, 2, 3)
            if (directionCode >= 1 && directionCode <= 3)
            {
                // [FIX]: Gọi trực tiếp Utility đã sửa chuẩn Matrix
                var vectors = SapUtils.GetElementVectors(elementName);

                if (vectors.HasValue)
                {
                    Vector3D axisVector;
                    switch (directionCode)
                    {
                        case 1: axisVector = vectors.Value.L1; break;
                        case 2: axisVector = vectors.Value.L2; break;
                        case 3: axisVector = vectors.Value.L3; break;
                        default: axisVector = new Vector3D(0, 0, 1); break;
                    }
                    return axisVector * signedVal;
                }
            }

            // Fallback an toàn
            return new Vector3D(0, 0, 1) * signedVal;
        }

        /// <summary>
        /// FIX BUG #3: Get correct Z elevation for element
        /// - For BEAMS (horizontal): Use AverageZ (midpoint Z is correct)
        /// - For COLUMNS (vertical): Use BOTTOM Z (MinZ - where column starts, this is the story it belongs to)
        /// - For AREAS: Use AverageZ
        /// - For POINTS: Use exact Z
        /// </summary>
        private double GetElementElevation(string elementName, string elementType)
		{
			if (_inventory != null)
			{
				var info = _inventory.GetElement(elementName);
				if (info != null)
				{
                    // FIX BUG #3: Use GetStoryElevation() which returns MinZ for columns
                    return info.IsVertical ? info.MinZ : info.AverageZ;
                }
			}

			// Fallback: Get from SapUtils with correct logic
			if (elementType.Contains("Frame"))
			{
				var frame = SapUtils.GetFrameGeometry(elementName);
				if (frame != null)
				{
					// For vertical elements (columns), use BOTTOM Z for story assignment
                        return frame.IsVertical ? Math.Min(frame.Z1, frame.Z2) : frame.AverageZ;
				}
			}
			else if (elementType.Contains("Point"))
			{
				var points = SapUtils.GetAllPoints();
				var pt = points.FirstOrDefault(p => p.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase));
				if (pt != null)
					return pt.Z;
			}
			else if (elementType.Contains("Area"))
			{
				var areas = SapUtils.GetAllAreasGeometry();
				var area = areas.FirstOrDefault(a => a.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase));
				if (area != null)
				{
					double minZ = area.ZValues.Count > 0 ? area.ZValues.Min() : area.AverageZ;
					double maxZ = area.ZValues.Count > 0 ? area.ZValues.Max() : area.AverageZ;
					bool isVertical = Math.Abs(maxZ - minZ) > Math.Max(area.BoundaryPoints.Select(p => p.X).DefaultIfEmpty().Max() - area.BoundaryPoints.Select(p => p.X).DefaultIfEmpty().Min(),
																  area.BoundaryPoints.Select(p => p.Y).DefaultIfEmpty().Max() - area.BoundaryPoints.Select(p => p.Y).DefaultIfEmpty().Min()) * 0.5;
					return isVertical ? minZ : area.AverageZ;
				}
			}

			return 0;
		}

		#endregion

		#region TABLE MODE: Readers

		public List<RawSapLoad> ReadFrameDistributedLoads(string patternFilter = null)
		{
			if (_useFallbackApi) return GetFrameLoads_DirectAPI(patternFilter);

			var loads = new List<RawSapLoad>();
			var schema = GetTableSchema("Frame Loads - Distributed", patternFilter);
			if (schema == null || schema.RecordCount == 0)
			{
				if (SapUtils.CountFrames() > 0)
				{
					_useFallbackApi = true;
					return GetFrameLoads_DirectAPI(patternFilter);
				}
				return loads;
			}

			string colFrame = schema.FindColumn("Frame");
			string colPattern = schema.FindColumn("LoadPat", "OutputCase");
			string colDir = schema.FindColumn("Dir");
			string colCoordSys = schema.FindColumn("CoordSys");
			string colFOverLA = schema.FindColumn("FOverLA", "FOverL");
			string colFOverLB = schema.FindColumn("FOverLB");
			string colAbsA = schema.FindColumn("AbsDistA");
			string colAbsB = schema.FindColumn("AbsDistB");
			string colRelA = schema.FindColumn("RelDistA");
			string colRelB = schema.FindColumn("RelDistB");

			if (colFrame == null || colPattern == null || colDir == null)
			{
				_useFallbackApi = true;
				return GetFrameLoads_DirectAPI(patternFilter);
			}

			for (int r = 0; r < schema.RecordCount; r++)
			{
				string frameName = schema.GetString(r, colFrame);
				string pattern = schema.GetString(r, colPattern);
				if (string.IsNullOrEmpty(frameName) || string.IsNullOrEmpty(pattern)) continue;
				if (!string.IsNullOrEmpty(patternFilter) && !pattern.Equals(patternFilter, StringComparison.OrdinalIgnoreCase)) continue;

				double valA = colFOverLA != null ? schema.GetDouble(r, colFOverLA) : 0.0;
				double valB = colFOverLB != null ? schema.GetDouble(r, colFOverLB) : valA;
				double avgVal = (valA + valB) / 2.0;
				// CRITICAL FIX: Store magnitude WITH sign preserved (no Math.Abs)
				double magnitude = SapUtils.ConvertLoadToKnPerM(avgVal);

				string dir = schema.GetString(r, colDir) ?? "Gravity";
				string sys = schema.GetString(r, colCoordSys) ?? "Global";

				double absA = colAbsA != null ? schema.GetDouble(r, colAbsA) : 0.0;
				double absB = colAbsB != null ? schema.GetDouble(r, colAbsB) : 0.0;
				double relA = colRelA != null ? schema.GetDouble(r, colRelA) : 0.0;
				double relB = colRelB != null ? schema.GetDouble(r, colRelB) : 0.0;

				bool hasAbs = Math.Abs(absA) > 1e-9 || Math.Abs(absB) > 1e-9;
				bool isRelative = !hasAbs && (Math.Abs(relA) > 1e-9 || Math.Abs(relB) > 1e-9);
				double distStart = hasAbs ? absA : relA;
				double distEnd = hasAbs ? absB : relB;

				int dirCode = NormalizeDirectionCode(dir);
				string dirDisplay = GetDirectionDisplayName(dirCode, frameName); // FIX: Pass element name

				var vec = CalculateForceVector(frameName, magnitude, dirCode, sys);
				double z = GetElementElevation(frameName, "Frame");

				var raw = new RawSapLoad
				{
					ElementName = frameName,
					LoadPattern = pattern,
					Value1 = magnitude, // Preserve sign from SAP
					LoadType = "FrameDistributed",
					Direction = dirDisplay,
					DirectionCode = dirCode,
					CoordSys = sys,
					DistStart = distStart,
					DistEnd = distEnd,
					IsRelative = isRelative,
					ElementZ = z
				};
				raw.SetForceVector(vec);
				loads.Add(raw);
			}

			return loads;
		}

		public List<RawSapLoad> ReadAreaUniformLoads(string patternFilter = null)
		{
			if (_useFallbackApi) return GetAreaLoads_DirectAPI(patternFilter);

			var loads = new List<RawSapLoad>();
			var schema = GetTableSchema("Area Loads - Uniform", patternFilter);
			if (schema == null || schema.RecordCount == 0)
			{
				try
				{
					int c = 0; string[] n = null; _model.AreaObj.GetNameList(ref c, ref n);
					if (c > 0) { _useFallbackApi = true; return GetAreaLoads_DirectAPI(patternFilter); }
				}
				catch { }
				return loads;
			}

			string colArea = schema.FindColumn("Area");
			string colPat = schema.FindColumn("LoadPat", "OutputCase");
			string colVal = schema.FindColumn("UnifLoad");
			string colDir = schema.FindColumn("Dir");
			string colCoordSys = schema.FindColumn("CoordSys");

			if (colArea == null || colPat == null || colDir == null)
			{
				_useFallbackApi = true;
				return GetAreaLoads_DirectAPI(patternFilter);
			}

			for (int r = 0; r < schema.RecordCount; r++)
			{
				string area = schema.GetString(r, colArea);
				string pat = schema.GetString(r, colPat);
				if (string.IsNullOrEmpty(area) || string.IsNullOrEmpty(pat)) continue;
				if (!string.IsNullOrEmpty(patternFilter) && !pat.Equals(patternFilter, StringComparison.OrdinalIgnoreCase)) continue;

				double val = schema.GetDouble(r, colVal);
				// CRITICAL FIX: Store magnitude WITH sign preserved
				double magnitude = SapUtils.ConvertLoadToKnPerM2(val);
				string dir = schema.GetString(r, colDir) ?? "Gravity";
				string sys = schema.GetString(r, colCoordSys) ?? "Local";

				int dirCode = NormalizeDirectionCode(dir);
				string dirDisplay = GetDirectionDisplayName(dirCode, area); // FIX: Pass element name
				var vec = CalculateForceVector(area, magnitude, dirCode, sys);
				double z = GetElementElevation(area, "Area");

				var raw = new RawSapLoad
				{
					ElementName = area,
					LoadPattern = pat,
					Value1 = magnitude, // Preserve sign
					LoadType = "AreaUniform",
					Direction = dirDisplay,
					DirectionCode = dirCode,
					CoordSys = sys,
					ElementZ = z
				};
				raw.SetForceVector(vec);
				loads.Add(raw);
			}

			return loads;
		}

		public List<RawSapLoad> ReadAreaUniformToFrameLoads(string patternFilter = null)
		{
			if (_useFallbackApi) return GetAreaUniformToFrameLoads_DirectAPI(patternFilter);

			var loads = new List<RawSapLoad>();
			var schema = GetTableSchema("Area Loads - Uniform To Frame", patternFilter);
			if (schema == null || schema.RecordCount == 0)
			{
				try { int c = 0; string[] n = null; _model.AreaObj.GetNameList(ref c, ref n); if (c > 0) { _useFallbackApi = true; return GetAreaUniformToFrameLoads_DirectAPI(patternFilter); } }
				catch { }
				return loads;
			}

			string colArea = schema.FindColumn("Area");
			string colPat = schema.FindColumn("LoadPat");
			string colVal = schema.FindColumn("UnifLoad");
			string colDir = schema.FindColumn("Dir");
			string colDist = schema.FindColumn("DistType");
			string colCoordSys = schema.FindColumn("CoordSys");

			if (colArea == null || colPat == null || colDir == null)
			{
				_useFallbackApi = true;
				return GetAreaUniformToFrameLoads_DirectAPI(patternFilter);
			}

			for (int r = 0; r < schema.RecordCount; r++)
			{
				string area = schema.GetString(r, colArea);
				string pat = schema.GetString(r, colPat);
				if (string.IsNullOrEmpty(area) || string.IsNullOrEmpty(pat)) continue;
				if (!string.IsNullOrEmpty(patternFilter) && !pat.Equals(patternFilter, StringComparison.OrdinalIgnoreCase)) continue;

				double val = schema.GetDouble(r, colVal);
				// CRITICAL FIX: Store magnitude WITH sign preserved
				double magnitude = SapUtils.ConvertLoadToKnPerM2(val);
				string dir = schema.GetString(r, colDir) ?? "Gravity";
				string sys = schema.GetString(r, colCoordSys) ?? "Global";

				int dirCode = NormalizeDirectionCode(dir);
				string dirDisplay = GetDirectionDisplayName(dirCode, area); // FIX: Pass element name
				var vec = CalculateForceVector(area, magnitude, dirCode, sys);
				double z = GetElementElevation(area, "Area");

				var raw = new RawSapLoad
				{
					ElementName = area,
					LoadPattern = pat,
					Value1 = magnitude, // Preserve sign
					LoadType = "AreaUniformToFrame",
					Direction = dirDisplay,
					DirectionCode = dirCode,
					CoordSys = sys,
					DistributionType = schema.GetString(r, colDist),
					ElementZ = z
				};
				raw.SetForceVector(vec);
				loads.Add(raw);
			}

			return loads;
		}

		public List<RawSapLoad> ReadJointLoads(string patternFilter = null)
		{
			if (_useFallbackApi) return GetJointLoads_DirectAPI(patternFilter);

			var loads = new List<RawSapLoad>();
			var schema = GetTableSchema("Joint Loads - Force", patternFilter);
			if (schema == null || schema.RecordCount == 0) return loads;

			string colJoint = schema.FindColumn("Joint");
			string colPat = schema.FindColumn("LoadPat");
			string colF1 = schema.FindColumn("F1");
			string colF2 = schema.FindColumn("F2");
			string colF3 = schema.FindColumn("F3");
			string colSys = schema.FindColumn("CoordSys");

			if (colJoint == null || colPat == null)
			{
				_useFallbackApi = true;
				return GetJointLoads_DirectAPI(patternFilter);
			}

			for (int r = 0; r < schema.RecordCount; r++)
			{
				string joint = schema.GetString(r, colJoint);
				string pat = schema.GetString(r, colPat);
				if (string.IsNullOrEmpty(joint) || string.IsNullOrEmpty(pat)) continue;
				if (!string.IsNullOrEmpty(patternFilter) && !pat.Equals(patternFilter, StringComparison.OrdinalIgnoreCase)) continue;

				double f1 = schema.GetDouble(r, colF1);
				double f2 = schema.GetDouble(r, colF2);
				double f3 = schema.GetDouble(r, colF3);
				string sys = schema.GetString(r, colSys) ?? "Global";

				double z = GetElementElevation(joint, "Point");

				void AddComp(double val, string dirName, string localAxis)
				{
					if (Math.Abs(val) < 1e-6) return;
					double mag = SapUtils.ConvertForceToKn(val);
					int dirCode = NormalizeDirectionCode(localAxis);
					var vec = CalculateForceVector(joint, mag, dirCode, sys);
					string dirDisplay = string.IsNullOrEmpty(dirName) ? GetDirectionDisplayName(dirCode) : dirName;

					var raw = new RawSapLoad
					{
						ElementName = joint,
						LoadPattern = pat,
						Value1 = mag,
						LoadType = "PointForce",
						Direction = dirDisplay,
						DirectionCode = dirCode,
						CoordSys = sys,
						ElementZ = z
					};
					raw.SetForceVector(vec);
					loads.Add(raw);
				}

				AddComp(f1, "F1/X", "1");
				AddComp(f2, "F2/Y", "2");
				AddComp(f3, "F3/Z", "3");
			}

			return loads;
		}

		#endregion

		#region FALLBACK MODE: Direct API Readers

		private List<RawSapLoad> ReadAllLoads_ViaDirectAPI(string patternFilter)
		{
			var results = new List<RawSapLoad>();

			try { results.AddRange(GetFrameLoads_DirectAPI(patternFilter)); }
			catch (Exception e) { System.Diagnostics.Debug.WriteLine("API Frame Load Error: " + e.Message); }

			try { results.AddRange(GetAreaLoads_DirectAPI(patternFilter)); }
			catch (Exception e) { System.Diagnostics.Debug.WriteLine("API Area Load Error: " + e.Message); }

			try { results.AddRange(GetAreaUniformToFrameLoads_DirectAPI(patternFilter)); }
			catch (Exception e) { System.Diagnostics.Debug.WriteLine("API AreaToFrame Error: " + e.Message); }

			try { results.AddRange(GetJointLoads_DirectAPI(patternFilter)); }
			catch (Exception e) { System.Diagnostics.Debug.WriteLine("API Joint Load Error: " + e.Message); }

			try { results.AddRange(SapUtils.GetAllFramePointLoads(patternFilter)); } catch { }

			return results;
		}

		private List<RawSapLoad> GetFrameLoads_DirectAPI(string patternFilter)
		{
			var list = new List<RawSapLoad>();
			int count = 0;
			string[] frameNames = null;

			_model.FrameObj.GetNameList(ref count, ref frameNames);
			if (count == 0 || frameNames == null) return list;

			foreach (var name in frameNames)
			{
				int numItems = 0;
				string[] frameArr = null;
				string[] patArr = null;
				int[] typeArr = null;
				string[] csysArr = null;
				int[] dirArr = null;
				double[] rd1Arr = null;
				double[] rd2Arr = null;
				double[] dist1Arr = null;
				double[] dist2Arr = null;
				double[] val1Arr = null;
				double[] val2Arr = null;

				int ret = _model.FrameObj.GetLoadDistributed(name, ref numItems, ref frameArr, ref patArr,
					ref typeArr, ref csysArr, ref dirArr, ref rd1Arr, ref rd2Arr, ref dist1Arr, ref dist2Arr, ref val1Arr, ref val2Arr, eItemType.Objects);

				if (ret == 0 && numItems > 0 && patArr != null)
				{
					for (int i = 0; i < numItems; i++)
					{
						if (typeArr != null && i < typeArr.Length && typeArr[i] != 1) continue;
						if (!string.IsNullOrEmpty(patternFilter) && !patArr[i].Equals(patternFilter, StringComparison.OrdinalIgnoreCase)) continue;

						double v1 = (val1Arr != null && i < val1Arr.Length) ? val1Arr[i] : 0.0;
						double v2 = (val2Arr != null && i < val2Arr.Length) ? val2Arr[i] : v1;
						// CRITICAL FIX: Store magnitude WITH sign preserved
						double magnitude = SapUtils.ConvertLoadToKnPerM((v1 + v2) / 2.0);

						double distStart = (dist1Arr != null && i < dist1Arr.Length) ? dist1Arr[i] : 0.0;
						double distEnd = (dist2Arr != null && i < dist2Arr.Length) ? dist2Arr[i] : distStart;
						bool isRelative = false;
						if (rd1Arr != null && i < rd1Arr.Length && Math.Abs(rd1Arr[i]) > 1e-9)
						{
							distStart = rd1Arr[i];
							isRelative = true;
						}
						if (rd2Arr != null && i < rd2Arr.Length && Math.Abs(rd2Arr[i]) > 1e-9)
						{
							distEnd = rd2Arr[i];
							isRelative = true;
						}

						int dirCode = (dirArr != null && i < dirArr.Length) ? dirArr[i] : 10;
						string dirStr = GetDirectionDisplayName(dirCode, name); // FIX: Pass element name
						string csys = (csysArr != null && i < csysArr.Length) ? csysArr[i] : "Global";

						var vec = CalculateForceVector(name, magnitude, dirCode, csys);
						double z = GetElementElevation(name, "Frame");

						var raw = new RawSapLoad
						{
							ElementName = name,
							LoadPattern = patArr[i],
							Value1 = magnitude, // Preserve sign
							LoadType = "FrameDistributed",
							Direction = dirStr,
							DirectionCode = dirCode,
							CoordSys = csys,
							DistStart = distStart,
							DistEnd = distEnd,
							IsRelative = isRelative,
							ElementZ = z
						};
						raw.SetForceVector(vec);
						list.Add(raw);
					}
				}
			}
			return list;
		}

		private List<RawSapLoad> GetAreaLoads_DirectAPI(string patternFilter)
		{
			var list = new List<RawSapLoad>();
			int count = 0;
			string[] names = null;
			_model.AreaObj.GetNameList(ref count, ref names);
			if (count == 0 || names == null) return list;

			foreach (var name in names)
			{
				int num = 0;
				string[] areaArr = null;
				string[] patArr = null;
				string[] csysArr = null;
				int[] dirArr = null;
				double[] valArr = null;

				int ret = _model.AreaObj.GetLoadUniform(name, ref num, ref areaArr, ref patArr, ref csysArr, ref dirArr, ref valArr, eItemType.Objects);

				if (ret == 0 && num > 0 && patArr != null)
				{
					for (int i = 0; i < num; i++)
					{
						if (!string.IsNullOrEmpty(patternFilter) && !patArr[i].Equals(patternFilter, StringComparison.OrdinalIgnoreCase)) continue;

						// CRITICAL FIX: Store magnitude WITH sign preserved
						double magnitude = SapUtils.ConvertLoadToKnPerM2(valArr[i]);
							int dirCode = (dirArr != null && i < dirArr.Length) ? dirArr[i] : 10;
							string dirStr = GetDirectionDisplayName(dirCode, name); // FIX: Pass element name
						string csys = (csysArr != null && i < csysArr.Length) ? csysArr[i] : "Local";

							var vec = CalculateForceVector(name, magnitude, dirCode, csys);
						double z = GetElementElevation(name, "Area");

						var raw = new RawSapLoad
						{
							ElementName = name,
							LoadPattern = patArr[i],
							Value1 = magnitude, // Preserve sign
							LoadType = "AreaUniform",
								Direction = dirStr,
								DirectionCode = dirCode,
							CoordSys = csys,
							ElementZ = z
						};
						raw.SetForceVector(vec);
						list.Add(raw);
					}
				}
			}
			return list;
		}

		private List<RawSapLoad> GetAreaUniformToFrameLoads_DirectAPI(string patternFilter)
		{
			var list = new List<RawSapLoad>();
			int count = 0;
			string[] names = null;
			_model.AreaObj.GetNameList(ref count, ref names);
			if (count == 0 || names == null) return list;

			foreach (var name in names)
			{
				int num = 0;
				string[] areaArr = null;
				string[] patArr = null;
				string[] csysArr = null;
				int[] dirArr = null;
				double[] valArr = null;
				int[] distTypeArr = null;

				int ret = _model.AreaObj.GetLoadUniformToFrame(name, ref num, ref areaArr, ref patArr, ref csysArr, ref dirArr, ref valArr, ref distTypeArr, eItemType.Objects);
				if (ret == 0 && num > 0 && patArr != null)
				{
					for (int i = 0; i < num; i++)
					{
						if (!string.IsNullOrEmpty(patternFilter) && !patArr[i].Equals(patternFilter, StringComparison.OrdinalIgnoreCase)) continue;

						double magnitude = SapUtils.ConvertLoadToKnPerM2(valArr[i]);
								int dirCode = (dirArr != null && i < dirArr.Length) ? dirArr[i] : 10;
								string dirStr = GetDirectionDisplayName(dirCode, name); // FIX: Pass element name
						string csys = (csysArr != null && i < csysArr.Length) ? csysArr[i] : "Global";
						string distStr = (distTypeArr != null && i < distTypeArr.Length && distTypeArr[i] == 1) ? "One Way" : "Two Way";

								var vec = CalculateForceVector(name, magnitude, dirCode, csys);
						double z = GetElementElevation(name, "Area");

						var raw = new RawSapLoad
						{
							ElementName = name,
							LoadPattern = patArr[i],
							Value1 = magnitude,
							LoadType = "AreaUniformToFrame",
									Direction = dirStr,
									DirectionCode = dirCode,
							CoordSys = csys,
							DistributionType = distStr,
							ElementZ = z
						};
						raw.SetForceVector(vec);
						list.Add(raw);
					}
				}
			}
			return list;
		}

		private List<RawSapLoad> GetJointLoads_DirectAPI(string patternFilter)
		{
			var list = new List<RawSapLoad>();
			int count = 0;
			string[] names = null;
			_model.PointObj.GetNameList(ref count, ref names);
			if (count == 0 || names == null) return list;

			foreach (var name in names)
			{
				int num = 0;
				string[] pointArr = null;
				string[] patArr = null;
				int[] lcStepArr = null;
				string[] csysArr = null;
				double[] f1Arr = null;
				double[] f2Arr = null;
				double[] f3Arr = null;
				double[] m1Arr = null;
				double[] m2Arr = null;
				double[] m3Arr = null;

				int ret = _model.PointObj.GetLoadForce(name, ref num, ref pointArr, ref patArr, ref lcStepArr, ref csysArr, ref f1Arr, ref f2Arr, ref f3Arr, ref m1Arr, ref m2Arr, ref m3Arr, eItemType.Objects);
				if (ret == 0 && num > 0 && patArr != null)
				{
					for (int i = 0; i < num; i++)
					{
						if (!string.IsNullOrEmpty(patternFilter) && !patArr[i].Equals(patternFilter, StringComparison.OrdinalIgnoreCase)) continue;

						double f1 = (f1Arr != null && i < f1Arr.Length) ? f1Arr[i] : 0.0;
						double f2 = (f2Arr != null && i < f2Arr.Length) ? f2Arr[i] : 0.0;
						double f3 = (f3Arr != null && i < f3Arr.Length) ? f3Arr[i] : 0.0;
						string csys = (csysArr != null && i < csysArr.Length) ? csysArr[i] : "Global";

						double z = GetElementElevation(name, "Point");

						void AddF(double v, string dir)
						{
							if (Math.Abs(v) < 1e-6) return;
							double mag = SapUtils.ConvertForceToKn(v);
							int dirCode = NormalizeDirectionCode(dir);
							var vec = CalculateForceVector(name, mag, dirCode, csys);
							string dirDisplay = GetDirectionDisplayName(dirCode);

							var raw = new RawSapLoad
							{
								ElementName = name,
								LoadPattern = patArr[i],
								Value1 = mag,
								LoadType = "PointForce",
								Direction = dirDisplay,
								DirectionCode = dirCode,
								CoordSys = csys,
								ElementZ = z
							};
							raw.SetForceVector(vec);
							list.Add(raw);
						}

						// F1 = X or Local-1, F2 = Y or Local-2, F3 = Z or Local-3
						AddF(f1, csys.Contains("Local") ? "1" : "X");
						AddF(f2, csys.Contains("Local") ? "2" : "Y");
						AddF(f3, csys.Contains("Local") ? "3" : "Z");
					}
				}
			}
			return list;
		}

		#endregion
	}
}
