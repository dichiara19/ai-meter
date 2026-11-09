using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AIMeter
{
    /// <summary>
    /// Chooses WPF's touch and pen stack before WPF starts. The pointer stack (WM_POINTER) is the one that brings taps
    /// and finger drags to WPF on .NET Framework 4.8, but it throws an ArgumentException on the first mouse capture
    /// when a pen or touch device reports the same HID property twice: WPF appends every property it knows, other
    /// than X, Y and pressure, to a StylusPointDescription, whose constructor refuses duplicates
    /// (PointerTabletDeviceInfo.TryInitializeSupportedStylusPointProperties in dotnet/wpf). On such a PC the app keeps
    /// the older WISP stack: mouse and keyboard work, touch behaves as it did before pointer support.
    /// A device plugged in after start-up is not checked; Program.Run logs the error it causes instead.
    /// </summary>
    internal static class TouchStack
    {
        private const string PointerSupportSwitch = "Switch.System.Windows.Input.Stylus.EnablePointerSupport";

        /// <summary>The device that made the app keep the WISP stack, or null; Program.Run writes it to errors.log.</summary>
        internal static string WispReason { get; private set; }

        /// <summary>Set the switch; it must run before WPF processes any input, because WPF reads it only once.</summary>
        internal static void Choose()
        {
            string device = null;
            try { device = DeviceWithDuplicateProperty(); }
            catch (EntryPointNotFoundException) { }   // No pointer API: the switch has no effect anyway.
            if (device != null) WispReason = "Pointer touch stack off: \"" + device + "\" reports a HID property twice.";
            AppContext.SetSwitch(PointerSupportSwitch, device == null);
        }

        /// <summary>
        /// True when a device's properties, as (usage page, usage) pairs, would make WPF build a description with
        /// duplicates. X, Y and tip pressure overwrite fixed slots, so only their repeats are harmless. Usages that WPF
        /// does not know are skipped by WPF but counted here: the check errs towards the WISP stack.
        /// </summary>
        internal static bool HasDuplicateProperty(IEnumerable<KeyValuePair<ushort, ushort>> usages)
        {
            return usages
                .Where(u => !IsFixedSlot(u.Key, u.Value))
                .GroupBy(u => u)
                .Any(g => g.Count() > 1);
        }

        private static bool IsFixedSlot(ushort page, ushort usage)
        {
            const ushort GenericDesktop = 0x01, Digitizer = 0x0D;
            return (page == GenericDesktop && (usage == 0x30 || usage == 0x31)) || (page == Digitizer && usage == 0x30);
        }

        private static string DeviceWithDuplicateProperty()
        {
            uint count = 0;
            if (!GetPointerDevices(ref count, null) || count == 0) return null;
            var devices = new POINTER_DEVICE_INFO[count];
            if (!GetPointerDevices(ref count, devices)) return null;

            foreach (var device in devices.Take((int)count))
            {
                uint propertyCount = 0;
                if (!GetPointerDeviceProperties(device.device, ref propertyCount, null) || propertyCount == 0) continue;
                var properties = new POINTER_DEVICE_PROPERTY[propertyCount];
                if (!GetPointerDeviceProperties(device.device, ref propertyCount, properties)) continue;

                var usages = properties.Take((int)propertyCount).Select(p => new KeyValuePair<ushort, ushort>(p.usagePageId, p.usageId));
                if (HasDuplicateProperty(usages)) return string.IsNullOrEmpty(device.productString) ? "unnamed device" : device.productString;
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct POINTER_DEVICE_INFO
        {
            public uint displayOrientation;
            public IntPtr device;
            public uint pointerDeviceType;
            public IntPtr monitor;
            public uint startingCursorId;
            public ushort maxActiveContacts;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string productString;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_DEVICE_PROPERTY
        {
            public int logicalMin, logicalMax, physicalMin, physicalMax;
            public uint unit, unitExponent;
            public ushort usagePageId, usageId;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPointerDevices(ref uint count, [Out] POINTER_DEVICE_INFO[] devices);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPointerDeviceProperties(IntPtr device, ref uint count, [Out] POINTER_DEVICE_PROPERTY[] properties);
    }
}
