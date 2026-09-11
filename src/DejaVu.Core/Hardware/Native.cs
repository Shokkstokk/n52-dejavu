using System.Runtime.InteropServices;

namespace DejaVu.Core.Hardware;

/// <summary>
/// SetupAPI, kernel32 and WinUSB bindings used by <see cref="N52UsbDevice"/>.
/// Nothing here is n52-specific: it is the plumbing for finding a device interface by
/// GUID and talking to it through WinUSB.
/// </summary>
internal static class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint ShareRead = 0x00000001;
    public const uint ShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FlagOverlapped = 0x40000000;

    public const int ErrorSemTimeout = 121;
    public const int ErrorOperationAborted = 995;

    /// <summary>WinUSB pipe policy: milliseconds before a read gives up.</summary>
    public const uint PipeTransferTimeout = 0x03;

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SetupPacket
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClass;
        public uint Flags;
        public nint Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateFile(string name, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_Initialize(nint device, out nint winUsb);

    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_Free(nint winUsb);

    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_ControlTransfer(nint winUsb, SetupPacket setup,
        byte[] buffer, uint length, out uint transferred, nint overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_ReadPipe(nint winUsb, byte pipeId, byte[] buffer,
        uint length, out uint transferred, nint overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_SetPipePolicy(nint winUsb, byte pipeId, uint policyType,
        uint valueLength, ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_QueryInterfaceSettings(nint winUsb, byte alternate,
        out InterfaceDescriptor descriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    public static extern bool WinUsb_QueryPipe(nint winUsb, byte alternate, byte index,
        out PipeInformation pipe);

    [StructLayout(LayoutKind.Sequential)]
    public struct InterfaceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public byte InterfaceNumber;
        public byte AlternateSetting;
        public byte NumEndpoints;
        public byte InterfaceClass;
        public byte InterfaceSubClass;
        public byte InterfaceProtocol;
        public byte Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    /// <summary>USBD_PIPE_TYPE value for an interrupt pipe.</summary>
    public const int PipeTypeInterrupt = 3;

    /// <summary>Set on a pipe id when the endpoint carries data device-to-host.</summary>
    public const byte PipeDirectionIn = 0x80;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator,
        nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint deviceInfo,
        ref Guid interfaceClass, uint index, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set,
        ref DeviceInterfaceData data, nint detail, uint detailSize, ref uint required, nint deviceInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    /// <summary>
    /// Returns the interface path for the first present device exposing the given
    /// interface GUID, or null when there is none.
    /// </summary>
    public static string? FindInterfacePath(Guid interfaceGuid)
    {
        var set = SetupDiGetClassDevs(ref interfaceGuid, nint.Zero, nint.Zero,
            DigcfPresent | DigcfDeviceInterface);

        if (set == -1) return null;

        try
        {
            var data = new DeviceInterfaceData();
            data.Size = (uint)Marshal.SizeOf(data);

            if (!SetupDiEnumDeviceInterfaces(set, nint.Zero, ref interfaceGuid, 0, ref data))
                return null;

            uint required = 0;
            SetupDiGetDeviceInterfaceDetail(set, ref data, nint.Zero, 0, ref required, nint.Zero);
            if (required == 0) return null;

            var buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8 on 64-bit: the fixed
                // header, not the size of the allocation.
                Marshal.WriteInt32(buffer, 8);

                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, ref required, nint.Zero))
                    return null;

                return Marshal.PtrToStringUni(buffer + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
