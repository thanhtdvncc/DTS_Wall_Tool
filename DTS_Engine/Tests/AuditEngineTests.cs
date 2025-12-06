using System;
using System.Collections.Generic;
using System.Diagnostics;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Engines;

// This file provides both a lightweight manual test runner (AuditEngineTestRunner)
// and an optional MSTest class (compiled only when UNIT_TESTS symbol is defined).

namespace DTS_Engine.Tests
{
    public static class AuditEngineTestRunner
    {
        // Run quick checks; throws on failure so it can be used interactively.
        public static void Run()
        {
            var engine = new AuditEngine();

            // Case 1: majority lateral (3 of 4 -> true)
            var loads1 = new List<RawSapLoad>
            {
                new RawSapLoad { Direction = "Global X" },
                new RawSapLoad { Direction = "X Projected" },
                new RawSapLoad { Direction = "Global Y" },
                new RawSapLoad { Direction = "Gravity" }
            };
            bool res1 = engine.CheckIfLateralLoad(loads1);
            Debug.WriteLine($"Test1 expected=true got={res1}");
            if (!res1) throw new Exception("AuditEngine.CheckIfLateralLoad failed Test1 (expected true)");

            // Case 2: minority lateral (1 of 3 -> false)
            var loads2 = new List<RawSapLoad>
            {
                new RawSapLoad { Direction = "Gravity/Z" },
                new RawSapLoad { Direction = "Gravity" },
                new RawSapLoad { Direction = "Global X" }
            };
            bool res2 = engine.CheckIfLateralLoad(loads2);
            Debug.WriteLine($"Test2 expected=false got={res2}");
            if (res2) throw new Exception("AuditEngine.CheckIfLateralLoad failed Test2 (expected false)");

            // Case 3: strings variety (contains X/Y substrings)
            var loads3 = new List<RawSapLoad>
            {
                new RawSapLoad { Direction = "SHEAR X" },
                new RawSapLoad { Direction = "LATERAL" },
                new RawSapLoad { Direction = "Gravity" }
            };
            bool res3 = engine.CheckIfLateralLoad(loads3);
            Debug.WriteLine($"Test3 expected=true got={res3}");
            if (!res3) throw new Exception("AuditEngine.CheckIfLateralLoad failed Test3 (expected true)");

            // Case 4: empty/invalid
            var loads4 = new List<RawSapLoad>();
            bool res4 = engine.CheckIfLateralLoad(loads4);
            Debug.WriteLine($"Test4 expected=false got={res4}");
            if (res4) throw new Exception("AuditEngine.CheckIfLateralLoad failed Test4 (expected false)");

            Debug.WriteLine("All AuditEngine checks passed.");
        }
    }
}

#if UNIT_TESTS
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DTS_Engine.Tests
{
    [TestClass]
    public class AuditEngineTests
    {
        [TestMethod]
        public void CheckIfLateral_MajorityLateral_ReturnsTrue()
        {
            var engine = new AuditEngine();
            var loads = new List<RawSapLoad>
            {
                new RawSapLoad { Direction = "Global X" },
                new RawSapLoad { Direction = "X Projected" },
                new RawSapLoad { Direction = "Global Y" },
                new RawSapLoad { Direction = "Gravity" }
            };
            Assert.IsTrue(engine.CheckIfLateralLoad(loads));
        }

        [TestMethod]
        public void CheckIfLateral_MinorityLateral_ReturnsFalse()
        {
            var engine = new AuditEngine();
            var loads = new List<RawSapLoad>
            {
                new RawSapLoad { Direction = "Gravity/Z" },
                new RawSapLoad { Direction = "Gravity" },
                new RawSapLoad { Direction = "Global X" }
            };
            Assert.IsFalse(engine.CheckIfLateralLoad(loads));
        }

        [TestMethod]
        public void CheckIfLateral_VariousKeywords_ReturnsTrue()
        {
            var engine = new AuditEngine();
            var loads = new List<RawSapLoad>
            {
                new RawSapLoad { Direction = "SHEAR X" },
                new RawSapLoad { Direction = "LATERAL" },
                new RawSapLoad { Direction = "Gravity" }
            };
            Assert.IsTrue(engine.CheckIfLateralLoad(loads));
        }
    }
}
#endif
