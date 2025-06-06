using Avalonia.Controls;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using System.Text;
using Avalonia.Threading;
using System.Diagnostics;
using System.Linq;
using Avalonia.Interactivity;

namespace PO_WIZ_noatatnik.Views
{
    public class Note
    {
        public string Text { get; set; } = "";
        public List<string> Attachments { get; set; } = new();

        public override string ToString()
        {
            return Attachments.Count > 0
                ? $"{Text} (za³¹czniki: {Attachments.Count})"
                : Text;
        }
    }

    public partial class MainWindow : Window
    {
        private string sessionTitle = "";
        private DateTime sessionCreatedAt;
        private List<Note> notes = new();
        private List<string> currentAttachments = new();

        private const string DbPath = "notatnik.db";
        private int selectedNoteIndex = -1;

        private class SessionInfo
        {
            public long Id { get; set; }
            public string Title { get; set; } = "";
            public override string ToString() => Title;
        }

        public MainWindow()
        {
            InitializeComponent();
            EnsureDatabase();
            LoadAllSessions();
        }

        private void EnsureDatabase()
        {
            if (!File.Exists(DbPath))
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();

                var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Sessions (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Title TEXT,
                        CreatedAt TEXT
                    );

                    CREATE TABLE IF NOT EXISTS Notes (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SessionId INTEGER,
                        Text TEXT
                    );

                    CREATE TABLE IF NOT EXISTS Attachments (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        NoteId INTEGER,
                        FilePath TEXT
                    );";
                cmd.ExecuteNonQuery();
            }
        }

        private void LoadAllSessions()
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Title FROM Sessions ORDER BY Id DESC;";

            using var reader = cmd.ExecuteReader();
            var sessions = new List<SessionInfo>();

            while (reader.Read())
            {
                sessions.Add(new SessionInfo
                {
                    Id = Convert.ToInt64(reader["Id"]),
                    Title = reader["Title"].ToString() ?? ""
                });
            }

