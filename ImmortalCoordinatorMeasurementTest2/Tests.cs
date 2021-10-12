using NUnit.Framework;

namespace ImmortalCoordinatorMeasurementTest
{
    [TestFixture]
    public class Tests
    {
        private AmbrosiaRuntime _runtime;

        [Setup]
        public void SetUp()
        {
            _runtime = new AmbrosiaRuntime();
            _runtime.InitializeMetric();
        }
        
        [Test]
        public void Test1()
        {
            Assert.True(true);
        }
    }
}