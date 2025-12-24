using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;

namespace DTS_Engine.Core.Algorithms
{
    /// <summary>
    /// TopologyBuilder V5: Star Topology với XData làm Single Source of Truth.
    /// 
    /// Thay thế hoàn toàn NOD-based BeamGroup persistence.
    /// Sử dụng hệ thống DTS_LINK có sẵn để quản lý liên kết.
    /// 
    /// Quy tắc:
    /// 1. Physical X-coordinate (L->R) là Truth duy nhất
    /// 2. Phần tử bên trái nhất (S1) là "Mother" - các phần tử khác link tới S1
    /// 3. Indices (S1, S2...) được rebuild mỗi lần chạy, không lưu trữ
    /// 4. XData trên entity là database, không dùng NOD
    /// 
    /// ISO 25010: Maintainability, Modularity
    /// </summary>
    public class TopologyBuilder
    {
        #region Constants

        /// <summary>Tolerance cho việc so sánh tọa độ Z (mm)</summary>
        private const double Z_TOLERANCE = 100.0;

        /// <summary>Tolerance cho việc so sánh tọa độ X/Y khi kiểm tra kết nối (mm)</summary>
        private const double CONNECTION_TOLERANCE = 50.0;

        #endregion

        #region Build Graph - Core Method

        /// <summary>
        /// Xây dựng topology graph từ selection.
        /// 
        /// Step A: Expand - Tìm Mother (OriginHandle) và Children (ReferenceHandles)
        /// Step B: Sort - Sắp xếp theo X tăng dần (Left-to-Right)
        /// Step C: Star Link - Thiết lập Star Topology (tất cả link về S1)
        /// </summary>
        /// <param name="selectedIds">Danh sách ObjectId được chọn</param>
        /// <param name="tr">Transaction hiện tại</param>
        /// <param name="autoEstablishLinks">Tự động thiết lập Star Topology nếu chưa có</param>
        /// <returns>Danh sách các BeamTopology đã sắp xếp L->R</returns>
        public List<BeamTopology> BuildGraph(
            ICollection<ObjectId> selectedIds,
            Transaction tr,
            bool autoEstablishLinks = true)
        {
            if (selectedIds == null || selectedIds.Count == 0)
                return new List<BeamTopology>();

            // Step A: Expand selection để bao gồm Mother và Children
            var expandedSet = ExpandSelection(selectedIds, tr);

            // Validate: Chỉ xử lý các entity có XData hợp lệ
            var validTopologies = new List<BeamTopology>();
            foreach (var id in expandedSet)
            {
                if (id.IsErased) continue;

                var topology = ExtractTopology(id, tr);
                if (topology != null)
                {
                    validTopologies.Add(topology);
                }
            }

            if (validTopologies.Count == 0)
                return new List<BeamTopology>();

            // Step B: Sort theo X tăng dần (Left-to-Right)
            // Primary: Min(Start.X, End.X)
            // Secondary: Y coordinate (cho các dầm song song)
            var sortedList = validTopologies
                .OrderBy(t => Math.Min(t.StartPoint.X, t.EndPoint.X))
                .ThenBy(t => Math.Min(t.StartPoint.Y, t.EndPoint.Y))
                .ToList();

            // Gán SpanIndex sau khi sort
            for (int i = 0; i < sortedList.Count; i++)
            {
                sortedList[i].SpanIndex = i;
                sortedList[i].SpanId = $"S{i + 1}";
            }

            // Step C: Thiết lập Star Topology (optional)
            if (autoEstablishLinks && sortedList.Count > 1)
            {
                EstablishStarTopology(sortedList, tr);
            }

            return sortedList;
        }

        #endregion

        #region Step A: Expand Selection

