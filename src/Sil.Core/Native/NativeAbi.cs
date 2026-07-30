using System.Runtime.InteropServices;

namespace Sil.Core.Native;

/// <summary>
/// Constants of the native model ABI. These mirror <c>src/Sil.NativeSpec/include/sil_model.h</c>
/// and the frozen contract in <c>spec/native-abi.md</c>. Do not change v1 values.
/// </summary>
public static class NativeAbi
{
    /// <summary>ABI revision this loader speaks.</summary>
    public const int Version = 1;

    /// <summary>Buffer size of the port name field, including the NUL terminator.</summary>
    public const int NameMax = 64;

    /// <summary>Buffer size of the port unit field, including the NUL terminator.</summary>
    public const int UnitMax = 32;

    /// <summary>Status: success.</summary>
    public const int Ok = 0;

    /// <summary>Status: instance allocation failed.</summary>
    public const int ErrAlloc = 1;

    /// <summary>Status: a null pointer or otherwise invalid argument.</summary>
    public const int ErrInvalidArg = 2;

    /// <summary>Status: port index out of range.</summary>
    public const int ErrRange = 3;

    /// <summary>Status: call-order violation.</summary>
    public const int ErrState = 4;

    /// <summary>Port direction: the model reads it.</summary>
    public const int PortInput = 0;

    /// <summary>Port direction: the model writes it.</summary>
    public const int PortOutput = 1;

    /// <summary>Names of the eight required exports, in load order.</summary>
    public static readonly string[] RequiredExports =
    [
        "sil_abi_version",
        "sil_init",
        "sil_step",
        "sil_port_count",
        "sil_port_info",
        "sil_get",
        "sil_set",
        "sil_free",
    ];

    /// <summary>Turns a status code into a readable name.</summary>
    public static string DescribeStatus(int status) => status switch
    {
        Ok => "SIL_OK",
        ErrAlloc => "SIL_ERR_ALLOC",
        ErrInvalidArg => "SIL_ERR_INVALID_ARG",
        ErrRange => "SIL_ERR_RANGE",
        ErrState => "SIL_ERR_STATE",
        _ => $"unknown status {status}",
    };

    /// <summary>
    /// The platform's shared-library file name for a model base name, e.g. <c>first_order</c>
    /// becomes <c>libsil_first_order.dylib</c> on macOS.
    /// </summary>
    public static string LibraryFileName(string baseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);

        if (OperatingSystem.IsWindows())
        {
            return baseName + ".dll";
        }

        return "lib" + baseName + (OperatingSystem.IsMacOS() ? ".dylib" : ".so");
    }
}

/// <summary>
/// Blittable mirror of <c>sil_port_info_t</c>. The fixed-size char arrays keep string ownership
/// on the caller's side: nothing is allocated or freed across the boundary.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SilPortInfo
{
    public int Index;
    public int Direction;
    public fixed byte Name[NativeAbi.NameMax];
    public fixed byte Unit[NativeAbi.UnitMax];
}

/// <summary>Raised when a native model library breaks the ABI contract or reports a failure.</summary>
public sealed class SilNativeException : Exception
{
    public SilNativeException(string message)
        : base(message)
    {
    }

    public SilNativeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The ABI status code, when the failure came from a native call.</summary>
    public int Status { get; private init; } = NativeAbi.Ok;

    internal static SilNativeException FromStatus(string operation, int status)
        => new($"{operation} failed with {NativeAbi.DescribeStatus(status)}.") { Status = status };
}
