using System;
using System.Collections.Generic;

namespace QuickNote.Core;

public enum NoteKind { Text, Image }

public sealed record Note
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public NoteKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string? ImageFile { get; init; }
    public string Label { get; init; } = "";
    public int Color { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record Preferences
{
    public bool AlwaysOnTop { get; init; } = true;
    public double? Left { get; init; }
    public double? Top { get; init; }
}

public sealed record Notebook
{
    public int Version { get; init; } = 1;
    public List<Note> Notes { get; init; } = new();
    public Preferences Preferences { get; init; } = new();
}

public sealed record LoadResult(Notebook Notebook, bool RecoveredFromBackup);