        /// <summary>
        /// Mở rộng selection để bao gồm cả Mother và Children.
        /// </summary>
        private HashSet<ObjectId> ExpandSelection(ICollection<ObjectId> selectedIds, Transaction tr)
        {
            var expandedSet = new HashSet<ObjectId>(selectedIds);
            var toProcess = new Queue<ObjectId>(selectedIds);

            while (toProcess.Count > 0)
            {
                var currentId = toProcess.Dequeue();
                if (currentId.IsErased) continue;

                try
                {
                    var obj = tr.GetObject(currentId, OpenMode.ForRead);
                    var elemData = XDataUtils.ReadElementData(obj);

                    if (elemData != null)
                    {
                        // V7.0: Tìm Mother (MotherHandle) - không dùng OriginHandle (Origin Point tầng)
                        if (!string.IsNullOrEmpty(elemData.MotherHandle))
                        {
                            var motherId = AcadUtils.GetObjectIdFromHandle(elemData.MotherHandle);
                            if (motherId != ObjectId.Null && !motherId.IsErased && !expandedSet.Contains(motherId))
                            {
                                expandedSet.Add(motherId);
                                toProcess.Enqueue(motherId);
                            }
                        }

                        // Tìm Children (từ ChildHandles của entity này nếu nó là Mother)
                        if (elemData.ChildHandles != null)
                        {
                            foreach (var childHandle in elemData.ChildHandles)
                            {
                                var childId = AcadUtils.GetObjectIdFromHandle(childHandle);
                                if (childId != ObjectId.Null && !childId.IsErased && !expandedSet.Contains(childId))
                                {
                                    expandedSet.Add(childId);
                                    toProcess.Enqueue(childId);
                                }
                            }
                        }

                        // Tìm References (nếu có)
                        if (elemData.ReferenceHandles != null)
                        {
                            foreach (var refHandle in elemData.ReferenceHandles)
                            {
                                var refId = AcadUtils.GetObjectIdFromHandle(refHandle);
                                if (refId != ObjectId.Null && !refId.IsErased && !expandedSet.Contains(refId))
                                {
                                    expandedSet.Add(refId);
                                    toProcess.Enqueue(refId);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Skip invalid entities
                }
            }

            return expandedSet;
        }

        #endregion

        #region Step B: Extract Topology

        /// <summary>
        /// Trích xuất thông tin topology từ một entity.
        /// </summary>
        private BeamTopology ExtractTopology(ObjectId id, Transaction tr)
        {
            try
            {
                var obj = tr.GetObject(id, OpenMode.ForRead);
                var curve = obj as Curve;
                if (curve == null) return null;

                // Đọc XData
                var elemData = XDataUtils.ReadElementData(obj);
                var rebarData = XDataUtils.ReadRebarData(obj);

                // Kiểm tra có phải beam không
                string xType = elemData?.XType?.ToUpperInvariant();
                bool isBeam = xType == "BEAM" || xType == "REBAR_DATA" || rebarData != null;
                if (!isBeam && elemData == null) return null;

                var startPt = curve.StartPoint;
                var endPt = curve.EndPoint;

                // Xác định hướng geometry (L->R hay R->L)
                bool isGeometryReversed = startPt.X > endPt.X;

                // FIX: Prioritize XData's BaseZ over geometric Z (2D drawings have Z=0 in geometry)
                double levelZ = 0;
                if (elemData is BeamData beamDataZ && beamDataZ.BaseZ.HasValue && beamDataZ.BaseZ.Value != 0)
                {
                    levelZ = beamDataZ.BaseZ.Value;
                }
                else
                {
                    // Fallback to geometric Z
                    levelZ = Math.Round((startPt.Z + endPt.Z) / 2.0 / Z_TOLERANCE) * Z_TOLERANCE;
                }

                // Tính toán các thông số
                var topology = new BeamTopology
                {
                    ObjectId = id,
                    Handle = obj.Handle.ToString(),
                    StartPoint = startPt,
                    EndPoint = endPt,
                    MidPoint = startPt + (endPt - startPt) * 0.5,
                    Length = curve.GetDistanceAtParameter(curve.EndParam),
                    IsGeometryReversed = isGeometryReversed,
                    LevelZ = levelZ,

                    // Thông tin từ XData
                    ElementData = elemData,
                    RebarData = rebarData,
                    OriginHandle = elemData?.OriginHandle,
                    MotherHandle = elemData?.MotherHandle, // V7.0: Group mother handle
                    SapElementName = rebarData?.SapElementName ?? (elemData as BeamData)?.SapFrameName
                };

                // CRITICAL FIX: Read GroupId from XData for Fix P1 GroupId fallback
                var (groupId, _) = XDataUtils.ReadGroupIdentity(obj);
                if (!string.IsNullOrEmpty(groupId))
                {
                    topology.GroupId = groupId;
                }

                // Lấy section dimensions
                if (rebarData != null)
                {
                    topology.Width = rebarData.Width > 0 ? rebarData.Width * 10 : 0; // cm -> mm
                    topology.Height = rebarData.SectionHeight > 0 ? rebarData.SectionHeight * 10 : 0;
                }
                else if (elemData is BeamData beamData)
                {
                    topology.Width = beamData.Width ?? 0;
                    topology.Height = beamData.Height ?? beamData.Depth ?? 0;
                }

                return topology;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Step C: Establish Star Topology

        /// <summary>
        /// [V5.0] Thiết lập Star Topology: S1 (Left-most) là Mother, các entity khác link về S1.
        /// Now also writes GroupIdentity and GroupState to ALL entities for self-sufficiency.
        /// FIX 1.3R: Always re-establish links to repair stale data.
        /// </summary>
        private void EstablishStarTopology(List<BeamTopology> sortedList, Transaction tr)
        {
            if (sortedList.Count < 1) return;

            var motherTopology = sortedList[0]; // S1 = Left-most = Mother
            var motherObj = tr.GetObject(motherTopology.ObjectId, OpenMode.ForWrite);

            // FIX 1.3R: Reuse existing GroupId if available, otherwise generate new
            string groupId;
            var (existingGroupId, _) = XDataUtils.ReadGroupIdentity(motherObj);
            if (!string.IsNullOrEmpty(existingGroupId))
            {
                groupId = existingGroupId;  // Reuse existing for consistency
            }
            else
            {
                groupId = Guid.NewGuid().ToString();  // Generate new
            }

            // V5.0: Write GroupIdentity/GroupState to ALL entities
            for (int i = 0; i < sortedList.Count; i++)
            {
                var topology = sortedList[i];
                var obj = tr.GetObject(topology.ObjectId, OpenMode.ForWrite);

                // Write GroupIdentity: GroupId + SpanIndex (ALWAYS update to ensure correct index)
                XDataUtils.WriteGroupIdentity(obj, groupId, i, tr);

                // V6.0: Write IsLocked flag (initial state = unlocked)
                XDataUtils.WriteIsLocked(obj, isLocked: false, tr);

                // FIX 1.3R + Stale Mother Fix: ALWAYS establish links
                if (i > 0)
                {
                    // Child: Link to Mother
                    // V7.0: Write MotherHandle (Group) + keep OriginHandle (Origin Point tầng)
                    var result = XDataUtils.RegisterLink(obj, motherObj, isReference: false, tr);
                    topology.MotherHandle = motherTopology.Handle;  // V7.0: Update Group Mother
                }
                else
                {
                    // Mother (i=0): Must point to ITSELF as Mother
                    // V7.0: Mother self-references via MotherHandle
                    var result = XDataUtils.RegisterLink(obj, obj, isReference: false, tr);
                    topology.MotherHandle = topology.Handle; // V7.0: Self-reference
                }
            }

            // Store GroupId in topology for later use
            if (sortedList.Count > 0)
            {
                sortedList[0].GroupId = groupId;
            }
        }

        #endregion

        #region Build BeamGroup từ Topology

        /// <summary>
        /// Chuyển đổi danh sách BeamTopology thành BeamGroup (Runtime-only).
        /// BeamGroup không lưu vào NOD, chỉ dùng trong session hiện tại.
        /// </summary>
        /// <param name="topologies">Danh sách topology đã sort L->R</param>
        /// <param name="settings">DTS Settings</param>
        /// <returns>BeamGroup runtime</returns>
        public BeamGroup BuildBeamGroup(List<BeamTopology> topologies, DtsSettings settings)
        {
            if (topologies == null || topologies.Count == 0)
                return null;

            var first = topologies[0];
            var last = topologies[topologies.Count - 1];

            // Xác định hướng dầm
            double dx = Math.Abs(last.EndPoint.X - first.StartPoint.X);
            double dy = Math.Abs(last.EndPoint.Y - first.StartPoint.Y);
            string direction = dx >= dy ? "X" : "Y";

            // Xác định loại (Girder hay Beam) - ưu tiên từ XData, fallback to width
            double avgWidth = topologies.Average(t => t.Width);
            double girderThreshold = settings?.Naming?.GirderMinWidth ?? 300;

            // Read SectionLabel (NamingEngine) and GroupName (display) from mother beam XData
            var motherBeamData = first.ElementData as BeamData;
            string sectionLabel = motherBeamData?.SectionLabel;
            string groupTypeFromXData = motherBeamData?.GroupType;
            string axisName = motherBeamData?.AxisName;
            // PRIORITY: Read GroupName (display name) from XData first
            string groupDisplayName = motherBeamData?.GroupName;

            // === NOD FALLBACK: Try registry if XData is missing ===
            if (string.IsNullOrEmpty(sectionLabel))
            {
                try
                {
                    using (var tr = Utils.AcadUtils.Db.TransactionManager.StartTransaction())
                    {
                        var regInfo = Engines.RegistryEngine.LookupBeamGroup(first.Handle, tr);
                        if (regInfo != null)
                        {
                            sectionLabel = regInfo.Name;
                            groupDisplayName = regInfo.GroupName;
                            if (string.IsNullOrEmpty(groupTypeFromXData))
                                groupTypeFromXData = regInfo.GroupType;
                            if (string.IsNullOrEmpty(axisName))
                                axisName = regInfo.AxisName;
                        }
                        tr.Commit();
                    }
                }
                catch { /* Silent fallback - continue without NOD data */ }
            }

            // Fallback to width-based classification if not in XData or NOD
            string groupType = !string.IsNullOrEmpty(groupTypeFromXData)
                ? groupTypeFromXData
                : (avgWidth >= girderThreshold ? "Girder" : "Beam");

            // FIX: Check if beams have GroupIdentity (were grouped via Group command)
            // If no GroupIdentity, this is an ungrouped single beam - use Handle as name
            bool hasGroupIdentity = !string.IsNullOrEmpty(first.GroupId);

            string displayName;
            if (!string.IsNullOrEmpty(groupDisplayName))
            {
                // Use existing GroupName from XData/NOD
                displayName = groupDisplayName;
            }
            else if (hasGroupIdentity)
            {
                // Has GroupIdentity but no GroupName - use axis-based naming
                displayName = $"{groupType} [{axisName ?? first.Handle}] @Z={first.LevelZ:F0}";
            }
            else
            {
                // No GroupIdentity = ungrouped single beam - use Handle
                displayName = $"Frame [{first.Handle}]";
            }

            var group = new BeamGroup
            {
                // FIX: Use display name logic above
                GroupName = displayName,
                Name = sectionLabel, // Label from NamingEngine (like "1GHY5") for rebar grouping
                GroupType = groupType,
                Direction = direction,
                AxisName = axisName, // Also store axis name for reference
                Width = avgWidth,
                Height = topologies.Average(t => t.Height),
                TotalLength = topologies.Sum(t => t.Length) / 1000.0, // mm -> m
                LevelZ = first.LevelZ,
                Source = "Topology",
                EntityHandles = topologies.Select(t => t.Handle).ToList(),
                IsSingleBeam = topologies.Count == 1
            };

            // Tạo Supports
            group.Supports = BuildSupports(topologies, settings);

            // Tạo Spans
            group.Spans = BuildSpans(topologies, group.Supports, settings);

            // Check splice requirement
            double standardLength = settings?.Beam?.StandardBarLength ?? 11700;
            group.RequiresSplice = group.TotalLength * 1000 > standardLength;

            return group;
        }

        /// <summary>
        /// Xây dựng danh sách gối đỡ từ topology.
        /// </summary>
        private List<SupportData> BuildSupports(List<BeamTopology> topologies, DtsSettings settings)
        {
            var supports = new List<SupportData>();
            double cumPosition = 0;

            for (int i = 0; i < topologies.Count; i++)
            {
                var topo = topologies[i];

                // Support tại đầu beam (chỉ cho beam đầu tiên hoặc khi có SupportI)
                if (i == 0)
                {
                    bool hasStartSupport = topo.RebarData?.SupportI == 1;
                    supports.Add(new SupportData
                    {
                        SupportId = $"C{supports.Count + 1}",
                        SupportIndex = supports.Count,
                        Type = hasStartSupport ? SupportType.Column : SupportType.FreeEnd,
                        Position = cumPosition,
                        Width = hasStartSupport ? 400 : 0
                    });
                }

                cumPosition += topo.Length / 1000.0; // mm -> m

                // Support tại cuối beam
                bool hasEndSupport = topo.RebarData?.SupportJ == 1;
                if (i == topologies.Count - 1)
                {
                    // Last beam
                    supports.Add(new SupportData
                    {
                        SupportId = $"C{supports.Count + 1}",
                        SupportIndex = supports.Count,
                        Type = hasEndSupport ? SupportType.Column : SupportType.FreeEnd,
                        Position = cumPosition,
                        Width = hasEndSupport ? 400 : 0
                    });
                }
                else
                {
                    // Internal node (End of current beam, Start of next)
                    // ALWAYS add a support here to split spans, satisfying user requirement
                    // to display/calculate each segment separately.
                    // If hasEndSupport=false, we create a "Virtual Joint" (Width=0).

                    supports.Add(new SupportData
                    {
                        SupportId = $"C{supports.Count + 1}",
                        SupportIndex = supports.Count,
                        Type = hasEndSupport ? SupportType.Column : SupportType.Column, // Treat as point support
                        Position = cumPosition,
                        Width = hasEndSupport ? 400 : 0 // Virtual joint has 0 width
                    });
                }
            }

            return supports;
        }

        /// <summary>
        /// Xây dựng danh sách nhịp từ topology và supports.
        /// </summary>
        private List<SpanData> BuildSpans(List<BeamTopology> topologies, List<SupportData> supports, DtsSettings settings)
        {
            var spans = new List<SpanData>();

            if (supports.Count < 2)
            {
                // Single span covering all beams
                var span = CreateSpanFromTopologies(topologies, 0, settings);
                span.SpanId = "S1";
                span.SpanIndex = 0;
                span.LeftSupportId = supports.FirstOrDefault()?.SupportId ?? "FE_Start";
                span.RightSupportId = supports.LastOrDefault()?.SupportId ?? "FE_End";
                spans.Add(span);
            }
            else
            {
                // Multiple spans between supports
                double cumPos = 0;
                int currentBeamIdx = 0;

                for (int i = 0; i < supports.Count - 1; i++)
                {
                    var leftSupport = supports[i];
                    var rightSupport = supports[i + 1];
                    double spanLength = rightSupport.Position - leftSupport.Position;

                    // Find beams that fall within this span
                    var spanBeams = new List<BeamTopology>();
                    double checkPos = cumPos;

                    while (currentBeamIdx < topologies.Count)
                    {
                        var beam = topologies[currentBeamIdx];
                        double beamEnd = checkPos + beam.Length / 1000.0;

                        if (beamEnd <= rightSupport.Position + 0.01)
                        {
                            spanBeams.Add(beam);
                            checkPos = beamEnd;
                            currentBeamIdx++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (spanBeams.Count == 0 && currentBeamIdx < topologies.Count)
                    {
                        spanBeams.Add(topologies[currentBeamIdx]);
                    }

                    var span = CreateSpanFromTopologies(spanBeams, i, settings);
                    span.SpanId = $"S{i + 1}";
                    span.SpanIndex = i;
                    span.Length = spanLength;
                    span.LeftSupportId = leftSupport.SupportId;
                    span.RightSupportId = rightSupport.SupportId;
                    span.IsConsole = leftSupport.Type == SupportType.FreeEnd || rightSupport.Type == SupportType.FreeEnd;
                    spans.Add(span);

                    cumPos = rightSupport.Position;
                }
            }

            return spans;
        }

        /// <summary>
        /// Tạo SpanData từ danh sách topology.
        /// </summary>
        private SpanData CreateSpanFromTopologies(List<BeamTopology> topologies, int spanIndex, DtsSettings settings)
        {
            if (topologies == null || topologies.Count == 0)
                return new SpanData();

            var span = new SpanData
            {
                Width = topologies.Average(t => t.Width),
                Height = topologies.Average(t => t.Height),
                Length = topologies.Sum(t => t.Length) / 1000.0,
                ClearLength = topologies.Sum(t => t.Length) / 1000.0 - 0.3, // Trừ 30cm cho gối
                IsActive = true
            };

            // Tạo segments
            span.Segments = new List<PhysicalSegment>();
            foreach (var topo in topologies)
            {
                span.Segments.Add(new PhysicalSegment
                {
                    EntityHandle = topo.Handle,
                    SapFrameName = topo.SapElementName,
                    Length = topo.Length / 1000.0,
                    StartPoint = new double[] { topo.StartPoint.X, topo.StartPoint.Y },
                    EndPoint = new double[] { topo.EndPoint.X, topo.EndPoint.Y }
                });
            }

            // Populate As_Top/As_Bot từ XData của beam đầu tiên
            var firstTopo = topologies[0];
            if (firstTopo.RebarData != null)
            {
                PopulateSpanRequirements(span, firstTopo, settings);
            }

            return span;
        }

        /// <summary>
        /// Populate As_Top/As_Bot từ BeamResultData vào SpanData.
        /// Xử lý trường hợp geometry bị đảo ngược (R->L).
        /// </summary>
        private void PopulateSpanRequirements(SpanData span, BeamTopology topo, DtsSettings settings)
        {
            var data = topo.RebarData;
            if (data == null) return;

            double torsTop = settings?.Beam?.TorsionDist_TopBar ?? 0.25;
            double torsBot = settings?.Beam?.TorsionDist_BotBar ?? 0.25;
            double torsSide = settings?.Beam?.TorsionDist_SideBar ?? 0.50;

            // Ensure arrays exist
            if (span.As_Top == null || span.As_Top.Length < 6) span.As_Top = new double[6];
            if (span.As_Bot == null || span.As_Bot.Length < 6) span.As_Bot = new double[6];
            if (span.StirrupReq == null || span.StirrupReq.Length < 3) span.StirrupReq = new double[3];
            if (span.WebReq == null || span.WebReq.Length < 3) span.WebReq = new double[3];

            // Source arrays
            var topArea = data.TopArea ?? new double[3];
            var botArea = data.BotArea ?? new double[3];
            var torsionArea = data.TorsionArea ?? new double[3];
            var shearArea = data.ShearArea ?? new double[3];

            // FIX 1.4: REMOVED flip logic - TopologyBuilder sorts beams L→R
            // SAP data is read in geometric order, which is now canonical L→R
            // Old code:
            // if (topo.IsGeometryReversed)
            // {
            //     topArea = FlipArray(topArea);
            //     botArea = FlipArray(botArea);
            //     torsionArea = FlipArray(torsionArea);
            //     shearArea = FlipArray(shearArea);
            // }

            // Map 3 zones -> 6 positions
            for (int zi = 0; zi < 3; zi++)
            {
                double asTopReq = (topArea.ElementAtOrDefault(zi)) + (torsionArea.ElementAtOrDefault(zi)) * torsTop;
                double asBotReq = (botArea.ElementAtOrDefault(zi)) + (torsionArea.ElementAtOrDefault(zi)) * torsBot;

                int p0 = zi == 0 ? 0 : (zi == 1 ? 2 : 4);
                int p1 = p0 + 1;

                span.As_Top[p0] = asTopReq;
                span.As_Top[p1] = asTopReq;
                span.As_Bot[p0] = asBotReq;
                span.As_Bot[p1] = asBotReq;

                span.StirrupReq[zi] = shearArea.ElementAtOrDefault(zi);
                span.WebReq[zi] = torsionArea.ElementAtOrDefault(zi) * torsSide;
            }
        }

        /// <summary>
        /// Flip array cho trường hợp geometry bị đảo ngược.
        /// [0, 1, 2] -> [2, 1, 0]
        /// </summary>
        private double[] FlipArray(double[] arr)
        {
            if (arr == null || arr.Length == 0) return arr;
            var flipped = new double[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                flipped[i] = arr[arr.Length - 1 - i];
            }
            return flipped;
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Chia danh sách topology thành các nhóm riêng biệt dựa trên topology.
        /// Mỗi nhóm là một dải dầm liên tục (connected via links).
        /// </summary>
        public List<List<BeamTopology>> SplitIntoGroups(List<BeamTopology> allTopologies)
        {
            if (allTopologies == null || allTopologies.Count == 0)
                return new List<List<BeamTopology>>();

            var result = new List<List<BeamTopology>>();
            var processed = new HashSet<string>();

            foreach (var topo in allTopologies)
            {
                if (processed.Contains(topo.Handle))
                    continue;

                // Tìm tất cả topology connected với topo này
                var group = new List<BeamTopology>();
                var queue = new Queue<BeamTopology>();
                queue.Enqueue(topo);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (processed.Contains(current.Handle))
                        continue;

                    processed.Add(current.Handle);
                    group.Add(current);

                    // Tìm connected topologies
                    foreach (var other in allTopologies)
                    {
                        if (processed.Contains(other.Handle))
                            continue;

                        // FIX P1: Check nếu connected via Link OR GroupId
                        // V7.0: GroupId fallback ensures correct grouping even when MotherHandle is stale
                        bool sameGroup = !string.IsNullOrEmpty(current.GroupId) &&
                                        current.GroupId == other.GroupId;
                        // V7.0: Check MotherHandle thay vì OriginHandle cho Group logic
                        bool isLinked = sameGroup ||
                                       current.MotherHandle == other.Handle ||
                                       other.MotherHandle == current.Handle;

                        if (isLinked)
                        {
                            queue.Enqueue(other);
                        }
                    }
                }

                if (group.Count > 0)
                {
                    // Sort L->R
                    group = group.OrderBy(t => Math.Min(t.StartPoint.X, t.EndPoint.X)).ToList();
                    result.Add(group);
                }
            }

            return result;
        }

        #endregion

        #region Downstream Tracking

        /// <summary>
        /// Tìm tất cả downstream beams từ một beam cụ thể.
        /// Downstream = các beam có X position lớn hơn và thuộc cùng group.
        /// </summary>
        /// <param name="beamHandle">Handle của beam hiện tại</param>
        /// <param name="tr">Transaction</param>
        /// <returns>Danh sách downstream BeamTopology sorted L->R</returns>
        public List<BeamTopology> GetDownstreamBeams(string beamHandle, Transaction tr)
        {
            if (string.IsNullOrEmpty(beamHandle)) return new List<BeamTopology>();

            var beamId = AcadUtils.GetObjectIdFromHandle(beamHandle);
            if (beamId == ObjectId.Null) return new List<BeamTopology>();

            // Get current beam's group
            var allIds = new List<ObjectId> { beamId };
            var graph = BuildGraph(allIds, tr, autoEstablishLinks: false);

            if (graph.Count == 0) return new List<BeamTopology>();

            // Find current beam in sorted graph
            var currentTopo = graph.FirstOrDefault(t => t.Handle == beamHandle);
            if (currentTopo == null) return new List<BeamTopology>();

            // Downstream = all beams after current in sorted order
            return graph.Where(t => t.SpanIndex > currentTopo.SpanIndex).ToList();
        }

        /// <summary>
        /// Tìm tất cả upstream beams từ một beam cụ thể.
        /// Upstream = các beam có X position nhỏ hơn và thuộc cùng group.
        /// </summary>
        public List<BeamTopology> GetUpstreamBeams(string beamHandle, Transaction tr)
        {
            if (string.IsNullOrEmpty(beamHandle)) return new List<BeamTopology>();

            var beamId = AcadUtils.GetObjectIdFromHandle(beamHandle);
            if (beamId == ObjectId.Null) return new List<BeamTopology>();

            var allIds = new List<ObjectId> { beamId };
            var graph = BuildGraph(allIds, tr, autoEstablishLinks: false);

            if (graph.Count == 0) return new List<BeamTopology>();

            var currentTopo = graph.FirstOrDefault(t => t.Handle == beamHandle);
            if (currentTopo == null) return new List<BeamTopology>();

            return graph.Where(t => t.SpanIndex < currentTopo.SpanIndex).ToList();
        }

        /// <summary>
        /// Re-link downstream beams to a new mother after unlink.
        /// </summary>
        /// <param name="newMotherHandle">Handle của mother mới</param>
        /// <param name="downstreamHandles">Danh sách handles của downstream beams</param>
        /// <param name="tr">Transaction</param>
        /// <returns>Số lượng beams đã re-link thành công</returns>
        public int RelinkDownstreamToNewMother(string newMotherHandle, List<string> downstreamHandles, Transaction tr)
        {
            if (string.IsNullOrEmpty(newMotherHandle) || downstreamHandles == null || downstreamHandles.Count == 0)
                return 0;

            var newMotherId = AcadUtils.GetObjectIdFromHandle(newMotherHandle);
            if (newMotherId == ObjectId.Null) return 0;

            var newMotherObj = tr.GetObject(newMotherId, OpenMode.ForWrite);
            if (newMotherObj == null) return 0;

            int count = 0;
            foreach (var handle in downstreamHandles)
            {
                var childId = AcadUtils.GetObjectIdFromHandle(handle);
                if (childId == ObjectId.Null) continue;

                try
                {
                    var childObj = tr.GetObject(childId, OpenMode.ForWrite);
                    var result = XDataUtils.RegisterLink(childObj, newMotherObj, isReference: false, tr);

                    if (result == LinkRegistrationResult.Primary || result == LinkRegistrationResult.AlreadyLinked)
                    {
                        count++;
                    }
                }
                catch
                {
                    // Skip failed links
                }
            }

            return count;
        }

        /// <summary>
        /// Validate và sửa chữa Star Topology cho một group.
        /// Đảm bảo S1 (left-most) là Mother duy nhất.
        /// </summary>
        public bool ValidateAndRepairStarTopology(List<BeamTopology> sortedGroup, Transaction tr)
        {
            if (sortedGroup == null || sortedGroup.Count < 2) return true;

            bool needsRepair = false;
            var expectedMother = sortedGroup[0];

            // Check if all children point to S1 as mother
            for (int i = 1; i < sortedGroup.Count; i++)
            {
                var child = sortedGroup[i];
                // V7.0: Check MotherHandle thay vì OriginHandle
                if (child.MotherHandle != expectedMother.Handle)
                {
                    needsRepair = true;
                    break;
                }
            }

            if (needsRepair)
            {
                // Repair: Re-establish Star Topology
                EstablishStarTopology(sortedGroup, tr);
                return false; // Indicates repair was performed
            }

            return true; // Already valid
        }

        #endregion

        #region DtsSettings Integration

        /// <summary>
        /// Build BeamGroup với settings từ DtsSettings singleton.
        /// </summary>
        public BeamGroup BuildBeamGroupWithSettings(List<BeamTopology> topologies)
        {
            return BuildBeamGroup(topologies, DtsSettings.Instance);
        }

        #endregion

        #region V5.0: Geometric Sort for Regroup/Resurrect

        /// <summary>
        /// [V5.0] Sort beams geometrically for correct SpanIndex assignment.
        /// Critical for Regroup/Resurrect operations where selection order is random.
        /// 
        /// ALGORITHM:
        /// 1. Get center point of each beam
        /// 2. Determine dominant axis (X or Y)
        /// 3. Sort by dominant axis
        /// </summary>
        /// <param name="unsortedIds">List of ObjectIds in random order</param>
        /// <param name="tr">Transaction</param>
        /// <returns>Sorted list with correct geometric order</returns>
        public List<ObjectId> SortBeamsGeometrically(List<ObjectId> unsortedIds, Transaction tr)
        {
            if (unsortedIds == null || unsortedIds.Count <= 1)
                return unsortedIds ?? new List<ObjectId>();

            // Extract center points
            var beamCenters = new List<(ObjectId Id, Point3d Center)>();

            foreach (var id in unsortedIds)
            {
                if (id.IsErased) continue;

                try
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    Point3d center;
                    if (ent is Line line)
                    {
                        center = new Point3d(
                            (line.StartPoint.X + line.EndPoint.X) / 2,
                            (line.StartPoint.Y + line.EndPoint.Y) / 2,
                            (line.StartPoint.Z + line.EndPoint.Z) / 2);
                    }
                    else if (ent is Polyline pline && pline.NumberOfVertices >= 2)
                    {
                        var first = pline.GetPoint3dAt(0);
                        var last = pline.GetPoint3dAt(pline.NumberOfVertices - 1);
                        center = new Point3d(
                            (first.X + last.X) / 2,
                            (first.Y + last.Y) / 2,
                            (first.Z + last.Z) / 2);
                    }
                    else
                    {
                        // Fallback: use Geometric Extents center
                        var ext = ent.GeometricExtents;
                        center = new Point3d(
                            (ext.MinPoint.X + ext.MaxPoint.X) / 2,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) / 2,
                            (ext.MinPoint.Z + ext.MaxPoint.Z) / 2);
                    }

                    beamCenters.Add((id, center));
                }
                catch { }
            }

            if (beamCenters.Count == 0)
                return new List<ObjectId>();

            // Determine dominant axis
            double minX = beamCenters.Min(b => b.Center.X);
            double maxX = beamCenters.Max(b => b.Center.X);
            double minY = beamCenters.Min(b => b.Center.Y);
            double maxY = beamCenters.Max(b => b.Center.Y);

            double deltaX = maxX - minX;
            double deltaY = maxY - minY;

            // Sort by dominant axis
            List<(ObjectId Id, Point3d Center)> sorted;
            if (deltaX > deltaY)
            {
                // Horizontal alignment - sort by X
                sorted = beamCenters.OrderBy(b => b.Center.X).ThenBy(b => b.Center.Y).ToList();
            }
            else
            {
                // Vertical alignment - sort by Y
                sorted = beamCenters.OrderBy(b => b.Center.Y).ThenBy(b => b.Center.X).ToList();
            }

            return sorted.Select(b => b.Id).ToList();
        }

        /// <summary>
        /// [V5.0] Get center point of a beam entity.
        /// </summary>
        public Point3d? GetBeamCenter(ObjectId id, Transaction tr)
        {
            try
            {
                if (id.IsErased) return null;

                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) return null;

                if (ent is Line line)
                {
                    return new Point3d(
                        (line.StartPoint.X + line.EndPoint.X) / 2,
                        (line.StartPoint.Y + line.EndPoint.Y) / 2,
                        (line.StartPoint.Z + line.EndPoint.Z) / 2);
                }
                else if (ent is Polyline pline && pline.NumberOfVertices >= 2)
                {
                    var first = pline.GetPoint3dAt(0);
                    var last = pline.GetPoint3dAt(pline.NumberOfVertices - 1);
                    return new Point3d(
                        (first.X + last.X) / 2,
                        (first.Y + last.Y) / 2,
                        (first.Z + last.Z) / 2);
                }
                else
                {
                    var ext = ent.GeometricExtents;
                    return new Point3d(
                        (ext.MinPoint.X + ext.MaxPoint.X) / 2,
                        (ext.MinPoint.Y + ext.MaxPoint.Y) / 2,
                        (ext.MinPoint.Z + ext.MaxPoint.Z) / 2);
                }
            }
            catch
            {
                return null;
            }
        }

        #endregion // V5.0: Geometric Sort
    }

    #region BeamTopology DTO

    /// <summary>
    /// DTO chứa thông tin topology của một beam entity.
    /// Tồn tại trong runtime, không persist.
    /// </summary>
    public class BeamTopology
    {
        public ObjectId ObjectId { get; set; }
        public string Handle { get; set; }
        public string SpanId { get; set; }
        public int SpanIndex { get; set; }

        // Geometry
        public Point3d StartPoint { get; set; }
        public Point3d EndPoint { get; set; }
        public Point3d MidPoint { get; set; }
        public double Length { get; set; }
        public double LevelZ { get; set; }

        /// <summary>
        /// True nếu geometry được vẽ R->L (StartPoint.X > EndPoint.X)
        /// </summary>
        public bool IsGeometryReversed { get; set; }

        // Section
        public double Width { get; set; }
        public double Height { get; set; }

        // Link info
        public string OriginHandle { get; set; }
        /// <summary>
        /// [V7.0] Handle của Mother beam trong Group
        /// </summary>
        public string MotherHandle { get; set; }
        public string SapElementName { get; set; }

        // V5.0: Group identification
        public string GroupId { get; set; }

        // XData references
        public ElementData ElementData { get; set; }
        public BeamResultData RebarData { get; set; }
    }

    #endregion
}
