using System;
using System.Collections.Generic;
using System.IO;
using AcDream.Core.Journal;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI;

public sealed class JournalPersistence(
    RuntimeJournalState journal,
    string directory,
    Action<string>? report = null)
{
    private readonly RuntimeJournalState _journal =
        journal ?? throw new ArgumentNullException(nameof(journal));

    private readonly string _directory = string.IsNullOrWhiteSpace(directory)
        ? throw new ArgumentException("A journal directory is required.", nameof(directory))
        : directory;

    private string? _characterName;

    public string? CurrentPath { get; private set; }

    public void Load(string characterName, string serverName = "acdream")
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return;

        _characterName = characterName;
        CurrentPath = Path.Combine(
            _directory, JournalFile.FileNameFor(serverName, characterName));

        string text;
        try
        {
            text = File.Exists(CurrentPath) ? File.ReadAllText(CurrentPath) : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report?.Invoke($"Problem loading journal: {e.Message}");
            _journal.Load([]);
            return;
        }

        JournalReadResult result = JournalFile.Read(text);
        if (result.Error is not null)
        {
            report?.Invoke(result.Error);
            _journal.Load([]);
            return;
        }

        _journal.Load(result.Pages);
    }

    public bool Save(DateTime now)
    {
        if (CurrentPath is null || _characterName is null)
            return false;
        if (!_journal.IsDirty)
            return false;

        IReadOnlyList<JournalPage> pages = _journal.CaptureForSave(now);
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(CurrentPath, JournalFile.Write(pages));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report?.Invoke($"Problem saving journal: {e.Message}");
            return false;
        }

        _journal.MarkSaved();
        return true;
    }

    public void Close(DateTime now)
    {
        Save(now);
        _characterName = null;
        CurrentPath = null;
    }
}
