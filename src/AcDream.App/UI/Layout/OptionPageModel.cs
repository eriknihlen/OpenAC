using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.UI.Layout;

public interface IOptionRow
{
    bool Changed { get; }

    /// <summary><c>SaveCurrentValue</c>: <c>m_saved = m_current</c>. No live
    /// side effect — the value is already live (every mutator below applies
    /// immediately).</summary>
    void SaveCurrentValue();

    /// <summary><c>RestoreSavedValue</c>: <c>m_current = m_saved</c>, then
    /// applies the reverted value live.</summary>
    void RestoreSavedValue();

    /// <summary><c>RestoreDefaultValue</c>: <c>m_current = m_default</c>,
    /// then applies the default live.</summary>
    void RestoreDefaultValue();

    void AttachPageNotify(Action notify);
}

public sealed class BoolOptionRow : IOptionRow
{
    private readonly Action<bool>? _apply;
    private readonly Func<bool>? _read;
    private readonly Action<bool>? _refresh;
    private Action? _notifyPageOptionChanged;
    private bool _current;
    private bool _saved;
    private bool _default;

    public BoolOptionRow(
        bool initial,
        bool defaultValue,
        Action<bool>? apply = null,
        Func<bool>? read = null,
        Action<bool>? refresh = null)
    {
        _current = initial;
        _saved = initial;
        _default = defaultValue;
        _apply = apply;
        _read = read;
        _refresh = refresh;
    }

    public bool Current => _current;

    public bool Saved => _saved;

    public bool DefaultValue => _default;

    public bool Changed => _saved != _current;

    public void SetDefaultValue(bool value) => _default = value;

    public void SetCurrentValue(bool value)
    {
        _current = value;
        _apply?.Invoke(value);
        _notifyPageOptionChanged?.Invoke();
    }

    public void AttachPageNotify(Action notify) => _notifyPageOptionChanged = notify;

    public void SaveCurrentValue()
    {
        if (_read is not null)
        {
            _current = _read();
            _refresh?.Invoke(_current);
        }
        _saved = _current;
    }

    public void RestoreSavedValue()
    {
        _current = _saved;
        _apply?.Invoke(_current);
    }

    public void RestoreDefaultValue()
    {
        _current = _default;
        _apply?.Invoke(_current);
    }
}

public sealed class FloatOptionRow : IOptionRow
{
    private readonly Action<float>? _apply;
    private readonly Func<float>? _read;
    private readonly Action<float>? _refresh;
    private Action? _notifyPageOptionChanged;
    private float _current;
    private float _saved;
    private float _default;

    public FloatOptionRow(
        float initial,
        float defaultValue,
        Action<float>? apply = null,
        Func<float>? read = null,
        Action<float>? refresh = null)
    {
        _current = initial;
        _saved = initial;
        _default = defaultValue;
        _apply = apply;
        _read = read;
        _refresh = refresh;
    }

    public float Current => _current;

    public float Saved => _saved;

    public float DefaultValue => _default;

    public bool Changed => _saved != _current;

    public void SetDefaultValue(float value) => _default = value;

    public void SetCurrentValue(float value)
    {
        _current = value;
        _apply?.Invoke(value);
        _notifyPageOptionChanged?.Invoke();
    }

    public void RefreshFromLink(float value)
    {
        _current = value;
        _refresh?.Invoke(value);
        _notifyPageOptionChanged?.Invoke();
    }

    public void AttachPageNotify(Action notify) => _notifyPageOptionChanged = notify;

    public void SaveCurrentValue()
    {
        if (_read is not null)
        {
            _current = _read();
            _refresh?.Invoke(_current);
        }
        _saved = _current;
    }

    public void RestoreSavedValue()
    {
        _current = _saved;
        _apply?.Invoke(_current);
    }

    public void RestoreDefaultValue()
    {
        _current = _default;
        _apply?.Invoke(_current);
    }
}

public sealed class IntOptionRow : IOptionRow
{
    private readonly Action<int>? _apply;
    private readonly Func<int>? _read;
    private readonly Action<int>? _refresh;
    private Action? _notifyPageOptionChanged;
    private int _current;
    private int _saved;
    private int _default;

