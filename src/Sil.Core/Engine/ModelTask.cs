using Sil.Core.Models;

namespace Sil.Core.Engine;

/// <summary>Runs one <see cref="IModel"/> as a cycle task.</summary>
public sealed class ModelTask : ISimTask
{
    private readonly bool _initializeOnReset;

    /// <param name="model">The model to step.</param>
    /// <param name="initializeOnReset">
    /// When true, an engine reset re-initializes the model. A <c>SilSystem</c> sets this false
    /// because it initializes models itself, in an order that also re-applies the wiring.
    /// </param>
    public ModelTask(IModel model, bool initializeOnReset = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        _initializeOnReset = initializeOnReset;
    }

    public IModel Model { get; }

    public string Name => Model.Name;

    public void Initialize(in StepContext ctx)
    {
        if (_initializeOnReset)
        {
            Model.Initialize();
        }
    }

    public void Step(in StepContext ctx) => Model.Step(ctx.Dt);
}
