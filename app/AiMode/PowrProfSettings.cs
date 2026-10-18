using System.Runtime.InteropServices;

namespace GHelper.AiMode
{
    /// <summary>
    /// IPowerSettings over PowrProf.dll. Declared here instead of reusing PowerNative,
    /// because PowerNative keeps its imports private and we don't want to touch upstream code.
    /// </summary>
    internal class PowrProfSettings : IPowerSettings
    {
        [DllImport("PowrProf.dll")]
        static extern uint PowerReadACValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            out uint AcValueIndex);

        [DllImport("PowrProf.dll")]
        static extern uint PowerReadDCValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            out uint DcValueIndex);

        [DllImport("PowrProf.dll")]
        static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            uint AcValueIndex);

        [DllImport("PowrProf.dll")]
        static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            uint DcValueIndex);

        [DllImport("PowrProf.dll")]
        static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("PowrProf.dll")]
        static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr hMem);

        public Guid? GetActiveScheme()
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) != 0 || ptr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStructure<Guid>(ptr);
            }
            finally
            {
                LocalFree(ptr);
            }
        }

        public bool SetActiveScheme(Guid scheme) => PowerSetActiveScheme(IntPtr.Zero, scheme) == 0;

        public uint? ReadAC(Guid scheme, Guid subgroup, Guid setting) =>
            PowerReadACValueIndex(IntPtr.Zero, scheme, subgroup, setting, out uint value) == 0 ? value : null;

        public uint? ReadDC(Guid scheme, Guid subgroup, Guid setting) =>
            PowerReadDCValueIndex(IntPtr.Zero, scheme, subgroup, setting, out uint value) == 0 ? value : null;

        public bool WriteAC(Guid scheme, Guid subgroup, Guid setting, uint value) =>
            PowerWriteACValueIndex(IntPtr.Zero, scheme, subgroup, setting, value) == 0;

        public bool WriteDC(Guid scheme, Guid subgroup, Guid setting, uint value) =>
            PowerWriteDCValueIndex(IntPtr.Zero, scheme, subgroup, setting, value) == 0;
    }
}
