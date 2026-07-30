namespace Sil.Core.Engine;

/// <summary>
/// The state passed to every task on every cycle. Simulation time is authoritative and is
/// always derived from <see cref="StepIndex"/>, never accumulated.
/// </summary>
/// <param name="StepIndex">Zero-based index of the cycle about to execute.</param>
/// <param name="Time">Simulation time in seconds at the start of the cycle.</param>
/// <param name="Dt">Fixed step size in seconds.</param>
public readonly record struct StepContext(long StepIndex, double Time, double Dt);

/// <summary>
/// A unit of work executed once per fixed cycle. The engine calls tasks in list order, so the
/// order of the list expresses the data-flow of a cycle
/// (stimulus -&gt; inputs -&gt; model step -&gt; outputs -&gt; logging).
/// </summary>
public interface ISimTask
{
    /// <summary>Stable identifier used in diagnostics and logs.</summary>
    string Name { get; }

    /// <summary>
    /// Brings the task to its t=0 condition. Called by <c>SimEngine.Reset</c> before any step.
    /// </summary>
    void Initialize(in StepContext ctx);

    /// <summary>Executes one cycle.</summary>
    void Step(in StepContext ctx);
}

/// <summary>A task built from delegates; convenient for composition and tests.</summary>
public sealed class DelegateSimTask : ISimTask
{
    private readonly Action<StepContext> _step;
    private readonly Action<StepContext>? _initialize;

    public DelegateSimTask(string name, Action<StepContext> step, Action<StepContext>? initialize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(step);

        Name = name;
        _step = step;
        _initialize = initialize;
    }

    public string Name { get; }

    public void Initialize(in StepContext ctx) => _initialize?.Invoke(ctx);

    public void Step(in StepContext ctx) => _step(ctx);
}
