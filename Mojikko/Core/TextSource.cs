using System.IO;
using System.IO.Compression;
using System.Text;

namespace Mojikko.Core;

/// <summary>読み込み元 1 つ。普通のファイルか、ZIP の中のファイル。</summary>
public sealed class TextSource
{
    /// <summary>実際のファイル（ZIP の場合は ZIP そのもの）。</summary>
    public required string FilePath { get; init; }

    /// <summary>ZIP の中の場所。普通のファイルなら null。</summary>
    public string? EntryName { get; init; }

    public bool InZip => EntryName is not null;

    public string DisplayName => InZip ? $"{Path.GetFileName(FilePath)} › {EntryName}" : Path.GetFileName(FilePath);

    /// <summary>拡張子（ZIP の中ならその中のファイルの拡張子）。</summary>
    public string Extension => Path.GetExtension(EntryName ?? FilePath).ToLowerInvariant();

    public byte[] ReadAllBytes()
    {
        if (!InZip) return File.ReadAllBytes(FilePath);
        using var zip = OpenZip(FilePath);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName == EntryName)
                    ?? throw new FileNotFoundException("ZIP の中にファイルが見つかりません: " + EntryName);
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>テキストとして扱う拡張子。</summary>
    public static readonly string[] TextExtensions = { ".csv", ".tsv", ".txt", ".tab", ".log", ".md", ".json", ".xml", ".html", ".htm", ".ini", ".sql" };

    public static bool IsTextExtension(string ext) => TextExtensions.Contains(ext.ToLowerInvariant());

    /// <summary>
    /// ZIP を開く。まず UTF-8 で名前を読み、読めない名前があれば Shift_JIS で開き直す
    /// （日本語の Windows で作った ZIP は名前が Shift_JIS。Google フォームなどの ZIP は UTF-8）。
    /// .NET 8 は名前の文字コードを指定すると UTF-8 の印がある項目にもそれを使うため、最初は指定せずに開く。
    /// </summary>
    public static ZipArchive OpenZip(string path)
    {
        var zip = ZipFile.Open(path, ZipArchiveMode.Read);
        if (!zip.Entries.Any(e => e.FullName.Contains('�'))) return zip;
        zip.Dispose();
        _ = EncodingDetector.ShiftJis;   // コードページの登録を確実にする
        return ZipFile.Open(path, ZipArchiveMode.Read, Encoding.GetEncoding(932));
    }

    /// <summary>ZIP の中のテキスト ファイルを列挙する。</summary>
    public static IEnumerable<TextSource> FromZip(string zipPath)
    {
        using var zip = OpenZip(zipPath);
        foreach (var e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;   // フォルダー
            if (!IsTextExtension(Path.GetExtension(e.Name))) continue;
            yield return new TextSource { FilePath = zipPath, EntryName = e.FullName };
        }
    }
}
