using System.Diagnostics;
using Sil.Core.Native;
using Xunit;

namespace Sil.Core.Tests;

/// <summary>
/// Compiles the reference C models from <c>src/Sil.NativeSpec</c> once per test run and hands
/// out their shared-library paths.
/// </summary>
/// <remarks>
/// If no C compiler is available the fixture fails rather than skipping: the ABI contract is the
/// point of M5, and a verification that silently disappears is worse than no verification.
/// <c>-ffp-contract=off</c> is mandatory — it stops the compiler fusing multiply-add pairs, which
/// is what lets the native model agree with the managed integrator bit-for-bit.
/// </remarks>
public sealed class NativeModelFixture : IDisposable
{
    private readonly string _outputDirectory;

    public NativeModelFixture()
    {
        string repoRoot = FindRepositoryRoot();
        string specRoot = Path.Combine(repoRoot, "src", "Sil.NativeSpec");
        string includeDir = Path.Combine(specRoot, "include");

        Compiler = FindCompiler()
            ?? throw new InvalidOperationException(
                "No C compiler found on PATH (looked for cc, gcc, clang). The native ABI tests " +
                "compile src/Sil.NativeSpec and cannot run without one.");

        _outputDirectory = Path.Combine(Path.GetTempPath(), $"sil-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outputDirectory);

        FirstOrderLibrary = Compile(specRoot, includeDir, "sil_first_order");
        PiControllerLibrary = Compile(specRoot, includeDir, "sil_pi_controller");
    }

    /// <summary>The C compiler used.</summary>
    public string Compiler { get; }

    /// <summary>Path to the compiled first-order reference model.</summary>
    public string FirstOrderLibrary { get; }

    /// <summary>Path to the compiled PI controller.</summary>
    public string PiControllerLibrary { get; }

    /// <summary>A path inside the fixture's scratch directory that no library occupies.</summary>
    public string ScratchPath(string fileName) => Path.Combine(_outputDirectory, fileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A still-mapped library on some platforms; the temp directory is disposable anyway.
        }
    }

    private string Compile(string specRoot, string includeDir, string baseName)
    {
        string source = Path.Combine(specRoot, "src", baseName + ".c");
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Reference model source is missing: {source}", source);
        }

        string output = Path.Combine(_outputDirectory, NativeAbi.LibraryFileName(baseName));

        string[] arguments =
        [
            "-O2",
            "-std=c11",
            "-ffp-contract=off",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-shared",
            "-fPIC",
            "-I", includeDir,
            source,
            "-o", output,
        ];

        var psi = new ProcessStartInfo(Compiler)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start the compiler '{Compiler}'.");

        string stderr = process.StandardError.ReadToEnd();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Compiling {baseName}.c failed with exit code {process.ExitCode}.{Environment.NewLine}" +
                $"{stdout}{stderr}");
        }

        return output;
    }

    private static string? FindCompiler()
    {
        foreach (string candidate in new[] { "cc", "gcc", "clang" })
        {
            string? resolved = Which(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? Which(string command)
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [string.Empty];

        foreach (string directory in pathVariable.Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SilRuntime.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find SilRuntime.sln above '{AppContext.BaseDirectory}'.");
    }
}

/// <summary>Shares one compilation of the reference models across every native test class.</summary>
[CollectionDefinition(Name)]
public sealed class NativeModelCollection : ICollectionFixture<NativeModelFixture>
{
    public const string Name = "native-models";
}
