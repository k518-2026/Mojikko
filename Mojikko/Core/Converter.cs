using System.IO;
using System.Text;

namespace Mojikko.Core;

/// <summary>読み込みの文字コード。Auto なら自動判別。</summary>
public enum InputEncoding { Auto, Utf8, ShiftJis, EucJp, Utf16Le }

public sealed class MergeOptions
{
    /// <summary>ファイルの切れ目に「--- ファイル名 ---」の行を入れる（旧 TextFileProcessor と同じ形）。</summary>
    public bool AddSeparator { get; init; }

    /// <summary>2 つ目以降のファイルの 1 行目（CSV の見出し）を飛ばす。</summary>
    public bool SkipHeaderAfterFirst { get; init; }
}

/// <summary>読み込んだ 1 ファイル分。</summary>
public sealed class LoadedText
{
    public required TextSource Source { get; init; }
    public required DetectedEncoding Encoding { get; init; }
    public required string Text { get; init; }

    /// <summary>CSV でダブルクォーテーションの数が奇数（どこかで壊れている）。</summary>
    public bool OddQuotes { get; init; }
}

/// <summary>文字コードを UTF-8（BOM 付き）にそろえる処理。</summary>
public static class Converter
{
    public static readonly Encoding Utf8Bom = new UTF8Encoding(true);

    public static LoadedText Load(TextSource source, InputEncoding mode)
    {
        byte[] bytes = source.ReadAllBytes();
        DetectedEncoding d = mode switch
        {
            InputEncoding.Utf8 => EncodingDetector.Forced(bytes, EncodingDetector.Utf8NoBom, "UTF-8"),
            InputEncoding.ShiftJis => EncodingDetector.Forced(bytes, EncodingDetector.ShiftJis, "Shift_JIS"),
            InputEncoding.EucJp => EncodingDetector.Forced(bytes, EncodingDetector.EucJp, "EUC-JP"),
            InputEncoding.Utf16Le => EncodingDetector.Forced(bytes, new UnicodeEncoding(false, true), "UTF-16 LE"),
            _ => EncodingDetector.Detect(bytes)
        };
        string text = EncodingDetector.Decode(bytes, d);
        bool csv = source.Extension is ".csv" or ".tsv";
        return new LoadedText
        {
            Source = source,
            Encoding = d,
            Text = text,
            OddQuotes = csv && text.Count(c => c == '"') % 2 == 1
        };
    }

    /// <summary>
    /// 1 ファイルを UTF-8（BOM 付き）で書き出す先を決める。
    /// 普通のファイル: 同じフォルダーの「名前_BOM.拡張子」（旧 CsvBomConverter と同じ）。
    /// ZIP の中のファイル: ZIP の隣の「ZIP 名_BOM」フォルダーに、ZIP の中と同じ構成で置く。
    /// 既にあれば (2), (3)… を付ける。
    /// </summary>
    public static string OutputPathFor(TextSource s)
    {
        string path;
        if (s.InZip)
        {
            string root = Path.Combine(Path.GetDirectoryName(s.FilePath) ?? ".", Path.GetFileNameWithoutExtension(s.FilePath) + "_BOM");
            string rel = s.EntryName!.Replace('/', Path.DirectorySeparatorChar);
            foreach (var c in Path.GetInvalidPathChars()) rel = rel.Replace(c, '_');
            path = Path.GetFullPath(Path.Combine(root, rel));
            // ZIP の中の「..\」で外へ書き出さない
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(root, Path.GetFileName(rel));
        }
        else
        {
            path = Path.Combine(Path.GetDirectoryName(s.FilePath) ?? ".",
                Path.GetFileNameWithoutExtension(s.FilePath) + "_BOM" + Path.GetExtension(s.FilePath));
        }
        return Unique(path);
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public static void WriteUtf8Bom(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Utf8Bom);
    }

    /// <summary>複数のテキストを 1 つにつなぐ。改行は 1 つ目のファイルに合わせる。</summary>
    public static string Merge(IReadOnlyList<LoadedText> items, MergeOptions options)
    {
        if (items.Count == 0) return "";
        string nl = items[0].Text.Contains("\r\n") ? "\r\n" : items[0].Text.Contains('\n') ? "\n" : "\r\n";
        var sb = new StringBuilder();

        for (int i = 0; i < items.Count; i++)
        {
            string text = items[i].Text;
            if (options.SkipHeaderAfterFirst && i > 0)
            {
                int cut = text.IndexOf('\n');
                text = cut < 0 ? "" : text[(cut + 1)..];
            }

            if (options.AddSeparator)
            {
                sb.Append("----------------------------------------").Append(nl);
                sb.Append("--- ").Append(items[i].Source.DisplayName).Append(" ---").Append(nl);
                sb.Append("----------------------------------------").Append(nl);
            }

            sb.Append(text);
            // 次のファイルが前の行の続きにならないよう、改行で終わらせる
            if (text.Length > 0 && !text.EndsWith('\n') && i < items.Count - 1) sb.Append(nl);
        }
        return sb.ToString();
    }
}
