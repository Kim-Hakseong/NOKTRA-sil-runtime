using System.Runtime.InteropServices;
using System.Text;
using Sil.Core.Models;

namespace Sil.Core.Native;

/// <summary>
/// An <see cref="IModel"/> backed by a compiled shared library that implements the SIL native
/// ABI v1 (spec/native-abi.md). This is the path that runs real compiled control code on the
/// same fixed cycle as the managed plant models.
/// </summary>
/// <remarks>
/// Entry points are resolved once at load time and called through unmanaged function pointers,
/// so a cycle costs one indirect call per port access and no marshalling.
/// </remarks>
public sealed unsafe class NativeModel : IModel
{
    private readonly nint _library;
    private readonly bool _ownsLibrary;
    private readonly PortDescriptor[] _ports;

    private readonly delegate* unmanaged<nint, double, int> _step;
    private readonly delegate* unmanaged<nint, int, double*, int> _get;
    private readonly delegate* unmanaged<nint, int, double, int> _set;
    private readonly delegate* unmanaged<nint, void> _free;
    private readonly delegate* unmanaged<nint*, int> _init;

    private nint _instance;
    private bool _disposed;

    internal NativeModel(string name, string libraryPath, nint library, bool ownsLibrary)
    {
        Name = name;
        LibraryPath = libraryPath;
        _library = library;
        _ownsLibrary = ownsLibrary;

        try
        {
            var abiVersion = (delegate* unmanaged<int>)Resolve("sil_abi_version");
            int version = abiVersion();
            if (version != NativeAbi.Version)
            {
                throw new SilNativeException(
                    $"'{libraryPath}' reports ABI version {version}; this runtime speaks version {NativeAbi.Version}.");
            }

            _init = (delegate* unmanaged<nint*, int>)Resolve("sil_init");
            _step = (delegate* unmanaged<nint, double, int>)Resolve("sil_step");
            var portCount = (delegate* unmanaged<nint, int*, int>)Resolve("sil_port_count");
            var portInfo = (delegate* unmanaged<nint, int, SilPortInfo*, int>)Resolve("sil_port_info");
            _get = (delegate* unmanaged<nint, int, double*, int>)Resolve("sil_get");
            _set = (delegate* unmanaged<nint, int, double, int>)Resolve("sil_set");
            _free = (delegate* unmanaged<nint, void>)Resolve("sil_free");

            _instance = CreateInstance();
            _ports = ReadPortTable(portCount, portInfo);
        }
        catch
        {
            ReleaseInstance();
            if (_ownsLibrary)
            {
                NativeLibrary.Free(_library);
            }

            throw;
        }
    }

    /// <summary>Path of the shared library this instance was loaded from.</summary>
    public string LibraryPath { get; }

    public string Name { get; }

    public IReadOnlyList<PortDescriptor> Ports => _ports;

    /// <summary>
    /// Frees the current native instance and creates a fresh one, which is how the ABI expresses
    /// "return to t=0": v1 has no reset entry point, and <c>sil_init</c> is defined to produce a
    /// t=0 instance.
    /// </summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ReleaseInstance();
        _instance = CreateInstance();
    }

    public void Step(double dt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!double.IsFinite(dt) || dt <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "Step size must be finite and positive.");
        }

        int status = _step(_instance, dt);
        if (status != NativeAbi.Ok)
        {
            throw SilNativeException.FromStatus($"sil_step on '{Name}'", status);
        }
    }

    public double GetPort(int portIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckPortIndex(portIndex);

        double value;
        int status = _get(_instance, portIndex, &value);
        if (status != NativeAbi.Ok)
        {
            throw SilNativeException.FromStatus($"sil_get({portIndex}) on '{Name}'", status);
        }

        return value;
    }

    public void SetPort(int portIndex, double value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckPortIndex(portIndex);

        int status = _set(_instance, portIndex, value);
        if (status != NativeAbi.Ok)
        {
            throw SilNativeException.FromStatus($"sil_set({portIndex}) on '{Name}'", status);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseInstance();

        if (_ownsLibrary)
        {
            NativeLibrary.Free(_library);
        }
    }

    private nint CreateInstance()
    {
        nint handle;
        int status = _init(&handle);
        if (status != NativeAbi.Ok)
        {
            throw SilNativeException.FromStatus($"sil_init on '{LibraryPath}'", status);
        }

        if (handle == nint.Zero)
        {
            throw new SilNativeException($"sil_init on '{LibraryPath}' returned SIL_OK but a null instance.");
        }

        return handle;
    }

    private void ReleaseInstance()
    {
        if (_instance != nint.Zero && _free != null)
        {
            _free(_instance);
            _instance = nint.Zero;
        }
    }

    private PortDescriptor[] ReadPortTable(
        delegate* unmanaged<nint, int*, int> portCount,
        delegate* unmanaged<nint, int, SilPortInfo*, int> portInfo)
    {
        int count;
        int status = portCount(_instance, &count);
        if (status != NativeAbi.Ok)
        {
            throw SilNativeException.FromStatus($"sil_port_count on '{Name}'", status);
        }

        if (count < 0)
        {
            throw new SilNativeException($"'{Name}' reports a negative port count ({count}).");
        }

        var ports = new PortDescriptor[count];
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            SilPortInfo info;
            status = portInfo(_instance, i, &info);
            if (status != NativeAbi.Ok)
            {
                throw SilNativeException.FromStatus($"sil_port_info({i}) on '{Name}'", status);
            }

            if (info.Index != i)
            {
                throw new SilNativeException(
                    $"'{Name}' port at position {i} declares index {info.Index}.");
            }

            PortDirection direction = info.Direction switch
            {
                NativeAbi.PortInput => PortDirection.Input,
                NativeAbi.PortOutput => PortDirection.Output,
                _ => throw new SilNativeException(
                    $"'{Name}' port {i} declares unknown direction {info.Direction}."),
            };

            string portName = ReadUtf8(info.Name, NativeAbi.NameMax);
            if (portName.Length == 0)
            {
                throw new SilNativeException($"'{Name}' port {i} has an empty name.");
            }

            if (!names.Add(portName))
            {
                throw new SilNativeException($"'{Name}' declares duplicate port name '{portName}'.");
            }

            ports[i] = new PortDescriptor(i, portName, direction, ReadUtf8(info.Unit, NativeAbi.UnitMax));
        }

        return ports;
    }

    private static string ReadUtf8(byte* buffer, int capacity)
    {
        int length = 0;
        while (length < capacity && buffer[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, length);
    }

    private void CheckPortIndex(int portIndex)
    {
        if ((uint)portIndex >= (uint)_ports.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(portIndex), portIndex, $"Model '{Name}' has {_ports.Length} ports.");
        }
    }

    private nint Resolve(string export)
    {
        if (!NativeLibrary.TryGetExport(_library, export, out nint address))
        {
            throw new SilNativeException($"'{LibraryPath}' does not export '{export}'.");
        }

        return address;
    }
}
