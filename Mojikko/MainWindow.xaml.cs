using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mojikko.Core;
using Mojikko.Models;

namespace Mojikko;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FileItem> _items = new();
    private bool _busy;
    private string? _lastOutput;

    public MainWindow()
    {
        InitializeComponent();
        FileGrid.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateState();
        UpdateState();
        Log("ファイルを追加して［変換する］を押してください。");

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0) Loaded += (_, _) => AddPaths(args);
    }

    private bool MergeMode => ModeCombo.SelectedIndex == 1;

    private InputEncoding InputMode => InputCombo.SelectedIndex switch
    {
        1 => InputEncoding.Utf8,
        2 => InputEncoding.ShiftJis,
        3 => InputEncoding.EucJp,
        4 => InputEncoding.Utf16Le,
        _ => InputEncoding.Auto
    };

    // ---------------------------------------------------------------- 追加

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = (System.Windows.Media.Brush)FindResource(ok ? "Accent" : "Line");
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (System.Windows.Media.Brush)FindResource("Line");
        if (_busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "変換するファイルを選ぶ",
            Multiselect = true,
            Filter = "テキスト・CSV・ZIP|*.csv;*.tsv;*.txt;*.tab;*.log;*.md;*.json;*.xml;*.html;*.htm;*.ini;*.sql;*.zip|すべてのファイル (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "フォルダーを選ぶ（中のテキストと ZIP をまとめて追加）" };
        if (dlg.ShowDialog(this) == true) AddPaths(new[] { dlg.FolderName });
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        int added = 0, skipped = 0;
        var sources = new List<TextSource>();

        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*.*", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    Collect(f, sources, ref skipped, fromFolder: true);
            }
            else if (File.Exists(p))
            {
                Collect(p, sources, ref skipped, fromFolder: false);
            }
        }

        foreach (var s in sources)
        {
            if (_items.Any(i => i.Source.FilePath.Equals(s.FilePath, StringComparison.OrdinalIgnoreCase) && i.Source.EntryName == s.EntryName))
                continue;
            var item = new FileItem { Source = s, Status = "待機中" };
            _items.Add(item);
            Inspect(item);
            added++;
        }

        if (added > 0) Log($"{added} 件を追加しました。");
        if (skipped > 0) Log($"{skipped} 件はテキストとして扱わない形式のため飛ばしました。");
    }

    private void Collect(string path, List<TextSource> into, ref int skipped, bool fromFolder)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".zip")
        {
            try
            {
                var inZip = TextSource.FromZip(path).ToList();
                if (inZip.Count == 0) Log($"{Path.GetFileName(path)}: 中にテキスト（CSV など）がありませんでした。");
                into.AddRange(inZip);
            }
            catch (Exception ex)
            {
                Log($"{Path.GetFileName(path)}: ZIP を開けませんでした（{ex.Message}）");
                skipped++;
            }
        }
        else if (TextSource.IsTextExtension(ext) || !fromFolder)
        {
            // 直接指定されたファイルは拡張子にかかわらず受け付ける。フォルダーの中は拡張子で絞る
            into.Add(new TextSource { FilePath = path });
        }
        else skipped++;
    }

    /// <summary>追加したときに文字コードを見ておく（変換前に結果が分かるように）。</summary>
    private void Inspect(FileItem item)
    {
        try
        {
            var loaded = Converter.Load(item.Source, InputMode);
            item.EncodingName = loaded.Encoding.Name;
            item.Size = FileItem.FormatBytes(System.Text.Encoding.UTF8.GetByteCount(loaded.Text));
            item.Status = loaded.OddQuotes ? "⚠ 引用符の数が奇数（CSV が壊れている可能性）"
                        : string.IsNullOrEmpty(loaded.Encoding.Note) ? "待機中" : loaded.Encoding.Note;
        }
        catch (Exception ex)
        {
            item.EncodingName = "?";
            item.Status = "読めません: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- 並べ替え・削除

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        if (_busy || FileGrid.SelectedItem is not FileItem item) return;
        int i = _items.IndexOf(item), j = i + delta;
        if (j < 0 || j >= _items.Count) return;
        _items.Move(i, j);
        FileGrid.SelectedItem = item;
        FileGrid.ScrollIntoView(item);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        foreach (var i in FileGrid.SelectedItems.Cast<FileItem>().ToList()) _items.Remove(i);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (!_busy) _items.Clear();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MergePanel is null) return;
        MergePanel.IsEnabled = MergeMode;
        BtnRun.Content = MergeMode ? "結合して保存…" : "変換する";
    }

    private void OnInputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_items is null) return;
        foreach (var i in _items) Inspect(i);   // 読み込み方を変えたら見直す
    }

    // ---------------------------------------------------------------- 実行

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_items.Count == 0)
        {
            MessageBox.Show(this, "ファイルを追加してください。", "Mojikko", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mode = InputMode;
        var targets = _items.ToList();

        if (MergeMode)
        {
            var first = targets[0].Source;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "結合したファイルの保存先",
                Filter = "CSV (*.csv)|*.csv|テキスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
                FileName = "結合_" + DateTime.Now.ToString("yyyyMMdd") + (first.Extension is ".csv" or ".tsv" ? first.Extension : ".txt"),
                InitialDirectory = Path.GetDirectoryName(first.FilePath)
            };
            if (dlg.ShowDialog(this) != true) return;
            string output = dlg.FileName;
            var options = new MergeOptions { AddSeparator = ChkSeparator.IsChecked == true, SkipHeaderAfterFirst = ChkSkipHeader.IsChecked == true };

            SetBusy(true);
            try
            {
                var loaded = new List<LoadedText>();
                foreach (var item in targets)
                {
                    item.Status = "読み込み中…";
                    var l = await Task.Run(() => Converter.Load(item.Source, mode));
                    item.EncodingName = l.Encoding.Name;
                    item.Status = l.OddQuotes ? "⚠ 引用符の数が奇数（そのまま結合）" : "結合しました";
                    loaded.Add(l);
                }
                await Task.Run(() => Converter.WriteUtf8Bom(output, Converter.Merge(loaded, options)));
                _lastOutput = output;
                Log($"{loaded.Count} 件を結合しました → {output}（{FileItem.FormatBytes(new FileInfo(output).Length)}）");
            }
            catch (Exception ex)
            {
                Log("結合できませんでした: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Mojikko", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
            return;
        }

        SetBusy(true);
        int ok = 0, failed = 0, skipped = 0;
        foreach (var item in targets)
        {
            item.Status = "変換中…";
            try
            {
                var l = await Task.Run(() => Converter.Load(item.Source, mode));
                item.EncodingName = l.Encoding.Name;
                if (l.OddQuotes)
                {
                    // 旧 CsvBomConverter と同じく、壊れている可能性のある CSV は書き出さない
                    item.Status = "⚠ 飛ばしました（引用符の数が奇数）";
                    Log($"{item.DisplayName}: ダブルクォーテーションの数が奇数のため、壊れている可能性があり飛ばしました。");
                    skipped++;
                    continue;
                }
                string output = Converter.OutputPathFor(item.Source);
                await Task.Run(() => Converter.WriteUtf8Bom(output, l.Text));
                item.Status = "完了 → " + Path.GetFileName(output);
                Log($"{item.DisplayName}（{l.Encoding.Name}）→ {output}");
                _lastOutput = output;
                ok++;
            }
            catch (Exception ex)
            {
                item.Status = "失敗: " + ex.Message;
                Log($"{item.DisplayName}: 失敗 — {ex.Message}");
                failed++;
            }
        }
        SetBusy(false);
        Log($"完了: 成功 {ok} 件" + (skipped > 0 ? $" / 飛ばした {skipped} 件" : "") + (failed > 0 ? $" / 失敗 {failed} 件" : ""));
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is null) return;
        try { Process.Start("explorer.exe", $"/select,\"{_lastOutput}\""); } catch { }
    }

    // ---------------------------------------------------------------- 表示

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnRun.IsEnabled = !busy;
        UpdateState();
    }

    private void UpdateState()
    {
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _busy ? "処理しています…" : $"{_items.Count} 件";
        BtnOpen.IsEnabled = !_busy && _lastOutput is not null;
    }

    private void Log(string message)
    {
        LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
