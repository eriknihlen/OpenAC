namespace AcDream.App.Rendering;

internal sealed class OrderedResourceTeardown(params Action[] stages)
{
    private readonly Action[] _stages = stages ?? throw new ArgumentNullException(nameof(stages));
    private int _nextStage;
    private bool _advancing;

    public bool IsComplete => _nextStage == _stages.Length;
    internal int NextStage => _nextStage;

    public void Advance()
    {
        if (_advancing || IsComplete)
            return;

        _advancing = true;
        try
        {
            while (_nextStage < _stages.Length)
            {
                _stages[_nextStage]();
                _nextStage++;
            }
        }
        finally
        {
            _advancing = false;
        }
    }
}
