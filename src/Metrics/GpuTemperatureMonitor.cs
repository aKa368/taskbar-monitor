using LibreHardwareMonitor.Hardware;

namespace TaskbarMonitor.Metrics;

/// <summary>
/// Optional hardware-sensor provider. LibreHardwareMonitor exposes GPU core
/// temperatures for AMD/NVIDIA/Intel when the driver/firmware makes them
/// available. Missing sensors are normal and return 0 without failing the app.
/// </summary>
public sealed class GpuTemperatureMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _opened;

    public GpuTemperatureMonitor()
    {
        _computer = new Computer
        {
            IsGpuEnabled = true,
            IsCpuEnabled = false,
            IsMemoryEnabled = false,
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsStorageEnabled = false
        };

        try
        {
            _computer.Open();
            _opened = true;
        }
        catch
        {
            _opened = false;
        }
    }

    public double SampleCelsius()
    {
        if (!_opened) return 0;

        try
        {
            _visitor.Hardware.Clear();
            _computer.Accept(_visitor);
            var temperatures = new List<double>();
            foreach (var hardware in _visitor.Hardware)
            {
                if (hardware.HardwareType is not (HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel))
                    continue;

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Temperature
                        && sensor.Value is float value
                        && float.IsFinite(value)
                        && value is >= 0 and <= 130)
                    {
                        temperatures.Add(value);
                    }
                }
            }

            return temperatures.Count == 0 ? 0 : temperatures.Average();
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (!_opened) return;
        try { _computer.Close(); } catch { }
        _opened = false;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public List<IHardware> Hardware { get; } = [];

        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            Hardware.Add(hardware);
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
                subHardware.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