    public IntOptionRow(
        int initial,
        int defaultValue,
        Action<int>? apply = null,
        Func<int>? read = null,
        Action<int>? refresh = null)
    {
        _current = initial;
        _saved = initial;
        _default = defaultValue;
        _apply = apply;
        _read = read;
        _refresh = refresh;
    }

    public int Current => _current;

    public int Saved => _saved;

    /// <summary>The value Defaults restores.</summary>
    public int DefaultValue => _default;

    public bool Changed => _saved != _current;

    public void SetDefaultValue(int value) => _default = value;

    public void SetCurrentValue(int value)
    {
        _current = value;
        _apply?.Invoke(value);
        _notifyPageOptionChanged?.Invoke();
    }

    public void AttachPageNotify(Action notify) => _notifyPageOptionChanged = notify;

    public void SaveCurrentValue()
    {
        if (_read is not null)
        {
            _current = _read();
            _refresh?.Invoke(_current);
        }
        _saved = _current;
    }

    public void RestoreSavedValue()
    {
        _current = _saved;
        _apply?.Invoke(_current);
    }

    public void RestoreDefaultValue()
    {
        _current = _default;
        _apply?.Invoke(_current);
    }
}

public sealed class StringOptionRow : IOptionRow
{
    private readonly Action<string>? _apply;
    private readonly Func<string>? _read;
    private readonly Action<string>? _refresh;
    private Action? _notifyPageOptionChanged;
    private string _current;
    private string _saved;
    private string _default;

    public StringOptionRow(
        string initial,
        string defaultValue,
        Action<string>? apply = null,
        Func<string>? read = null,
        Action<string>? refresh = null)
    {
        _current = initial;
        _saved = initial;
        _default = defaultValue;
        _apply = apply;
        _read = read;
        _refresh = refresh;
    }

    public string Current => _current;
    public string Saved => _saved;
    public string DefaultValue => _default;
    public bool Changed => _saved != _current;

    public void SetDefaultValue(string value) => _default = value;

    public void SetCurrentValue(string value)
    {
        _current = value;
        _apply?.Invoke(value);
        _notifyPageOptionChanged?.Invoke();
    }

    public void AttachPageNotify(Action notify) => _notifyPageOptionChanged = notify;

    public void SaveCurrentValue()
    {
        if (_read is not null)
        {
            _current = _read();
            _refresh?.Invoke(_current);
        }
        _saved = _current;
    }

    public void RestoreSavedValue()
    {
        _current = _saved;
        _apply?.Invoke(_current);
    }

    public void RestoreDefaultValue()
    {
        _current = _default;
        _apply?.Invoke(_current);
    }
}

public sealed class BitfieldOptionRow : IOptionRow
{
    private readonly Action<ulong>? _apply;
    private readonly Func<ulong>? _read;
    private readonly Action<ulong>? _refresh;
    private Action? _notifyPageOptionChanged;
    private ulong _current;
    private ulong _saved;
    private ulong _default;

    public BitfieldOptionRow(
        ulong initial,
        ulong defaultValue,
        Action<ulong>? apply = null,
        Func<ulong>? read = null,
        Action<ulong>? refresh = null)
    {
        _current = initial;
        _saved = initial;
        _default = defaultValue;
        _apply = apply;
        _read = read;
        _refresh = refresh;
    }

    public ulong Current => _current;
    public ulong Saved => _saved;
    public ulong DefaultValue => _default;
    public bool Changed => _saved != _current;

    public void SetDefaultValue(ulong value) => _default = value;

    public void SetCurrentValue(ulong value)
    {
        _current = value;
        _apply?.Invoke(value);
        _notifyPageOptionChanged?.Invoke();
    }

    public void AttachPageNotify(Action notify) => _notifyPageOptionChanged = notify;

    public void SaveCurrentValue()
    {
        if (_read is not null)
        {
            _current = _read();
            _refresh?.Invoke(_current);
        }
        _saved = _current;
    }

