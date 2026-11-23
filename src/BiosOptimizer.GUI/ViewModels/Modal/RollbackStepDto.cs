using System.Windows.Media;

namespace BiosOptimizer.GUI.ViewModels.Modal
{
    public class RollbackStepDto
    {
        public int StepNumber { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending"; // "Completed", "Active", "Pending"
        public string IconGlyph { get; set; } = "○";
        public Brush IconBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        public Brush TextBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        public bool IsSpinning => Status == "Active";
    }
}
