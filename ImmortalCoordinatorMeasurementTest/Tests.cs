using System;
using System.Collections.Generic;
using Ambrosia;
using Microsoft.QualityTools.Testing.Fakes;
using NUnit.Framework;

namespace ImmortalCoordinatorMeasurementTest
{
    [TestFixture]
    public class Tests
    {
        [Test]
        public void Test1()
        {
            using (ShimsContext.Create())
            {
                GenericLogsInterface.SetToGenericLogs();
                // Console.WriteLine($"COR_PROFILER_PATH: {Environment.GetEnvironmentVariable("COR_PROFILER_PATH")} | COR_PROFILER: {Environment.GetEnvironmentVariable("COR_PROFILER")}");
                var _runtime = new AmbrosiaRuntime();
                _runtime.InitializeMetric(5000, 5001, "analytics",
                    @"C:\Logs", true, false, false,
                    8L, "", 0L, 0L, false, @"C:\Logs",
                    @"E:\Studium\Master\Studienprojekt\AMBROSIA\TimeTravelDebuggingStudienprojekt\Analytics\Analytics.csproj",
                    @"E:\Studium\Master\Studienprojekt\Plugins", new Dictionary<string, object>()
                    {
                        {"LogSize", 512},
                    });
            }
            Assert.True(true);
        }
    }
}