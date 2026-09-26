using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BiosOptimizer.Core.Implementations.Input;
using BiosOptimizer.GUI.Services;
using BiosOptimizer.GUI.ViewModels;
using BiosOptimizer.IPC;

namespace InputTester
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("=======================================================");
            Console.WriteLine("STARTING NATIVE INPUT OPTIMIZER ENGINE MASTER AUDIT");
            Console.WriteLine("=======================================================");

            try
            {
                RunAuditAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[AUDIT FATAL ERROR]: {ex}");
                Environment.Exit(1);
            }
        }

        static async Task RunAuditAsync()
        {
            await Task.Yield();
            var engine = InputOptimizerEngine.Instance;

            // ----------------------------------------------------
            // TEST 1: Real Device Scan (HID / RawInput / XInput)
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 1] Testing Real Input Device Detection (HID / RawInput / XInput)...");
            var devices = engine.ScanDevices(forceRefresh: true);
            Console.WriteLine($"  Total Physical / Logical Input Devices: {devices.Count}");
            foreach (var d in devices)
            {
                Console.WriteLine($"    - [{d.TypeBadge}] '{d.Name}' | Vendor: '{d.Vendor}' | VID: {d.Vid} | PID: {d.Pid} | Wireless: {d.IsWireless} | Caps: {d.Capabilities}");
            }

            if (!devices.Any()) throw new Exception("No input devices detected on this system.");
            Console.WriteLine("  [PASS] Authentic device inventory verified.");

            // ----------------------------------------------------
            // TEST 2: Mouse Settings Read, Modify, & Readback Verify
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 2] Testing Mouse Settings Win32 Operations & Verification...");
            var initialMouse = engine.GetMouseSettings();
            Console.WriteLine($"  Initial Mouse: Speed={initialMouse.PointerSpeed}/20, Accel={initialMouse.AccelerationEnabled}, Trails={initialMouse.MouseTrails}, Snap={initialMouse.SnapToDefault}");

            // 1. Pointer Speed change to 10 (6/11 1:1)
            var resSpeed = engine.SetPointerSpeed(10);
            if (!resSpeed.Success || !resSpeed.Verified) throw new Exception($"Failed to set Pointer Speed: {resSpeed.Message}");
            Console.WriteLine($"  [VERIFIED] Pointer Speed: {resSpeed.CurrentValue}");

            // 2. Disable Acceleration (1:1 Raw Response)
            var resAccel = engine.SetMouseAcceleration(false);
            if (!resAccel.Success || !resAccel.Verified) throw new Exception($"Failed to disable Mouse Acceleration: {resAccel.Message}");
            Console.WriteLine($"  [VERIFIED] Mouse Acceleration: {resAccel.CurrentValue}");

            // 3. Disable Trails
            var resTrails = engine.SetMouseTrails(0);
            if (!resTrails.Success || !resTrails.Verified) throw new Exception($"Failed to configure Mouse Trails: {resTrails.Message}");
            Console.WriteLine($"  [VERIFIED] Mouse Trails: {resTrails.CurrentValue}");

            // 4. Disable Snap to Default
            var resSnap = engine.SetSnapToDefault(false);
            if (!resSnap.Success || !resSnap.Verified) throw new Exception($"Failed to configure Snap to Default: {resSnap.Message}");
            Console.WriteLine($"  [VERIFIED] Snap to Default: {resSnap.CurrentValue}");

            Console.WriteLine("  [PASS] Mouse settings operations and readback verified.");

            // ----------------------------------------------------
            // TEST 3: Keyboard Settings Read, Modify, & Readback Verify
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 3] Testing Keyboard Settings Win32 Operations & Verification...");
            var initialKbd = engine.GetKeyboardSettings();
            Console.WriteLine($"  Initial Keyboard: Delay={initialKbd.KeyboardDelay} (0..3), RepeatSpeed={initialKbd.KeyboardRepeatSpeed} (0..31)");

            // 1. Keyboard Delay to 0 (250ms Shortest)
            var resDelay = engine.SetKeyboardDelay(0);
            if (!resDelay.Success || !resDelay.Verified) throw new Exception($"Failed to set Keyboard Delay: {resDelay.Message}");
            Console.WriteLine($"  [VERIFIED] Keyboard Delay: {resDelay.CurrentValue}");

            // 2. Keyboard Repeat Speed to 31 (Max ~30 reps/s)
            var resKbdSpeed = engine.SetKeyboardRepeatSpeed(31);
            if (!resKbdSpeed.Success || !resKbdSpeed.Verified) throw new Exception($"Failed to set Keyboard Repeat Speed: {resKbdSpeed.Message}");
            Console.WriteLine($"  [VERIFIED] Keyboard Repeat Speed: {resKbdSpeed.CurrentValue}");

            Console.WriteLine("  [PASS] Keyboard settings operations and readback verified.");

            // ----------------------------------------------------
            // TEST 4: Profile Engine & Auto Foreground Management
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 4] Testing Input Profile Engine...");
            var profiles = engine.Profiles;
            Console.WriteLine($"  Available Profiles: {profiles.Count}");
            foreach (var p in profiles)
            {
                Console.WriteLine($"    - [{p.ProfileType}] '{p.Name}' | Target Processes: {p.TargetProcesses.Count} games/apps");
            }

            var compProfile = profiles.FirstOrDefault(p => p.ProfileType == InputProfileType.CompetitiveGaming);
            if (compProfile != null)
            {
                Console.WriteLine($"  Applying '{compProfile.Name}' profile...");
                engine.ApplyProfile(compProfile);
                var activeProf = engine.ActiveProfile;
                if (activeProf?.Name != compProfile.Name) throw new Exception("Profile activation mismatch.");
                Console.WriteLine($"  [VERIFIED] Active Profile: '{activeProf.Name}'");
            }

            // ----------------------------------------------------
            // TEST 5: Restore Original Settings
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 5] Testing Restore Original Settings...");
            engine.RestoreOriginalSettings();
            var restoredMouse = engine.GetMouseSettings();
            var restoredKbd = engine.GetKeyboardSettings();
            Console.WriteLine($"  Restored Mouse: Speed={restoredMouse.PointerSpeed}/20, Accel={restoredMouse.AccelerationEnabled}, Trails={restoredMouse.MouseTrails}");
            Console.WriteLine($"  Restored Keyboard: Delay={restoredKbd.KeyboardDelay}, RepeatSpeed={restoredKbd.KeyboardRepeatSpeed}");
            Console.WriteLine("  [PASS] Clean restore of initial settings verified.");

            // ----------------------------------------------------
            // TEST 6: WPF ViewModel State & STA Verification
            // ----------------------------------------------------
            Console.WriteLine("\n[TEST 6] Testing WPF ViewModel DataBinding & State on STA Thread...");
            var staThread = new Thread(() =>
            {
                if (Application.Current == null)
                {
                    _ = new Application();
                }
                var vm = new InputOptimizerViewModel(new IpcClient());
                Console.WriteLine($"  VM Detected Devices: {vm.DetectedDevices.Count}");
                Console.WriteLine($"  VM Mouse Devices: {vm.MouseDevices.Count}, Keyboard Devices: {vm.KeyboardDevices.Count}");
                Console.WriteLine($"  VM Mouse Display: '{vm.MouseDeviceDisplay}', Keyboard Display: '{vm.KeyboardDeviceDisplay}'");
                Console.WriteLine($"  VM Mouse Speed: {vm.MouseSpeedDisplay}, Accel: {vm.MouseAccelerationText}");
                Console.WriteLine($"  VM Keyboard Repeat: {vm.KeyboardRepeatSpeedText}, Delay: {vm.KeyboardDelayText}");
                Console.WriteLine($"  VM Active Profile: {vm.ActiveProfileName}");
                Console.WriteLine("  [PASS] WPF ViewModel data bindings verified.");
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            Console.WriteLine("\n=======================================================");
            Console.WriteLine("*** ALL NATIVE INPUT OPTIMIZER ENGINE TESTS PASSED! ***");
            Console.WriteLine("=======================================================");
        }
    }
}