    public void RestoreSavedValue()
    {
        _current = _saved;
        _apply?.Invoke(_current);
    }

    public void RestoreDefaultValue()
    {
        _current = _default;
        _apply?.Invoke(_current);
    }
}

public sealed class ActionKeyMapOptionRow : IOptionRow
{
    private readonly Action<IReadOnlyList<KeyChord>>? _apply;
    private Action? _notifyPageOptionChanged;
    private IReadOnlyList<KeyChord> _current;
    private IReadOnlyList<KeyChord> _saved;
    private IReadOnlyList<KeyChord> _default;

    public ActionKeyMapOptionRow(
        IReadOnlyList<KeyChord> initial,
        IReadOnlyList<KeyChord> defaultValue,
        Action<IReadOnlyList<KeyChord>>? apply = null)
    {
        _current = initial;
        _saved = initial;
        _default = defaultValue;
        _apply = apply;
    }

    public IReadOnlyList<KeyChord> Current => _current;

    public IReadOnlyList<KeyChord> Saved => _saved;

    /// <summary>The DAT master-map default slot list Reset-to-Defaults restores.</summary>
    public IReadOnlyList<KeyChord> DefaultValue => _default;

    public bool Changed => !_current.SequenceEqual(_saved);

    public void SetDefaultValue(IReadOnlyList<KeyChord> value) => _default = value;

    public void SetCurrentValue(IReadOnlyList<KeyChord> value)
    {
        _current = value;
        _apply?.Invoke(value);
        _notifyPageOptionChanged?.Invoke();
    }

    public void AttachPageNotify(Action notify) => _notifyPageOptionChanged = notify;

    public void SaveCurrentValue() => _saved = _current;

    public void ReloadCurrentAndSaved(IReadOnlyList<KeyChord> value)
    {
        _current = value;
        _saved = value;
        _notifyPageOptionChanged?.Invoke();
    }

    public void RestoreSavedValue()
    {
        _current = _saved;
        _apply?.Invoke(_current);
    }

    public void RestoreDefaultValue()
    {
        _current = _default;
        _apply?.Invoke(_current);
    }
}

public sealed class OptionPage
{
    private readonly List<IOptionRow> _rows = new();

    public IReadOnlyList<IOptionRow> Rows => _rows;

    public Action? AfterApply { get; set; }

    public Action? OnOptionChanged { get; set; }

    public void Register(IOptionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.AttachPageNotify(() => OnOptionChanged?.Invoke());
        _rows.Add(row);
    }

    public void RemoveTail(int retainedRowCount)
    {
        if (retainedRowCount < 0 || retainedRowCount > _rows.Count)
            throw new ArgumentOutOfRangeException(nameof(retainedRowCount));
        for (int i = _rows.Count - 1; i >= retainedRowCount; i--)
        {
            _rows[i].AttachPageNotify(static () => { });
            _rows.RemoveAt(i);
        }
        OnOptionChanged?.Invoke();
    }

    public bool Changed => _rows.Any(static row => row.Changed);

    public void Apply()
    {
        foreach (IOptionRow row in _rows.ToArray())
            if (_rows.Contains(row))
                row.SaveCurrentValue();
        AfterApply?.Invoke();
        OnOptionChanged?.Invoke();
    }

    public void Reset()
    {
        foreach (IOptionRow row in _rows.Where(static row => row.Changed).ToArray())
            if (_rows.Contains(row))
                row.RestoreSavedValue();
        OnOptionChanged?.Invoke();
    }

    public void Defaults()
    {
        foreach (IOptionRow row in _rows.ToArray())
            if (_rows.Contains(row))
                row.RestoreDefaultValue();
        OnOptionChanged?.Invoke();
    }

    public void OnShown() => Apply();

    public void ReloadFromLive()
    {
        foreach (IOptionRow row in _rows.ToArray())
            if (_rows.Contains(row))
                row.SaveCurrentValue();
        OnOptionChanged?.Invoke();
    }

    public void OnHidden() => Reset();
}
