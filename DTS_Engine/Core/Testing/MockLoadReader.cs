using DTS_Engine.Core.Data;
using DTS_Engine.Core.Interfaces;
using System;
using System.Collections.Generic;

namespace DTS_Engine.Core.Testing
{
    /// <summary>
    /// Mock implementation c?a ISapLoadReader cho Unit Testing.
    /// 
    /// RATIONALE:
    /// - Dependency Injection cho phép test AuditEngine mà không c?n SAP2000
    /// - Mock này tr? v? d? li?u gi? l?p ?? verify logic
    /// - Tuân th? Test Driven Development (TDD)
    /// 
    /// USAGE:
    /// var mockReader = new MockLoadReader();
    /// mockReader.AddMockLoad("B1", "DL", 10.0, LoadType.FrameDistributed);
    /// var engine = new AuditEngine(mockReader);
    /// var report = engine.RunSingleAudit("DL");
    /// </summary>
    public class MockLoadReader : ISapLoadReader
    {
        private readonly List<RawSapLoad> _mockLoads;

        public MockLoadReader()
        {
            _mockLoads = new List<RawSapLoad>();
        }

        /// <summary>
        /// Thêm t?i tr?ng gi? l?p vào Mock Reader.
        /// </summary>
        public void AddMockLoad(string elementName, string pattern, double value, string loadType, 
            double dirX = 0, double dirY = 0, double dirZ = -1, double elemZ = 0)
        {
            var load = new RawSapLoad
            {
                ElementName = elementName,
                LoadPattern = pattern,
                Value1 = value,
                LoadType = loadType,
                ElementZ = elemZ,
                Direction = "Mock"
            };
            load.SetForceVector(new Primitives.Vector3D(dirX, dirY, dirZ));
            _mockLoads.Add(load);
        }

        /// <summary>
        /// Thêm t?i tr?ng gi? l?p v?i ??y ?? thông tin.
        /// </summary>
        public void AddMockLoad(RawSapLoad load)
        {
            _mockLoads.Add(load);
        }

        /// <summary>
        /// Clear t?t c? mock data.
        /// </summary>
        public void Clear()
        {
            _mockLoads.Clear();
        }

        /// <summary>
        /// Implementation c?a ISapLoadReader.ReadAllLoads
        /// Tr? v? mock data thay vì ??c t? SAP.
        /// </summary>
        public List<RawSapLoad> ReadAllLoads(string patternFilter)
        {
            if (string.IsNullOrEmpty(patternFilter))
                return new List<RawSapLoad>(_mockLoads);

            var filtered = new List<RawSapLoad>();
            foreach (var load in _mockLoads)
            {
                if (load.LoadPattern.Equals(patternFilter, StringComparison.OrdinalIgnoreCase))
                    filtered.Add(load);
            }
            return filtered;
        }

        /// <summary>
        /// Factory method: T?o Mock Reader v?i d? li?u m?u chu?n.
        /// </summary>
        public static MockLoadReader CreateSampleData()
        {
            var mock = new MockLoadReader();

            // Frame Distributed Load (Gravity)
            mock.AddMockLoad("B1", "DL", 10.0, "FrameDistributed", 0, 0, -10, 3000);
            mock.AddMockLoad("B2", "DL", 8.0, "FrameDistributed", 0, 0, -8, 3000);

            // Area Uniform Load (Gravity)
            mock.AddMockLoad("F1", "DL", 5.0, "AreaUniform", 0, 0, -5, 3000);
            
            // Lateral Point Load (Wind)
            mock.AddMockLoad("J1", "WX", 50.0, "PointForce", 50, 0, 0, 6000);

            return mock;
        }
    }
}
