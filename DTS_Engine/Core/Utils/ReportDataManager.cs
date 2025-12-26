using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
using DTS_Engine.Core.Algorithms;
using Autodesk.AutoCAD.DatabaseServices;

namespace DTS_Engine.Core.Utils
{
    public static class ReportDataManager
    {
        public static string BuildReportJson(List<BeamGroup> groups)
        {
            if (groups == null || !groups.Any()) return "[]";

            // Dictionary để gộp theo tên tiết diện
            // Key: SectionName (VD: "300x500" hoặc tên do user đặt)
            var sectionMap = new Dictionary<string, ReportGroupData>();
            string projectName = string.IsNullOrEmpty(AcadUtils.Doc?.Name) ? "DTS Project" : System.IO.Path.GetFileNameWithoutExtension(AcadUtils.Doc.Name);

            foreach (var group in groups)
            {
                foreach (var span in group.Spans)
                {
                    // Lấy ID tiết diện: Ưu tiên nhãn tiết diện gán bởi người dùng, fallback về WxH
                    string secName = span.xSectionLabel;
                    if (string.IsNullOrEmpty(secName))
                    {
                        secName = $"{span.Width}x{span.Height}";
                    }

                    if (!sectionMap.ContainsKey(secName))
                    {
                        sectionMap[secName] = new ReportGroupData
                        {
                            GroupName = secName,      // Hiển thị tên tiết diện làm header
                            SectionName = secName,
                            ProjectName = projectName,
                            Spans = new List<ReportSpanData>()
                        };
                    }

                    var reportSpan = ConvertSpanToReportData(span);
                    sectionMap[secName].Spans.Add(reportSpan);
                }
            }

            // Chuyển sang danh sách để trả về
            var sortedGroups = sectionMap.Values.OrderBy(g => g.SectionName).ToList();

            return JsonConvert.SerializeObject(sortedGroups, Formatting.Indented);
        }

        private static ReportSpanData ConvertSpanToReportData(SpanData span)
        {
            var reportSpan = new ReportSpanData
            {
                SpanId = span.SpanId,
                Section = $"{span.Width}x{span.Height}",
                Length = Math.Round(span.Length * 1000).ToString(), // mm
                Material = "B25 / CB400" // Fallback hoặc lấy từ segment đầu tiên
            };

            // Lấy dữ liệu 3 vùng từ các PhysicalSegment
            // Chiến thuật: Lấy segment đầu cho Left, segment cuối cho Right, segment giữa cho Mid.
            // Hoặc nếu 1 segment duy nhất thì lấy 3 zone của nó.

            var firstSeg = span.Segments.FirstOrDefault();
            var lastSeg = span.Segments.LastOrDefault();
            var midSeg = span.Segments.ElementAtOrDefault(span.Segments.Count / 2);

            if (firstSeg != null)
            {
                var dataLeft = GetResultData(firstSeg.EntityHandle);
                var dataMid = GetResultData(midSeg.EntityHandle);
                var dataRight = GetResultData(lastSeg.EntityHandle);

                reportSpan.Material = $"{dataLeft?.ConcreteGrade ?? ""} / {dataLeft?.SteelGrade ?? ""}";

                reportSpan.Left = CreateStationData(dataLeft, 0, span.SpanId, "Gối Trái");
                reportSpan.Mid = CreateStationData(dataMid, 1, span.SpanId, "Giữa Nhịp");
                reportSpan.Right = CreateStationData(dataRight, 2, span.SpanId, "Gối Phải");
            }

            return reportSpan;
        }

