using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed record UsbConnectionInfo(
    bool Available,
    UsbLinkSpeed LinkSpeed,
    bool DeviceSuperSpeedCapable,
    bool DeviceSuperSpeedPlusCapable,
    bool LogicalPortSupportsUsb3,
    int PortNumber,
    string? Detail = null);

public static class UsbConnectionDiagnostics
{
    private const uint CrSuccess = 0;
    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlUsbGetNodeConnectionInformationEx = 0x220448;
    private const uint IoctlUsbGetNodeConnectionInformationExV2 = 0x22045C;
    private const int ConnectionInformationExBytes = 36;
    private const int ConnectionInformationExV2Bytes = 16;

    private static readonly Guid UsbDeviceInterface =
        new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private static readonly Guid UsbHubInterface =
        new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
    private static readonly DevPropKey DeviceAddressKey = new(
        new("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        30);

    public static UsbConnectionInfo Query(string adbSerial)
    {
        if (string.IsNullOrWhiteSpace(adbSerial) ||
            adbSerial.Contains("._adb-tls-", StringComparison.OrdinalIgnoreCase) ||
            adbSerial.Contains(':'))
            return new(false, UsbLinkSpeed.Unknown, false, false, false, 0,
                "The selected ADB transport is wireless.");

        try
        {
            if (!TryFindUsbDevice(adbSerial, out var deviceNode))
                return new(false, UsbLinkSpeed.Unknown, false, false, false, 0,
                    "Windows could not match the ADB serial to a USB device.");
            if (!TryReadUInt32Property(deviceNode, DeviceAddressKey, out var port) ||
                port is 0 or > 255)
                return new(false, UsbLinkSpeed.Unknown, false, false, false, 0,
                    "Windows did not report the USB hub port number.");
            if (CM_Get_Parent(out var hubNode, deviceNode, 0) != CrSuccess)
                return new(false, UsbLinkSpeed.Unknown, false, false, false, (int)port,
                    "Windows did not report the parent USB hub.");

            var hubId = GetDeviceId(hubNode);
            var hubPath = GetInterfacePaths(UsbHubInterface, hubId).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(hubPath))
                return new(false, UsbLinkSpeed.Unknown, false, false, false, (int)port,
                    "Windows did not expose the parent USB hub interface.");

            using var hub = CreateFile(
                hubPath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (hub.IsInvalid)
                return new(false, UsbLinkSpeed.Unknown, false, false, false, (int)port,
                    $"Could not open the USB hub (Win32 {Marshal.GetLastWin32Error()}).");

            var legacySpeed = QueryLegacySpeed(hub, port);
            var v2 = QuerySuperSpeed(hub, port);
            var speed = v2.OperatingSuperSpeedPlus
                ? UsbLinkSpeed.SuperSpeedPlus
                : v2.OperatingSuperSpeed
                    ? UsbLinkSpeed.SuperSpeed
                    : legacySpeed;
            return new(
                true,
                speed,
                v2.SuperSpeedCapable || v2.SuperSpeedPlusCapable,
                v2.SuperSpeedPlusCapable,
                v2.PortSupportsUsb3,
                (int)port);
        }
        catch (Exception error)
        {
            return new(false, UsbLinkSpeed.Unknown, false, false, false, 0, error.Message);
        }
    }

    private static UsbLinkSpeed QueryLegacySpeed(SafeFileHandle hub, uint port)
    {
        var buffer = new byte[ConnectionInformationExBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, port);
        if (!DeviceIoControl(
                hub,
                IoctlUsbGetNodeConnectionInformationEx,
                buffer,
                (uint)buffer.Length,
                buffer,
                (uint)buffer.Length,
                out _,
                IntPtr.Zero))
            return UsbLinkSpeed.Unknown;

        return buffer[23] switch
        {
            0 => UsbLinkSpeed.LowSpeed,
            1 => UsbLinkSpeed.FullSpeed,
            2 => UsbLinkSpeed.HighSpeed,
            3 => UsbLinkSpeed.SuperSpeed,
            _ => UsbLinkSpeed.Unknown
        };
    }

    private static SuperSpeedInfo QuerySuperSpeed(SafeFileHandle hub, uint port)
    {
        var buffer = new byte[ConnectionInformationExV2Bytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), port);
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(4, 4),
            ConnectionInformationExV2Bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), 0x04);
        if (!DeviceIoControl(
                hub,
                IoctlUsbGetNodeConnectionInformationExV2,
                buffer,
                (uint)buffer.Length,
                buffer,
                (uint)buffer.Length,
                out _,
                IntPtr.Zero))
            return default;

