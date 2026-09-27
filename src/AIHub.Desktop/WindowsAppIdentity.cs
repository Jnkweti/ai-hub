using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AIHub.Desktop
{
    // Also loaded by Install-Shortcut.ps1; installed shortcuts always use the production ID.
    public static class WindowsAppIdentity
    {
        public const string Id = "AIHub.Desktop";

        public static void SetProcess()
        {
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(GetProcessAppId()));
        }

        public static string GetProcessAppId()
        {
            var dataDirectory = Environment.GetEnvironmentVariable("AIHUB_DATA_DIR");
            if (string.IsNullOrEmpty(dataDirectory)) return Id;

            // Fixture windows must not join the installed app's pinned taskbar group.
            var normalizedDirectory = Path.GetFullPath(dataDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            using (var hash = SHA256.Create())
            {
                return Id + ".Isolated." + BitConverter.ToString(
                    hash.ComputeHash(Encoding.UTF8.GetBytes(normalizedDirectory))).Replace("-", "");
            }
        }

        public static void SetShortcut(string path)
        {
            var iid = typeof(IPropertyStore).GUID;
            IPropertyStore store;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out store));
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            var value = new PropVariant { Type = 31, Text = Marshal.StringToCoTaskMemUni(Id) };
            try
            {
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Text);
                Marshal.ReleaseComObject(store);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr context, uint flags, ref Guid iid, out IPropertyStore store);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid Format;
            public uint Id;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Text;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
    }
}
