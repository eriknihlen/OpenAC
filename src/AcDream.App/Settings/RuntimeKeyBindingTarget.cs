using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Settings;

internal interface IRuntimeKeyBindingTarget
{
    void Apply(KeyBindings bindings);
}

internal sealed class RuntimeKeyBindingTarget : IRuntimeKeyBindingTarget
{
    private readonly InputDispatcher _dispatcher;
    private readonly string _path;
    private readonly Action<string> _log;

    public RuntimeKeyBindingTarget(
        InputDispatcher dispatcher,
        string path,
        Action<string>? log = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _log = log ?? Console.WriteLine;
    }

    public void Apply(KeyBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _dispatcher.SetBindings(bindings);
        try
        {
            bindings.SaveToFile(_path);
            _log($"keybinds: saved to {_path}");
        }
        catch (Exception failure)
        {
            _log($"keybinds: save failed: {failure.Message}");
        }
    }
}