        var protocols = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12, 4));
        return new(
            OperatingSuperSpeed: (flags & 0x01) != 0,
            SuperSpeedCapable: (flags & 0x02) != 0,
            OperatingSuperSpeedPlus: (flags & 0x04) != 0,
            SuperSpeedPlusCapable: (flags & 0x08) != 0,
            PortSupportsUsb3: (protocols & 0x04) != 0);
    }

    private static bool TryFindUsbDevice(string serial, out uint deviceNode)
    {
        foreach (var path in GetInterfacePaths(UsbDeviceInterface, null))
        {
            var deviceId = InterfacePathToDeviceId(path);
            if (deviceId is null ||
                CM_Locate_DevNodeW(out var current, deviceId, 0) != CrSuccess)
                continue;

            for (var depth = 0; depth < 8; depth++)
            {
                var currentId = GetDeviceId(current);
                var separator = currentId.LastIndexOf('\\');
                var instance = separator >= 0 ? currentId[(separator + 1)..] : currentId;
                if (instance.Equals(serial, StringComparison.OrdinalIgnoreCase))
                {
                    deviceNode = current;
                    return true;
                }
                if (CM_Get_Parent(out current, current, 0) != CrSuccess)
                    break;
            }
        }

        deviceNode = 0;
        return false;
    }

    private static IReadOnlyList<string> GetInterfacePaths(Guid interfaceClass, string? deviceId)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(
                    out var length,
                    ref interfaceClass,
                    deviceId,
                    CmGetDeviceInterfaceListPresent) != CrSuccess ||
                length <= 1)
                return [];
            var buffer = new char[length];
            var result = CM_Get_Device_Interface_ListW(
                ref interfaceClass,
                deviceId,
                buffer,
                length,
                CmGetDeviceInterfaceListPresent);
            if (result == CrSuccess)
                return new string(buffer)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return [];
    }

    private static string? InterfacePathToDeviceId(string path)
    {
        var value = path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path[4..]
            : path;
        var classMarker = value.LastIndexOf("#{", StringComparison.Ordinal);
        if (classMarker < 0)
            return null;
        return value[..classMarker].Replace('#', '\\');
    }

    private static string GetDeviceId(uint deviceNode)
    {
        var buffer = new StringBuilder(512);
        var result = CM_Get_Device_IDW(deviceNode, buffer, buffer.Capacity, 0);
        if (result != CrSuccess)
            throw new InvalidOperationException($"CM_Get_Device_ID failed: 0x{result:X}.");
        return buffer.ToString();
    }

    private static bool TryReadUInt32Property(
        uint deviceNode,
        DevPropKey key,
        out uint value)
    {
        var buffer = new byte[4];
        var size = (uint)buffer.Length;
        var result = CM_Get_DevNode_PropertyW(
            deviceNode,
            ref key,
            out var propertyType,
            buffer,
            ref size,
            0);
        if (result != CrSuccess || size != 4 || (propertyType & 0x0FFF) != 7)
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    private readonly record struct SuperSpeedInfo(
        bool OperatingSuperSpeed,
        bool SuperSpeedCapable,
        bool OperatingSuperSpeedPlus,
        bool SuperSpeedPlusCapable,
        bool PortSupportsUsb3);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(
        out uint deviceNode,
        string deviceId,
        uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(
        out uint parentDeviceNode,
        uint deviceNode,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(
        uint deviceNode,
        StringBuilder buffer,
        int bufferLength,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(
        out uint length,
        ref Guid interfaceClass,
        string? deviceId,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(
        ref Guid interfaceClass,
        string? deviceId,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint deviceNode,
        ref DevPropKey propertyKey,
        out uint propertyType,
        [Out] byte[] propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        [In, Out] byte[] inputBuffer,
        uint inputBufferSize,
        [In, Out] byte[] outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