            SessionSelector.ItemsSource = sessions;
        }

        private void CreateSession_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            sessionTitle = SessionTitleBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(sessionTitle))
                return;

            sessionCreatedAt = DateTime.Now;
            notes.Clear();
            currentAttachments.Clear();

            CurrentSessionTitle.Text = $"Sesja: {sessionTitle}";
            SessionCreatedAt.Text = $"Utworzono: {sessionCreatedAt:yyyy-MM-dd HH:mm}";
            NotesList.ItemsSource = null;
            NoteBox.Text = "";
            AttachmentList.ItemsSource = null;
        }

        private void AddNote_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var note = NoteBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(note))
                return;

            notes.Add(new Note
            {
                Text = note,
                Attachments = new List<string>(currentAttachments)
            });

            currentAttachments.Clear();

            UpdateNoteList();
            NoteBox.Text = "";
            AttachmentList.ItemsSource = null;
        }

        private async void AddAttachment_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                AllowMultiple = true,
                Filters =
                {
                    new FileDialogFilter() { Name = "Wszystkie", Extensions = { "*" } },
                    new FileDialogFilter() { Name = "FASTA", Extensions = { "fasta", "fa" } },
                    new FileDialogFilter() { Name = "CSV", Extensions = { "csv" } },
                    new FileDialogFilter() { Name = "Obrazy", Extensions = { "png", "jpg", "jpeg" } }
                }
            };

            var files = await dialog.ShowAsync(this);
            if (files != null)
            {
                currentAttachments.AddRange(files);
                AttachmentList.ItemsSource = null;
                AttachmentList.ItemsSource = currentAttachments;
            }
        }

        private void SaveToDatabase_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var currentNoteText = NoteBox.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(currentNoteText) || currentAttachments.Count > 0)
            {
                notes.Add(new Note
                {
                    Text = currentNoteText,
                    Attachments = new List<string>(currentAttachments)
                });

                currentAttachments.Clear();
                NoteBox.Text = "";
                AttachmentList.ItemsSource = null;
            }

            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            using var tx = conn.BeginTransaction();

            var cmdSession = conn.CreateCommand();
            cmdSession.CommandText = "INSERT INTO Sessions (Title, CreatedAt) VALUES ($title, $createdAt); SELECT last_insert_rowid();";
            cmdSession.Parameters.AddWithValue("$title", sessionTitle);
            cmdSession.Parameters.AddWithValue("$createdAt", sessionCreatedAt.ToString("o"));
            long sessionId = (long)cmdSession.ExecuteScalar();

            foreach (var note in notes)
            {
                var cmdNote = conn.CreateCommand();
                cmdNote.CommandText = "INSERT INTO Notes (SessionId, Text) VALUES ($sessionId, $text); SELECT last_insert_rowid();";
                cmdNote.Parameters.AddWithValue("$sessionId", sessionId);
                cmdNote.Parameters.AddWithValue("$text", note.Text);
                long noteId = (long)cmdNote.ExecuteScalar();

                foreach (var path in note.Attachments)
                {
                    var cmdAttachment = conn.CreateCommand();
                    cmdAttachment.CommandText = "INSERT INTO Attachments (NoteId, FilePath) VALUES ($noteId, $filePath);";
                    cmdAttachment.Parameters.AddWithValue("$noteId", noteId);
                    cmdAttachment.Parameters.AddWithValue("$filePath", path);
                    cmdAttachment.ExecuteNonQuery();
                }
            }

            tx.Commit();
            LoadAllSessions();
        }

        private void LoadSelectedSession_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (SessionSelector.SelectedItem is not SessionInfo selectedSession)
                return;

            LoadSessionById(selectedSession.Id);
        }

        private void LoadLastSession_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            var sessionCmd = conn.CreateCommand();
            sessionCmd.CommandText = "SELECT Id FROM Sessions ORDER BY Id DESC LIMIT 1;";
            var idObj = sessionCmd.ExecuteScalar();
            if (idObj != null)
            {
                long lastSessionId = (long)idObj;
                LoadSessionById(lastSessionId);
            }
        }

        private void LoadSessionById(long sessionId)
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            var sessionCmd = conn.CreateCommand();
            sessionCmd.CommandText = "SELECT * FROM Sessions WHERE Id = $id;";
            sessionCmd.Parameters.AddWithValue("$id", sessionId);
            using var sessionReader = sessionCmd.ExecuteReader();

            if (!sessionReader.Read())
                return;

            sessionTitle = sessionReader["Title"].ToString() ?? "";
            sessionCreatedAt = DateTime.Parse(sessionReader["CreatedAt"].ToString() ?? DateTime.Now.ToString());

            SessionTitleBox.Text = sessionTitle;
            CurrentSessionTitle.Text = $"Sesja: {sessionTitle}";
            SessionCreatedAt.Text = $"Utworzono: {sessionCreatedAt:yyyy-MM-dd HH:mm}";

            var loadedNotes = new List<Note>();

            var notesCmd = conn.CreateCommand();
            notesCmd.CommandText = "SELECT Id, Text FROM Notes WHERE SessionId = $sessionId;";
            notesCmd.Parameters.AddWithValue("$sessionId", sessionId);
            using var notesReader = notesCmd.ExecuteReader();

            while (notesReader.Read())
            {
                string text = notesReader["Text"].ToString() ?? "";
                long noteId = Convert.ToInt64(notesReader["Id"]);

                var attCmd = conn.CreateCommand();
                attCmd.CommandText = "SELECT FilePath FROM Attachments WHERE NoteId = $noteId;";
                attCmd.Parameters.AddWithValue("$noteId", noteId);
                var attachments = new List<string>();
                using var attReader = attCmd.ExecuteReader();
                while (attReader.Read())
                {
                    attachments.Add(attReader["FilePath"].ToString() ?? "");
                }

                loadedNotes.Add(new Note
                {
                    Text = text ?? "",
                    Attachments = attachments ?? new List<string>()
                });

            }

            notes = loadedNotes;
            UpdateNoteList();

            currentAttachments.Clear();

            if (notes.Count > 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (NotesList.ItemCount > 0)
                    {
                        NotesList.SelectedIndex = 0;
                    }
                });
            }
            else
            {
                NotesList.SelectedIndex = -1;
                NoteBox.Text = "";
                AttachmentList.ItemsSource = null;
            }

        }

        private void UpdateNoteList()
        {
            NotesList.SelectionChanged -= NotesList_SelectionChanged;

            NotesList.ItemsSource = null;
            NotesList.ItemsSource = notes;
            NotesList.SelectedIndex = -1;

            NotesList.SelectionChanged += NotesList_SelectionChanged;
        }

        private void NotesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            selectedNoteIndex = NotesList.SelectedIndex;

            if (selectedNoteIndex >= 0 && selectedNoteIndex < notes.Count && NotesList.ItemCount > selectedNoteIndex)
            {
                var note = notes[selectedNoteIndex];
                AttachmentList.ItemsSource = note.Attachments;
            }
            else
            {
                NoteBox.Text = "";
                AttachmentList.ItemsSource = null;
            }
        }

        public void ExportSessionToPdf_Simple(string sessionTitle, DateTime sessionCreatedAt, List<Note> notes)
        {
            try
            {
                var document = new PdfDocument();
                var page = document.AddPage();
                var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 12);
                var titleFont = new XFont("Arial", 16, XFontStyle.Bold);
                double y = 40;

                gfx.DrawString($"Raport sesji: {sessionTitle}", titleFont, XBrushes.Black, new XPoint(40, y));
                y += 30;
                gfx.DrawString($"Data: {sessionCreatedAt:yyyy-MM-dd HH:mm}", font, XBrushes.Black, new XPoint(40, y));
                y += 30;

                int noteNumber = 1;

                foreach (var note in notes)
                {
                    string noteHeader = $"Wpis {noteNumber}:";
                    gfx.DrawString(noteHeader, titleFont, XBrushes.DarkBlue, new XPoint(40, y));
                    y += 20;

                    string noteText = note.Text;
                    var lines = SplitTextToLines(noteText, 80);
                    foreach (var line in lines)
                    {
                        gfx.DrawString(line, font, XBrushes.Black, new XPoint(60, y));
                        y += 18;
                    }

                    if (note.Attachments.Any())
                    {
                        gfx.DrawString("Za³¹czniki:", font, XBrushes.DarkRed, new XPoint(60, y));
                        y += 18;

                        foreach (var attachment in note.Attachments)
                        {
                            var name = Path.GetFileName(attachment);
                            gfx.DrawString($"• {name}", font, XBrushes.Black, new XPoint(80, y));
                            y += 18;
                        }
                    }

                    y += 30;
                    noteNumber++;

                    if (y > page.Height - 100)
                    {
                        page = document.AddPage();
                        gfx = XGraphics.FromPdfPage(page);
                        y = 40;
                    }
                }

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string safeTitle = string.Join("_", sessionTitle.Split(Path.GetInvalidFileNameChars()));
                string filename = Path.Combine(desktop, $"Raport_{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                using var stream = File.Create(filename);
                document.Save(stream);

                Console.WriteLine("PDF zapisany: " + filename);

                Process.Start(new ProcessStartInfo
                {
                    FileName = filename,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("B³¹d PDF: " + ex.Message);
            }
        }

        private List<string> SplitTextToLines(string text, int maxLineLength)
        {
            var words = text.Split(' ');
            var lines = new List<string>();
            var currentLine = new StringBuilder();

            foreach (var word in words)
            {
                if (currentLine.Length + word.Length + 1 > maxLineLength)
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                }
                currentLine.Append((currentLine.Length > 0 ? " " : "") + word);
            }

            if (currentLine.Length > 0)
                lines.Add(currentLine.ToString());

            return lines;
        }
        private void ExportPdf_Click(object? sender, RoutedEventArgs e)
        {
            ExportSessionToPdf_Simple(sessionTitle, sessionCreatedAt, notes);
        }


    }
}
