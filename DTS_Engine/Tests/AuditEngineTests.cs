using System;
using System.Collections.Generic;
using System.Diagnostics;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Engines;
using DTS_Engine.Core.Primitives;

// This file provides both a lightweight manual test runner (AuditEngineTestRunner)
// and an optional MSTest class (compiled only when UNIT_TESTS symbol is defined).

namespace DTS_Engine.Tests
{
    public static class AuditEngineTestRunner
    {
        // Run quick checks for Vector3D functionality
        public static void Run()
        {
            Debug.WriteLine("=== AuditEngine Vector Tests ===");

            // Test 1: Vector3D basic operations
            var v1 = new Vector3D(3, 4, 0);
            var v2 = new Vector3D(0, 0, 5);
            
            Debug.WriteLine($"Test1: v1={v1}, Length={v1.Length:F3} (expected 5.000)");
            if (Math.Abs(v1.Length - 5.0) > 0.001)
                throw new Exception("Vector3D.Length failed");

            // Test 2: Dot product
            double dot = v1.Dot(v2);
            Debug.WriteLine($"Test2: v1·v2={dot:F3} (expected 0.000)");
            if (Math.Abs(dot) > 0.001)
                throw new Exception("Vector3D.Dot failed");

            // Test 3: Cross product
            var cross = v1.Cross(v2);
            Debug.WriteLine($"Test3: v1×v2={cross} (expected (20, -15, 0))");
            if (Math.Abs(cross.X - 20) > 0.001 || Math.Abs(cross.Y + 15) > 0.001)
                throw new Exception("Vector3D.Cross failed");

            // Test 4: IsLateral check
            var gravityLoad = new Vector3D(0, 0, -10);
            var lateralLoad = new Vector3D(5, 0, -1);
            
            Debug.WriteLine($"Test4: Gravity.IsLateral={gravityLoad.IsLateral} (expected False)");
            Debug.WriteLine($"Test4: Lateral.IsLateral={lateralLoad.IsLateral} (expected True)");
            
            if (gravityLoad.IsLateral)
                throw new Exception("Gravity incorrectly identified as lateral");
            if (!lateralLoad.IsLateral)
                throw new Exception("Lateral load not identified correctly");

            // Test 5: RawSapLoad vector operations
            var load = new RawSapLoad
            {
                ElementName = "F1",
                LoadPattern = "DL",
                Value1 = 10.0
            };
            
            var forceVector = new Vector3D(0, 0, -10);
            load.SetForceVector(forceVector);
            
            Debug.WriteLine($"Test5: Load DirectionZ={load.DirectionZ:F2} (expected -10.00)");
            Debug.WriteLine($"Test5: Load GlobalAxis={load.GlobalAxis} (expected Z)");
            
            if (Math.Abs(load.DirectionZ + 10) > 0.001)
                throw new Exception("RawSapLoad.SetForceVector failed");
            if (load.GlobalAxis != "Z")
                throw new Exception("RawSapLoad.GlobalAxis detection failed");

            Debug.WriteLine("All AuditEngine Vector tests passed.");
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
        public void Vector3D_Length_Calculated()
        {
            var v = new Vector3D(3, 4, 0);
            Assert.AreEqual(5.0, v.Length, 0.001);
        }

        [TestMethod]
        public void Vector3D_DotProduct_Perpendicular()
        {
            var v1 = new Vector3D(1, 0, 0);
            var v2 = new Vector3D(0, 1, 0);
            Assert.AreEqual(0.0, v1.Dot(v2), 0.001);
        }

        [TestMethod]
        public void Vector3D_IsLateral_Gravity()
        {
            var gravity = new Vector3D(0, 0, -10);
            Assert.IsFalse(gravity.IsLateral);
        }

        [TestMethod]
        public void Vector3D_IsLateral_Wind()
        {
            var wind = new Vector3D(5, 0, -1);
            Assert.IsTrue(wind.IsLateral);
        }

        [TestMethod]
        public void RawSapLoad_SetForceVector_UpdatesComponents()
        {
            var load = new RawSapLoad();
            var force = new Vector3D(1, 2, 3);
            load.SetForceVector(force);

            Assert.AreEqual(1.0, load.DirectionX, 0.001);
            Assert.AreEqual(2.0, load.DirectionY, 0.001);
            Assert.AreEqual(3.0, load.DirectionZ, 0.001);
            Assert.AreEqual("Z", load.GlobalAxis);
        }
    }
}
#endif
