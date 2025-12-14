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
		// [DELETED] private readonly Dictionary<string, TableSchema> _schemaCache; - Removed with Table Mode
		private readonly ModelInventory _inventory;

		private bool _useFallbackApi = false;
		private bool _preferApi = true;

		public SapDatabaseReader(cSapModel model, ModelInventory inventory = null)
		{
			_model = model ?? throw new ArgumentNullException(nameof(model));
			// [DELETED] _schemaCache initialization - Removed with Table Mode
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
			// REFACTORED: Direct API Only. No Table fallback.
			// All loads are read via the _DirectAPI methods which call SAP OAPI directly.
			return ReadAllLoads_ViaDirectAPI(patternFilter);
		}

			// [DELETED] #region Schema Detection
		// TableSchema class and GetTableSchema method removed.
		// All load reading now uses Direct API via Get...DirectAPI methods.



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
        /// REFACTORED: Uses ModelInventory cache for Local Axes (performance optimization).
        /// Falls back to SapUtils API only if cache miss.
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

            // LOCAL AXES (1, 2, 3) - Use Inventory Cache first
            if (directionCode >= 1 && directionCode <= 3)
            {
                // PRIORITY 1: Use cached vectors from ModelInventory (fast)
                if (_inventory != null)
                {
                    var cachedAxis = _inventory.GetLocalAxis(elementName, directionCode);
                    if (cachedAxis.HasValue)
                    {
                        return cachedAxis.Value * signedVal;
                    }
                }

                // PRIORITY 2: Fallback to API call (slow, but necessary if cache miss)
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

		// [DELETED] #region TABLE MODE: Readers
		// Legacy Table-based methods removed. Using Direct API methods below.


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

                        // [FILTER] Ignore zero loads
                        if (Math.Abs(magnitude) < 1e-9) continue;

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
