using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace LibreGuard.Common.Windows;

internal static class ShellLinkUtility
{
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);
    private static readonly PropertyKey ToastActivatorClsidKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        26);

    public static void CreateOrUpdateShortcut(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string iconPath,
        string appUserModelId,
        Guid? toastActivatorClsid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);

        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellLink = (IShellLinkW)new ShellLink();
        var persistFile = (IPersistFile)shellLink;

        if (File.Exists(shortcutPath))
            persistFile.Load(shortcutPath, 0);

        shellLink.SetPath(targetPath);
        shellLink.SetWorkingDirectory(workingDirectory);

        if (!string.IsNullOrWhiteSpace(iconPath))
            shellLink.SetIconLocation(iconPath, 0);

        var propertyStore = (IPropertyStore)shellLink;
        var appIdKey = AppUserModelIdKey;
        var appIdValue = PropVariant.FromString(appUserModelId);
        PropVariant? activatorValue = null;
        try
        {
            propertyStore.SetValue(ref appIdKey, ref appIdValue);

            if (toastActivatorClsid.HasValue)
            {
                var activatorKey = ToastActivatorClsidKey;
                activatorValue = PropVariant.FromGuid(toastActivatorClsid.Value);
                var activatorValueRef = activatorValue.Value;
                propertyStore.SetValue(ref activatorKey, ref activatorValueRef);
            }

            propertyStore.Commit();
        }
        finally
        {
            appIdValue.Dispose();
            if (activatorValue.HasValue)
            {
                var value = activatorValue.Value;
                value.Dispose();
            }
        }

        persistFile.Save(shortcutPath, true);
    }

    public static string? ReadAppUserModelId(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
            return null;

        var shellLink = (IShellLinkW)new ShellLink();
        var persistFile = (IPersistFile)shellLink;
        persistFile.Load(shortcutPath, 0);

        var propertyStore = (IPropertyStore)shellLink;
        var propertyKey = AppUserModelIdKey;
        propertyStore.GetValue(ref propertyKey, out var value);
        try
        {
            return value.GetString();
        }
        finally
        {
            value.Dispose();
        }
    }

    public static Guid? ReadToastActivatorClsid(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
            return null;

        var shellLink = (IShellLinkW)new ShellLink();
        var persistFile = (IPersistFile)shellLink;
        persistFile.Load(shortcutPath, 0);

        var propertyStore = (IPropertyStore)shellLink;
        var propertyKey = ToastActivatorClsidKey;
        propertyStore.GetValue(ref propertyKey, out var value);
        try
        {
            return value.GetGuid();
        }
        finally
        {
            value.Dispose();
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out] char[] pszFile, int cchMaxPath, out WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out] char[] pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out] char[] pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out] char[] pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out] char[] pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant propvar);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)]
        private ushort _valueType;

        [FieldOffset(8)]
        private IntPtr _pointerValue;

        public static PropVariant FromString(string value)
        {
            return new PropVariant
            {
                _valueType = 31,
                _pointerValue = Marshal.StringToCoTaskMemUni(value)
            };
        }

        public static PropVariant FromGuid(Guid value)
        {
            var guidPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(value, guidPointer, fDeleteOld: false);

            return new PropVariant
            {
                _valueType = 72,
                _pointerValue = guidPointer
            };
        }

        public string? GetString()
        {
            if (_pointerValue == IntPtr.Zero)
                return null;

            return _valueType switch
            {
                31 => Marshal.PtrToStringUni(_pointerValue),
                8 => Marshal.PtrToStringBSTR(_pointerValue),
                _ => null
            };
        }

        public Guid? GetGuid()
        {
            if (_pointerValue == IntPtr.Zero || _valueType != 72)
                return null;

            return Marshal.PtrToStructure<Guid>(_pointerValue);
        }

        public void Dispose()
        {
            PropVariantClear(ref this);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);
    }
}
