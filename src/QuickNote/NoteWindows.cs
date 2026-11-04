using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using QuickNote.Core;

namespace QuickNote;

public partial class MainWindow
{
    private readonly Dictionary<Guid, NoteWindow> noteWindows = new();
    private bool updating;

    private void OpenNoteWindow(Note note, bool isNew = false)
    {
        if (updating) return;
        if (noteWindows.TryGetValue(note.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        NoteWindow? window = null;
        var held = false;
        try
        {
            window = new NoteWindow(note, store) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual };
            var area = SystemParameters.WorkArea;
            window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, area.Width - 24));
            window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, area.Height - 24));
            var offset = noteWindows.Count % 6 * 26;
            window.Left = Math.Clamp(area.Left + Math.Max(0, (area.Width - window.Width) / 2) + offset,
                area.Left, Math.Max(area.Left, area.Right - window.Width));
            window.Top = Math.Clamp(area.Top + Math.Max(0, (area.Height - window.Height) / 2) + offset,
                area.Top, Math.Max(area.Top, area.Bottom - window.Height));
            var allowCreate = isNew;
            window.SaveAction = edited =>
            {
                var saved = SaveOpenNote(edited, allowCreate);
                if (saved) allowCreate = false;
                return saved;
            };
            noteWindows.Add(note.Id, window);
            dock.Hold(); held = true;
            window.Closed += (_, _) =>
            {
                noteWindows.Remove(note.Id);
                if (held) { held = false; dock.Release(Environment.TickCount64); }
            };
            window.Show();
        }
        catch (Exception ex) when (IsContentError(ex))
        {
            noteWindows.Remove(note.Id);
            if (held) { held = false; dock.Release(Environment.TickCount64); }
            window?.Close();
            NotifyError("暂时无法打开：" + FriendlyError(ex));
        }
    }

    private bool SaveOpenNote(Note edited, bool allowCreate)
    {
        var index = notebook.Notes.FindIndex(n => n.Id == edited.Id);
        if (index < 0) return allowCreate && AddNote(edited);
        var notes = notebook.Notes.ToList();
        // A card can be moved while its editor is open. Merge text into the latest note,
        // retaining its current rope position, layer, image metadata and creation date.
        notes[index] = notes[index] with { Text = edited.Text };
        if (!Commit(notebook with { Notes = notes })) return false;
        RenderNotes();
        return true;
    }

    private bool CloseNoteWindow(Guid id)
    {
        if (!noteWindows.TryGetValue(id, out var window)) return true;
        window.Close();
        return !noteWindows.ContainsKey(id);
    }

    private bool CloseNoteWindows()
    {
        foreach (var id in noteWindows.Keys.ToArray()) if (!CloseNoteWindow(id)) return false;
        return true;
    }
}
