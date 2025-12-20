using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Utils
{
    public static class RebarXDataBridge
    {
        // BeamResultData arrays are 3 zones: 0=Start,1=Mid,2=End
        // SpanData arrays are 6 positions: 0..5 (0/1=Start, 2/3=Mid, 4/5=End)

        public static void FillSpanFromBeamResultData(
            SpanData span,
            BeamResultData data,
            DtsSettings settings,
            bool includeProvidedLayout,
            bool includeRequired)
        {
            if (span == null || data == null) return;

            EnsureSpanArrays(span);

            if (includeProvidedLayout)
            {
                MapZonesToSpan6(span.TopRebar, 0, data.TopRebarString);
                MapZonesToSpan6(span.BotRebar, 0, data.BotRebarString);

                span.Stirrup[0] = SafeZone(data.StirrupString, 0);
                span.Stirrup[1] = SafeZone(data.StirrupString, 1);
                span.Stirrup[2] = SafeZone(data.StirrupString, 2);

                span.WebBar[0] = SafeZone(data.WebBarString, 0);
                span.WebBar[1] = SafeZone(data.WebBarString, 1);
                span.WebBar[2] = SafeZone(data.WebBarString, 2);
            }

            if (includeRequired)
            {
                double torsTop = settings?.Beam?.TorsionDist_TopBar ?? 0.25;
                double torsBot = settings?.Beam?.TorsionDist_BotBar ?? 0.25;
                double torsSide = settings?.Beam?.TorsionDist_SideBar ?? 0.50;

                for (int zi = 0; zi < 3; zi++)
                {
                    int p0 = ZoneToPos0(zi);
                    int p1 = p0 + 1;

                    double asTopReq = (data.TopArea?.ElementAtOrDefault(zi) ?? 0) + (data.TorsionArea?.ElementAtOrDefault(zi) ?? 0) * torsTop;
                    double asBotReq = (data.BotArea?.ElementAtOrDefault(zi) ?? 0) + (data.TorsionArea?.ElementAtOrDefault(zi) ?? 0) * torsBot;

                    span.As_Top[p0] = asTopReq;
                    span.As_Top[p1] = asTopReq;
                    span.As_Bot[p0] = asBotReq;
                    span.As_Bot[p1] = asBotReq;

                    // Shear/Web required (mirror DTS_REBAR_SHOW mode 3)
                    span.StirrupReq[zi] = (data.ShearArea?.ElementAtOrDefault(zi) ?? 0);
                    span.WebReq[zi] = (data.TorsionArea?.ElementAtOrDefault(zi) ?? 0) * torsSide;
                }
            }
        }

        public static (string[] TopZones, string[] BotZones, string[] StirrupZones, string[] WebZones) BuildSolutionZonesFromSpan(SpanData span)
        {
            if (span == null) return (new string[3], new string[3], new string[3], new string[3]);

            EnsureSpanArrays(span);

            var top = new string[3]
            {
                BuildRebarStringFrom2D(span.TopRebar, 0),
                BuildRebarStringFrom2D(span.TopRebar, 2),
                BuildRebarStringFrom2D(span.TopRebar, 4)
            };
            var bot = new string[3]
            {
                BuildRebarStringFrom2D(span.BotRebar, 0),
                BuildRebarStringFrom2D(span.BotRebar, 2),
                BuildRebarStringFrom2D(span.BotRebar, 4)
            };

            var stir = new string[3]
            {
                SafeIndex(span.Stirrup, 0),
                SafeIndex(span.Stirrup, 1),
                SafeIndex(span.Stirrup, 2)
            };
            var web = new string[3]
            {
                SafeIndex(span.WebBar, 0),
                SafeIndex(span.WebBar, 1),
                SafeIndex(span.WebBar, 2)
            };

            return (top, bot, stir, web);
        }

        private static void EnsureSpanArrays(SpanData span)
        {
            if (span.As_Top == null || span.As_Top.Length < 6) span.As_Top = new double[6];
            if (span.As_Bot == null || span.As_Bot.Length < 6) span.As_Bot = new double[6];
            if (span.TopRebar == null || span.TopRebar.GetLength(0) < 3 || span.TopRebar.GetLength(1) < 6) span.TopRebar = new string[3, 6];
            if (span.BotRebar == null || span.BotRebar.GetLength(0) < 3 || span.BotRebar.GetLength(1) < 6) span.BotRebar = new string[3, 6];
            if (span.Stirrup == null || span.Stirrup.Length < 3) span.Stirrup = new string[3];
            if (span.WebBar == null || span.WebBar.Length < 3) span.WebBar = new string[3];
            if (span.StirrupReq == null || span.StirrupReq.Length < 3) span.StirrupReq = new double[3];
            if (span.WebReq == null || span.WebReq.Length < 3) span.WebReq = new double[3];
        }

        private static void MapZonesToSpan6(string[,] target, int layer, string[] zones)
        {
            if (target == null) return;
            for (int zi = 0; zi < 3; zi++)
            {
                int p0 = ZoneToPos0(zi);
                int p1 = p0 + 1;
                var v = SafeZone(zones, zi);
                if (!string.IsNullOrEmpty(v))
                {
                    target[layer, p0] = v;
                    target[layer, p1] = v;
                }
            }
        }

        private static int ZoneToPos0(int zoneIndex)
        {
            if (zoneIndex <= 0) return 0;
            if (zoneIndex == 1) return 2;
            return 4;
        }

        private static string SafeZone(string[] arr, int idx) => (arr != null && idx >= 0 && idx < arr.Length) ? (arr[idx] ?? "") : "";
        private static string SafeIndex(string[] arr, int idx) => (arr != null && idx >= 0 && idx < arr.Length) ? (arr[idx] ?? "") : "";

        private static string BuildRebarStringFrom2D(string[,] arr, int position)
        {
            if (arr == null) return "";

            var parts = new List<string>();
            for (int layer = 0; layer < arr.GetLength(0); layer++)
            {
                if (position >= arr.GetLength(1)) continue;
                var v = arr[layer, position];
                if (!string.IsNullOrWhiteSpace(v)) parts.Add(v);
            }
            return string.Join("+", parts);
        }
    }
}
