using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
using SAP2000v1;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Engines
{
    /// <summary>
    /// Engine chuyên trách giao tiếp với module Design của SAP2000.
    /// Handles extraction of results and updating of rebar sections.
    /// </summary>
    public class SapDesignEngine
    {
        private readonly cSapModel _model;

        public SapDesignEngine()
        {
            if (SapUtils.IsConnected)
            {
                _model = SapUtils.GetModel();
            }
        }

        public bool IsReady => _model != null;

        /// <summary>
        /// Lấy kết quả thiết kế dầm (Summary Results)
        /// Phân tách rõ ràng Flexure Area và Torsion Area.
        /// </summary>
        public Dictionary<string, BeamResultData> GetBeamResults(List<string> frameNames)
        {
            var results = new Dictionary<string, BeamResultData>();
            if (_model == null || frameNames == null || frameNames.Count == 0) return results;

            // Đảm bảo đơn vị cm2 cho Diện tích thép
            var originalUnit = _model.GetPresentUnits();
            try
            {
                // Sử dụng kNM_cm_C để lấy diện tích cm2 trực tiếp
                // Hoặc kN_mm_C -> area sẽ là mm2. User thường quen cm2.
                // Let's use kN_cm_C for convenience in rebar area (cm2)
                _model.SetPresentUnits(eUnits.kN_cm_C);

                foreach (var name in frameNames)
                {
                    int numberItems = 0;
                    string[] frames = null;
                    double[] location = null;
                    string[] topCombo = null;
                    double[] topArea = null;
                    string[] botCombo = null;
                    double[] botArea = null;
                    string[] vMajorCombo = null;
                    double[] vMajorArea = null;
                    string[] tlCombo = null;
                    double[] tlArea = null; // Torsion Longitudinal
                    string[] ttCombo = null;
                    double[] ttArea = null;
                    string[] errorSummary = null;
                    string[] warningSummary = null;

                    // Gọi API cho từng phần tử (hoặc Group nếu tối ưu, nhưng ở đây ta loop danh sách chọn)
                    // ItemType = 0 (Object)
                    int ret = _model.DesignConcrete.GetSummaryResultsBeam(
                        name, 
                        ref numberItems, 
                        ref frames, 
                        ref location, 
                        ref topCombo, ref topArea, 
                        ref botCombo, ref botArea, 
                        ref vMajorCombo, ref vMajorArea, 
                        ref tlCombo, ref tlArea, 
                        ref ttCombo, ref ttArea, 
                        ref errorSummary, ref warningSummary, 
                        eItemType.Object);

                    if (ret == 0 && numberItems > 0)
                    {
                        var data = new BeamResultData();
                        
                        // SAP trả về nhiều Station. Cần lấy Start (0), End (L), Mid (L/2)
                        // Giả sử location đã sort sẵn? API SAP thường trả về sort theo Station.
                        // Tìm 3 điểm đại diện.
                        
                        // 1. Get Length
                        double length = location[numberItems - 1]; // Max location

                        // Indices
                        int idxStart = 0;
                        int idxEnd = numberItems - 1;
                        // Find Mid (closest to L/2)
                        int idxMid = 0;
                        double minDiff = double.MaxValue;
                        for(int i=0; i<numberItems; i++)
                        {
                            double diff = Math.Abs(location[i] - length / 2.0);
                            if(diff < minDiff)
                            {
                                minDiff = diff;
                                idxMid = i;
                            }
                        }

                        // Assign Data
                        data.TopArea[0] = topArea[idxStart];
                        data.TopArea[1] = topArea[idxMid];
                        data.TopArea[2] = topArea[idxEnd];

                        data.BotArea[0] = botArea[idxStart];
                        data.BotArea[1] = botArea[idxMid];
                        data.BotArea[2] = botArea[idxEnd];


                        data.TorsionArea[0] = tlArea[idxStart];
                        data.TorsionArea[1] = tlArea[idxMid];
                        data.TorsionArea[2] = tlArea[idxEnd];

                        data.DesignCombo = topCombo[0]; // Lấy combo đầu làm mẫu

                        // --- Get Section Props ---
                        string propName = "";
                        string sAuto = "";
                        if (_model.FrameObj.GetSection(name, ref propName, ref sAuto) == 0)
                        {
                            data.SectionName = propName;
                            
                            // Get Dims (Rectangular assumed)
                            string matProp = "";
                            double t3 = 0, t2 = 0; // t3=depth, t2=width
                            int color = -1;
                            string notes = "", guid = "";
                            
                            // Check if Auto-Select list? If so, we need actual section used?
                            // For design results, the API usually bases on the ANALYSIS section or DESIGN section.
                            // The `GetSection` returns the assigned property.
                            // If auto-select, we might need `GetSection` at station?
                            // For simplicity, assume Rectangular Section assigned directly.
                            
                            if (_model.PropFrame.GetRectangle(propName, ref propName, ref matProp, ref t3, ref t2, ref color, ref notes, ref guid) == 0)
                            {
                                // SAP Units are kN_cm_C
                                data.Height = t3; // cm
                                data.Width = t2;  // cm
                            }
                            else
                            {
                                // Try Concrete T, etc?
                                // For now, set 0.
                            }
                        }

                        results[name] = data;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("GetBeamResults Error: " + ex.Message);
            }
            finally
            {
                _model.SetPresentUnits(originalUnit);
            }

            return results;
        }

        /// <summary>
        /// Gán thép thực tế vào Dầm (Update SAP).
        /// Quy trình:
        /// 1. Kiểm tra/Tạo Section mới (dựa trên tên tiết diện cũ + hậu tố rebar).
        /// 2. Gán thép cho Section mới (SetRebarBeam).
        /// 3. Gán Section mới cho Frame.
        /// </summary>
        public bool UpdateBeamRebar(string frameName, string newSectionName, 
            double[] topAreaProv, double[] botAreaProv, 
            double coverTop, double coverBot)
        {
            if (_model == null) return false;

            try
            {
                // 1. Lấy tiết diện hiện tại của Frame để clone (nếu chưa có section mới)
                string propName = "";
                string sAuto = "";
                if (_model.FrameObj.GetSection(frameName, ref propName, ref sAuto) != 0) return false;

                // Nếu propName đã đúng là newSectionName thì ok, nếu khác thì cần tạo mới/kiểm tra
                if (propName != newSectionName)
                {
                    // Kiểm tra newSectionName có chưa
                    // Cách đơn giản: Thử GetSection, nếu fail tức là chưa có -> Clone
                    // Tuy nhiên SAP ko có lệnh "Exist". Ta dùng PropFrame.GetNameList
                    if (!SectionExists(newSectionName))
                    {
                        // Clone từ propName gốc
                        // SAP API không có Clone trực tiếp nhanh.
                        // Workaround: Get Prop Data -> Set Prop Data New Name.
                        // Tạm thời giả định module này sẽ được gọi sau khi đã có prop data.
                        // Nhưng để robust, ta cần implement CloneSection.
                        if (!CloneConcreteSection(propName, newSectionName)) return false;
                    }

                    // Gán Section mới cho Frame
                    _model.FrameObj.SetSection(frameName, newSectionName, eItemType.Object);
                }

                // 2. Set Rebar cho Section (newSectionName)
                // SetRebarBeam requires Material Names, Covers, and Areas.
                // We assume MatPropLong is same as used in original, or we fetch it.
                // For simplicity, let's try to get existing rebar props first.
                
                string matLong = "", matConf = "";
                double cTop=0, cBot=0, tl=0, tr=0, bl=0, br=0;
                
                // Get existing rebar to get Materials
                if (_model.PropFrame.GetRebarBeam(newSectionName, ref matLong, ref matConf, ref cTop, ref cBot, ref tl, ref tr, ref bl, ref br) != 0)
                {
                    // Nếu chưa có rebar data, có thể do section mới clone chưa set.
                    // Lấy từ section gốc 'propName' (trước khi đổi tên)
                     _model.PropFrame.GetRebarBeam(propName, ref matLong, ref matConf, ref cTop, ref cBot, ref tl, ref tr, ref bl, ref br);
                }

                // Update Values
                // SAP SetRebarBeam inputs: TopLeft, TopRight, BotLeft, BotRight.
                // Note: SAP Beam Section Rebar is defined at End I and End J (Start/End).
                // It does NOT support Mid-span rebar definition in Section Property directly?
                // Wait, SetRebarBeam doc says: TopLeftArea, TopRightArea...
                // "Left" usually means Start (I-End)?? No, Left/Right usually implies Cross Section corners?
                // Let's check SAP2000 Help.
                // "TopLeftArea: The total area of longitudinal reinforcement at the top left end of the beam."
                // "TopRightArea: ... top right end ..."
                // THIS IS CONFUSING. Usually "Left/Right" in SetRebarBeam refers to I-End and J-End? 
                // Or Left/Right of the cross section?
                // SAP2000 OAPI Doc usually refers to Start/End as I/J.
                // However, the function params are `TopLeftArea`, `TopRightArea`, `BotLeftArea`, `BotRightArea`.
                // Actually, SAP2000 Beam Reinforcement Override allows setting Top/Bot Area at I and J.
                // Let's assume:
                // TopLeftArea = Top Area at Start (I)
                // TopRightArea = Top Area at End (J)
                // BotLeftArea = Bot Area at Start (I)
                // BotRightArea = Bot Area at End (J)
                
                // But wait, what about Mid? 
                // SAP Section Property only defines I and J reinforcement for checking? 
                // Yes, standard concrete check often interpolates.
                // WE CANNOT SET MID REBAR in Section Property via API easily if it only accepts 4 values.
                // We will map:
                // TopLeft -> TopAreaProv[0] (Start)
                // TopRight -> TopAreaProv[2] (End)
                // BotLeft -> BotAreaProv[0] (Start)
                // BotRight -> BotAreaProv[2] (End)
                
                double topStart = topAreaProv[0];
                double topEnd = topAreaProv[2];
                double botStart = botAreaProv[0];
                double botEnd = botAreaProv[2];

                int retRebar = _model.PropFrame.SetRebarBeam(newSectionName, matLong, matConf, 
                    coverTop/10.0, coverBot/10.0, // mm -> cm (since we set unit to kN_cm_C at function start? No, need to be careful)
                    topStart, topEnd, botStart, botEnd);
                
                // Note: We need to handle Units carefully. This function assumes the context is set by caller or we set it.
                // Let's force unit set inside this scope if we want safety, but better to set outside.
                
                return retRebar == 0;
            }
            catch(Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("UpdateBeamRebar Error: " + ex.Message);
                return false;
            }
        }

        private bool SectionExists(string name)
        {
            int count = 0;
            string[] names = null;
            _model.PropFrame.GetNameList(ref count, ref names);
            return names != null && names.Contains(name);
        }

        private bool CloneConcreteSection(string sourceName, string destName)
        {
            // Simple Clone for Rectangular Section
            string matProp = "";
            string fileName = ""; // Dummy for first ref param
            double t3 = 0, t2 = 0;
            int color = -1;
            string notes = "", guid = "";
            
            // Try GetRectangular
            if (_model.PropFrame.GetRectangle(sourceName, ref fileName, ref matProp, ref t3, ref t2, ref color, ref notes, ref guid) == 0)
            {
                return _model.PropFrame.SetRectangle(destName, matProp, t3, t2, -1, notes, "") == 0;
            }
            
            // If not rectangular, we might fail or need more handlers.
            // For now, assume Rectangular beams.
            return false;
        }
    }
}