        private static ReportStationData CreateStationData(BeamResultData data, int zoneIndex, string spanId, string stationLabel)
        {
            var station = new ReportStationData
            {
                ElementId = spanId,
                Station = stationLabel,
                LoadCase = data?.TopCombo?.ElementAtOrDefault(zoneIndex) ?? ""
            };

            string sapElem = data?.SapElementNos?.ElementAtOrDefault(zoneIndex) ?? "-";
            string sapLoc = data?.LocationMm?.ElementAtOrDefault(zoneIndex).ToString() ?? "-";

            // Top - mm2
            double? topReq = data != null ? (data.TopArea?[zoneIndex] * 100.0) : (double?)null;
            double? topProv = data != null ? (data.TopAreaProv?[zoneIndex] * 100.0) : (double?)null;
            station.TopResult = new ReportForceResult
            {
                ElementId = sapElem,
                Station = stationLabel,
                LocationMm = sapLoc,
                Moment = data != null ? Math.Round(data.TopMoment[zoneIndex], 2) : (double?)null,
                AsCalc = topReq.HasValue ? Math.Round(topReq.Value, 1) : (double?)null,
                AsProv = topProv.HasValue ? Math.Round(topProv.Value, 1) : (double?)null,
                RebarStr = data?.TopRebarString?.ElementAtOrDefault(zoneIndex) ?? "-",
                LoadCase = data?.TopCombo?.ElementAtOrDefault(zoneIndex) ?? "-",
                Ratio = CalcRatio(topReq, topProv),
                Conclusion = GetConclusion(CalcRatio(topReq, topProv))
            };

            // Bot - mm2
            double? botReq = data != null ? (data.BotArea?[zoneIndex] * 100.0) : (double?)null;
            double? botProv = data != null ? (data.BotAreaProv?[zoneIndex] * 100.0) : (double?)null;
            station.BotResult = new ReportForceResult
            {
                ElementId = sapElem,
                Station = stationLabel,
                LocationMm = sapLoc,
                Moment = data != null ? Math.Round(data.BotMoment[zoneIndex], 2) : (double?)null,
                AsCalc = botReq.HasValue ? Math.Round(botReq.Value, 1) : (double?)null,
                AsProv = botProv.HasValue ? Math.Round(botProv.Value, 1) : (double?)null,
                RebarStr = data?.BotRebarString?.ElementAtOrDefault(zoneIndex) ?? "-",
                LoadCase = data?.BotCombo?.ElementAtOrDefault(zoneIndex) ?? "-",
                Ratio = CalcRatio(botReq, botProv),
                Conclusion = GetConclusion(CalcRatio(botReq, botProv))
            };

            // Stirrup Total (2At/st + Av/sv) - mm2/mm
            double? stirTotalReq = (data != null) ? (2 * data.TTArea[zoneIndex] + data.ShearArea[zoneIndex]) : (double?)null;
            double? stirProv = data != null ? (StirrupStringParser.ParseAsProv(data.StirrupString?.ElementAtOrDefault(zoneIndex)) / 100.0) : (double?)null;

            station.StirrupResult = new ReportForceResult
            {
                ElementId = sapElem,
                Station = stationLabel,
                LocationMm = sapLoc,
                Shear = data != null ? Math.Round(data.ShearForce[zoneIndex], 2) : (double?)null,
                AsCalc = stirTotalReq.HasValue ? Math.Round(stirTotalReq.Value, 3) : (double?)null,
                AsProv = stirProv.HasValue ? Math.Round(stirProv.Value, 3) : (double?)null,
                RebarStr = data?.StirrupString?.ElementAtOrDefault(zoneIndex) ?? "-",
                LoadCase = data?.ShearCombo?.ElementAtOrDefault(zoneIndex) ?? "-",
                Ratio = CalcRatio(stirTotalReq, stirProv),
                Conclusion = GetConclusion(CalcRatio(stirTotalReq, stirProv))
            };

            // Stirrup Only (2At/sv ?)
            double? stirOnlyReq = (data != null) ? (2 * data.TTArea[zoneIndex]) : (double?)null;
            station.StirrupOnlyResult = new ReportForceResult
            {
                ElementId = sapElem,
                Station = stationLabel,
                LocationMm = sapLoc,
                AsCalc = stirOnlyReq.HasValue ? Math.Round(stirOnlyReq.Value, 3) : (double?)null,
                AsProv = stirProv.HasValue ? Math.Round(stirProv.Value, 3) : (double?)null,
                Ratio = CalcRatio(stirOnlyReq, stirProv),
                Conclusion = GetConclusion(CalcRatio(stirOnlyReq, stirProv), 0.67) // 1/1.5 = 0.67
            };

            // Web Bar (Side bar) - mm2 (Total Area Al req)
            double? alReq = data != null ? (data.TorsionArea?[zoneIndex] * 100.0) : (double?)null;
            double? webProv = data != null ? (RebarCalculator.ParseRebarArea(data.WebBarString?.ElementAtOrDefault(zoneIndex)) * 100.0) : (double?)null;

            station.AlResult = new ReportForceResult
            {
                ElementId = sapElem,
                Station = stationLabel,
                LocationMm = sapLoc,
                AsCalc = alReq.HasValue ? Math.Round(alReq.Value, 1) : (double?)null,
                AsProv = webProv.HasValue ? Math.Round(webProv.Value, 1) : (double?)null,
                LoadCase = data?.TorsionCombo?.ElementAtOrDefault(zoneIndex) ?? "-",
                Ratio = CalcRatio(alReq, webProv),
                Conclusion = GetConclusion(CalcRatio(alReq, webProv))
            };

            station.Legs = data != null ? StirrupStringParser.GetLegs(data.StirrupString?.ElementAtOrDefault(zoneIndex)) : 0;

            station.WebResult = new ReportForceResult
            {
                ElementId = sapElem,
                Station = stationLabel,
                RebarStr = data?.WebBarString?.ElementAtOrDefault(zoneIndex) ?? "-",
                AsProv = webProv.HasValue ? Math.Round(webProv.Value, 1) : (double?)null
            };

            return station;
        }

        private static string GetConclusion(double? ratio, double threshold = 0.95)
        {
            if (!ratio.HasValue) return "-";
            return ratio.Value >= threshold ? "OK" : "NG";
        }

        private static double CheckStirrupReq(BeamResultData data, int idx)
        {
            if (data == null) return 0;
            return (2 * data.TTArea[idx] + data.ShearArea[idx]) * 100.0;
        }

        private static double? CalcRatio(double? req, double? prov)
        {
            if (!req.HasValue || !prov.HasValue) return null;
            if (req.Value <= 1e-6) return 9.99; // Very safe
            if (prov.Value <= 1e-6) return 0; // Very unsafe
            return Math.Round(prov.Value / req.Value, 3);
        }

        private static BeamResultData GetResultData(string handle)
        {
            if (string.IsNullOrEmpty(handle)) return null;
            try
            {
                using (var tr = HostApplicationServices.WorkingDatabase.TransactionManager.StartTransaction())
                {
                    ObjectId id = AcadUtils.GetObjectIdFromHandle(handle);
                    if (!id.IsNull)
                    {
                        using (var obj = tr.GetObject(id, OpenMode.ForRead))
                        {
                            return XDataUtils.ReadElementData<BeamResultData>(obj);
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
