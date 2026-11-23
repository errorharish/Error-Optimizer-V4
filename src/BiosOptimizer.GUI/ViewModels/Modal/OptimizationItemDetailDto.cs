using System.Windows.Media;

namespace BiosOptimizer.GUI.ViewModels.Modal
{
    public class OptimizationItemDetailDto
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "Kernel";
        public string Status { get; set; } = "APPLIED";
        public Brush StatusBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
        public string DetailNote { get; set; } = string.Empty;
        public string BeforeValue { get; set; } = "Default";
        public string AfterValue { get; set; } = "Optimized";
        public string VerificationDetail { get; set; } = "Kernel Verified";

        public string DisplayCategory => Category.ToUpperInvariant() switch
        {
            "CPU" or "KERNEL" or "SYSTEM" or "TIMER" or "MEMORY" => "Kernel & Microsecond Precision",
            "NETWORK" or "NET" or "TCP" or "NIC" or "BUFFER" => "Network Stack & Latency Buffer",
            "GPU" or "STORAGE" or "DISK" or "INTERRUPT" or "MSI" or "DEVICE" => "Interrupts & Peripheral MSI Steering",
            "PROTECTED" or "SKIPPED" or "SECURITY" => "Protected Hardware",
            _ => "Kernel & Microsecond Precision"
        };
    }
}
