using System.Runtime.ExceptionServices;

namespace AcDream.App.Rendering;

internal sealed class RestoredTextureBindingMutation
{
    private uint? _pendingRestore;
    private bool _operationActive;

    internal bool HasPendingRestore => _pendingRestore.HasValue;

    public void Execute(
        Func<uint> readBinding,
        Action<uint> bind,
        uint mutationBinding,
        Action mutation)
    {
        ArgumentNullException.ThrowIfNull(readBinding);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(mutation);
        if (_operationActive)
            throw new InvalidOperationException("A texture-binding mutation is already active.");

        _operationActive = true;
        try
        {
            ExecuteCore(readBinding, bind, mutationBinding, mutation);
        }
        finally
        {
            _operationActive = false;
        }
    }

    private void ExecuteCore(
        Func<uint> readBinding,
        Action<uint> bind,
        uint mutationBinding,
        Action mutation)
    {
        if (_pendingRestore is uint pending)
        {
            bind(pending);
            _pendingRestore = null;
        }

        uint previousBinding = readBinding();
        _pendingRestore = previousBinding;
        Exception? mutationFailure = null;
        try
        {
            bind(mutationBinding);
            mutation();
        }
        catch (Exception failure)
        {
            mutationFailure = failure;
        }

        try
        {
            bind(previousBinding);
            _pendingRestore = null;
        }
        catch (Exception restoreFailure)
        {
            if (mutationFailure is not null)
                throw new AggregateException(
                    "Texture mutation and binding restoration both failed.",
                    mutationFailure,
                    restoreFailure);
            throw;
        }

        if (mutationFailure is not null)
            ExceptionDispatchInfo.Capture(mutationFailure).Throw();
    }
}
