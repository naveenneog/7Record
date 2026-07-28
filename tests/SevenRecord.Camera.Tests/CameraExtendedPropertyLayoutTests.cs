using System.Runtime.InteropServices;

namespace SevenRecord.Camera.Tests;

[TestClass]
public sealed class CameraExtendedPropertyLayoutTests
{
    [TestMethod]
    public void WindowsHeaderUsesDocumentedNativeSize()
    {
        Assert.AreEqual(32, Marshal.SizeOf<CameraHeaderShape>());
        Assert.AreEqual(40, Marshal.SizeOf<CameraPayloadShape>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CameraHeaderShape
    {
        public uint Version;
        public uint PinId;
        public uint Size;
        public uint Result;
        public ulong Flags;
        public ulong Capability;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CameraPayloadShape
    {
        public CameraHeaderShape Header;
        public ulong Value;
    }
}
