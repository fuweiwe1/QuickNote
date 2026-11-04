using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickNote.Core;

namespace QuickNote;

public partial class MainWindow
{
    private async Task CheckMultipleNoteWindows(string output)
    {
        var a = notebook.Notes.First(note => note.Kind == NoteKind.Text);
        var b = notebook.Notes.First(note => note.Kind == NoteKind.Text && note.Id != a.Id);
        var image = notebook.Notes.First(note => note.Kind == NoteKind.Image);
        OpenEditor(a); OpenEditor(b); OpenEditor(image);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        SmokeChecks.Assert(noteWindows.Count == 3 && noteWindows.Values.All(window => window.IsVisible) && IsEnabled,
            "Multiple independent details did not open, or the rope was disabled");
        var first = noteWindows[a.Id];
        var second = noteWindows[b.Id];
        OpenEditor(a);
        SmokeChecks.Assert(noteWindows.Count == 3 && ReferenceEquals(first, noteWindows[a.Id]), "Reopening duplicated an editor");
        SmokeChecks.Assert(noteWindows[image.Id].FullImage.Source is not null, "Concurrent image detail failed");

        first.Editor.Text = "便签 A 独立修改";
        second.Editor.Text = "便签 B 独立修改";
        SaveCardPosition(a.Id, ClotheslineLayout.LeftForPosition(ActualWidth, 0.41));
        first.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var saved = store.Load().Notebook;
        SmokeChecks.Assert(saved.Notes.First(n => n.Id == a.Id).Text == "便签 A 独立修改" &&
            Math.Abs(saved.Notes.First(n => n.Id == a.Id).RopePosition!.Value - 0.41) < 0.001,
            "Editor save overwrote a moved card's latest position");
        SmokeChecks.Assert(first.IsVisible && second.IsVisible && saved.Notes.First(n => n.Id == b.Id).Text == b.Text,
            "Saving closed another window or saved another window's draft");
        var realSave = second.SaveAction;
        second.SaveAction = _ => false;
        second.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SmokeChecks.Assert(second.IsVisible && second.HasUnsavedChanges, "Save failure lost the draft");
        second.SaveAction = realSave;
        second.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SmokeChecks.Assert(store.Load().Notebook.Notes.First(n => n.Id == b.Id).Text == "便签 B 独立修改", "Second editor save failed");
        SmokeChecks.Snapshot(first, Path.Combine(output, "07-multiple-editor-a.png"));
        SmokeChecks.Snapshot(second, Path.Combine(output, "08-multiple-editor-b.png"));
        second.Close(); noteWindows[image.Id].Close();
        AdvancePointer(dockBounds.Left + 200, dockBounds.Top + dockBounds.Height + 100, Environment.TickCount64 + 10000);
        SmokeChecks.Assert(presented && first.IsVisible, "Closing one editor released another editor's dock hold");

        first.Editor.Text = "关闭前保存";
        first.CloseDecision = () => MessageBoxResult.Cancel;
        first.Close();
        SmokeChecks.Assert(noteWindows.ContainsKey(a.Id) && first.IsVisible, "Cancel discarded an edited card");
        first.CloseDecision = () => MessageBoxResult.Yes;
        first.Close();
        SmokeChecks.Assert(noteWindows.Count == 0 && store.Load().Notebook.Notes.First(n => n.Id == a.Id).Text == "关闭前保存",
            "Save on close did not persist or release the window");

        OpenBlank(); OpenBlank();
        SmokeChecks.Assert(noteWindows.Count == 2, "Multiple new draft windows did not open");
        var draft = noteWindows.First();
        draft.Value.Editor.Text = "新草稿首次保存";
        var count = notebook.Notes.Count;
        draft.Value.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        draft.Value.Editor.Text = "新草稿继续编辑";
        draft.Value.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SmokeChecks.Assert(notebook.Notes.Count == count + 1 && notebook.Notes.Single(n => n.Id == draft.Key).Text == "新草稿继续编辑",
            "Saving a draft more than once duplicated it");
        SmokeChecks.Assert(CloseNoteWindows(), "Clean drafts did not close");

        OpenEditor(b);
        var deleting = noteWindows[b.Id];
        deleting.Editor.Text = "取消删除时保留草稿";
        deleting.CloseDecision = () => MessageBoxResult.Cancel;
        Delete(NotesPanel.Children.Cast<NoteCard>().Single(card => card.Note.Id == b.Id));
        SmokeChecks.Assert(notebook.Notes.Any(n => n.Id == b.Id) && deleting.HasUnsavedChanges, "Canceled close deleted an edited note");
        deleting.CloseDecision = () => MessageBoxResult.No;
        Delete(NotesPanel.Children.Cast<NoteCard>().Single(card => card.Note.Id == b.Id));
        SmokeChecks.Assert(noteWindows.Count == 0 && store.Load().Notebook.Notes.All(n => n.Id != b.Id), "Delete left an open stale editor");

        OpenEditor(notebook.Notes.Single(n => n.Id == a.Id));
        var saveBeforeDelete = noteWindows[a.Id];
        saveBeforeDelete.Editor.Text = "删除前先保存修改";
        saveBeforeDelete.CloseDecision = () => MessageBoxResult.Yes;
        Delete(NotesPanel.Children.Cast<NoteCard>().Single(card => card.Note.Id == a.Id));
        SmokeChecks.Assert(noteWindows.Count == 0 && store.Load().Notebook.Notes.All(n => n.Id != a.Id),
            "Save-and-close during deletion used a stale card visual");

        AdvancePointer(dockBounds.Left + 200, dockBounds.Top + dockBounds.Height + 100, Environment.TickCount64 + 10000);
        SmokeChecks.Assert(!presented, "Last editor close did not release auto-hide");
        Reveal();
        await Task.Delay(320);
    }
}
