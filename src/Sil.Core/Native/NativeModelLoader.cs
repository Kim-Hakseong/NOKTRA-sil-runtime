using System.Runtime.InteropServices;

namespace Sil.Core.Native;

/// <summary>
/// Loads native model libraries. Every failure mode — missing file, wrong ABI version, missing
/// export, malformed port table — is rejected here with a message naming the library, so a bad
/// model never reaches the cycle.
/// </summary>
public static class NativeModelLoader
{
    /// <summary>
    /// Loads a shared library and creates one model instance from it. The returned model owns
    /// the library and unloads it on dispose.
    /// </summary>
    /// <param name="libraryPath">Path to the shared library.</param>
    /// <param name="instanceName">Name the runtime gives this instance. ABI v1 has no name query.</param>
    public static NativeModel Load(string libraryPath, string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        string fullPath = Path.GetFullPath(libraryPath);
        if (!File.Exists(fullPath))
        {
            throw new SilNativeException($"No native model library at '{fullPath}'.");
        }

        nint handle;
        try
        {
            handle = NativeLibrary.Load(fullPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            throw new SilNativeException($"Could not load '{fullPath}': {ex.Message}", ex);
        }

        return new NativeModel(instanceName, fullPath, handle, ownsLibrary: true);
    }

    /// <summary>
    /// Creates an additional instance from an already-loaded library. Useful when a system runs
    /// several copies of the same model: the ABI requires instances to be independent.
    /// </summary>
    /// <param name="prototype">A model already loaded from the library.</param>
    /// <param name="instanceName">Name for the new instance.</param>
    public static NativeModel LoadAnotherInstance(NativeModel prototype, string instanceName)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        return Load(prototype.LibraryPath, instanceName);
    }

    /// <summary>
    /// Checks a library without keeping it loaded. Returns null when it is a valid v1 model,
    /// or the reason it was rejected.
    /// </summary>
    public static string? Validate(string libraryPath)
    {
        try
        {
            using NativeModel model = Load(libraryPath, "probe");
            return model.Ports.Count == 0 ? "The model declares no ports." : null;
        }
        catch (SilNativeException ex)
        {
            return ex.Message;
        }
    }
}
