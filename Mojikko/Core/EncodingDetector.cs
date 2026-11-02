using System.Text;

namespace Mojikko.Core;

/// <summary>判別結果。</summary>
public sealed record DetectedEncoding(Encoding Encoding, string Name, int BomLength, string Note);

/// <summary>
/// テキストの文字コードを推定する（外部ライブラリなし）。
/// 順番: BOM → ISO-2022-JP（エスケープ列）→ BOM なし UTF-16 → 厳密な UTF-8 → Shift_JIS と EUC-JP の比較。
/// 日本語の Windows で作られたファイルを主に想定し、決め手がないときは Shift_JIS（CP932）とする。
/// </summary>
public static class EncodingDetector
{
    static EncodingDetector()
    {
        // Shift_JIS・EUC-JP・ISO-2022-JP は .NET 8 標準のコードページ提供で使える（NuGet 不要）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding ShiftJis => Encoding.GetEncoding(932);
    public static Encoding EucJp => Encoding.GetEncoding(51932);
    public static Encoding Iso2022Jp => Encoding.GetEncoding(50220);
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static DetectedEncoding Detect(byte[] b)
    {
        // 1) BOM
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return new(Utf8NoBom, "UTF-8（BOM 付き）", 3, "");
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
            return new(new UTF32Encoding(false, false), "UTF-32 LE（BOM 付き）", 4, "");
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return new(new UnicodeEncoding(false, false), "UTF-16 LE（BOM 付き）", 2, "");
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return new(new UnicodeEncoding(true, false), "UTF-16 BE（BOM 付き）", 2, "");

        if (b.Length == 0) return new(Utf8NoBom, "空のファイル", 0, "");

        // 2) BOM なし UTF-16（半角文字の上位バイトが 0 になる）
        var utf16 = GuessUtf16(b);
        if (utf16 is not null) return utf16;

        // 3) 7 ビットだけ: ISO-2022-JP のエスケープ列があればそれ、なければ ASCII（= UTF-8 として扱える）
        bool ascii = true;
        foreach (var x in b) { if (x >= 0x80) { ascii = false; break; } }
        if (ascii)
        {
            if (HasIso2022Escape(b)) return new(Iso2022Jp, "ISO-2022-JP（JIS）", 0, "");
            return new(Utf8NoBom, "ASCII（英数字のみ）", 0, "");
        }

        // 4) 厳密な UTF-8
        if (IsValidUtf8(b)) return new(Utf8NoBom, "UTF-8（BOM なし）", 0, "");

        // 5) Shift_JIS と EUC-JP の比較
        var sj = ScoreShiftJis(b);
        var euc = ScoreEucJp(b);

        if (sj.Invalid == 0 && euc.Invalid > 0) return new(ShiftJis, "Shift_JIS", 0, "");
        if (euc.Invalid == 0 && sj.Invalid > 0) return new(EucJp, "EUC-JP", 0, "");
        if (sj.Invalid == 0 && euc.Invalid == 0)
        {
            // EUC-JP の文章を Shift_JIS として読むと半角カナだらけになる
            bool looksEuc = sj.Chars > 0 && sj.HalfKana * 3 > sj.Chars;
            return looksEuc
                ? new(EucJp, "EUC-JP", 0, "Shift_JIS としても読めます")
                : new(ShiftJis, "Shift_JIS", 0, "EUC-JP としても読めます");
        }

        // どちらにも合わないバイトがある: 少ないほうを採る
        return sj.Invalid <= euc.Invalid
            ? new(ShiftJis, "Shift_JIS（推定）", 0, $"読めないバイトが {sj.Invalid} か所あります")
            : new(EucJp, "EUC-JP（推定）", 0, $"読めないバイトが {euc.Invalid} か所あります");
    }

    /// <summary>BOM を除いて文字列にする。</summary>
    public static string Decode(byte[] b, DetectedEncoding d) =>
        d.Encoding.GetString(b, d.BomLength, b.Length - d.BomLength);

    /// <summary>手動で指定された文字コードで読むときの情報（BOM があれば外す）。</summary>
    public static DetectedEncoding Forced(byte[] b, Encoding enc, string name)
    {
        var pre = enc.GetPreamble();
        int bom = 0;
        if (pre.Length == 0 && enc.CodePage == 65001 && b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) bom = 3;
        else if (pre.Length > 0 && b.Length >= pre.Length && b.AsSpan(0, pre.Length).SequenceEqual(pre)) bom = pre.Length;
        return new(enc, name + "（指定）", bom, "");
    }

    // ---------------------------------------------------------------- 個別の判定

    private static bool HasIso2022Escape(byte[] b)
    {
        for (int i = 0; i + 2 < b.Length; i++)
        {
            if (b[i] != 0x1B) continue;
            if (b[i + 1] == 0x24 && (b[i + 2] == 0x42 || b[i + 2] == 0x40)) return true;   // ESC $ B / ESC $ @
            if (b[i + 1] == 0x28 && (b[i + 2] == 0x4A || b[i + 2] == 0x49)) return true;   // ESC ( J / ESC ( I
        }
        return false;
    }

    private static DetectedEncoding? GuessUtf16(byte[] b)
    {
        int n = Math.Min(b.Length, 8192) & ~1;
        if (n < 4) return null;
        int evenZero = 0, oddZero = 0;
        for (int i = 0; i < n; i += 2)
        {
            if (b[i] == 0) evenZero++;
            if (b[i + 1] == 0) oddZero++;
        }
        int pairs = n / 2;
        if (oddZero > pairs * 0.3 && evenZero < pairs * 0.05) return new(new UnicodeEncoding(false, false), "UTF-16 LE（BOM なし）", 0, "");
        if (evenZero > pairs * 0.3 && oddZero < pairs * 0.05) return new(new UnicodeEncoding(true, false), "UTF-16 BE（BOM なし）", 0, "");
        return null;
    }

    public static bool IsValidUtf8(byte[] b)
    {
        int i = 0;
        while (i < b.Length)
        {
            byte c = b[i];
            if (c < 0x80) { i++; continue; }
            int need; int min;
            if (c >= 0xC2 && c <= 0xDF) { need = 1; min = 0x80; }
            else if (c >= 0xE0 && c <= 0xEF) { need = 2; min = 0x800; }
            else if (c >= 0xF0 && c <= 0xF4) { need = 3; min = 0x10000; }
            else return false;
            if (i + need >= b.Length) return false;   // ファイル全体を読んでいるので、途切れた文字は UTF-8 ではない
            int cp = c & (0xFF >> (need + 2));
            for (int k = 1; k <= need; k++)
            {
                byte t = b[i + k];
                if ((t & 0xC0) != 0x80) return false;
                cp = (cp << 6) | (t & 0x3F);
            }
            if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return false;
            i += need + 1;
        }
        return true;
    }

    private readonly record struct Score(int Invalid, int Chars, int HalfKana);

    private static Score ScoreShiftJis(byte[] b)
    {
        int invalid = 0, chars = 0, kana = 0, i = 0;
        while (i < b.Length)
        {
            byte c = b[i];
            if (c < 0x80) { i++; continue; }
            chars++;
            if (c >= 0xA1 && c <= 0xDF) { kana++; i++; continue; }                       // 半角カナ
            if ((c >= 0x81 && c <= 0x9F) || (c >= 0xE0 && c <= 0xFC))
            {
                if (i + 1 < b.Length)
                {
                    byte t = b[i + 1];
                    if ((t >= 0x40 && t <= 0x7E) || (t >= 0x80 && t <= 0xFC)) { i += 2; continue; }
                }
                else { i++; continue; }   // 末尾の途切れは数えない
            }
            invalid++; i++;
        }
        return new Score(invalid, chars, kana);
    }

    private static Score ScoreEucJp(byte[] b)
    {
        int invalid = 0, chars = 0, i = 0;
        while (i < b.Length)
        {
            byte c = b[i];
            if (c < 0x80) { i++; continue; }
            chars++;
            if (c == 0x8E)   // 半角カナ
            {
                if (i + 1 < b.Length && b[i + 1] >= 0xA1 && b[i + 1] <= 0xDF) { i += 2; continue; }
            }
            else if (c == 0x8F)   // 補助漢字
            {
                if (i + 2 < b.Length && b[i + 1] >= 0xA1 && b[i + 1] <= 0xFE && b[i + 2] >= 0xA1 && b[i + 2] <= 0xFE) { i += 3; continue; }
            }
            else if (c >= 0xA1 && c <= 0xFE)
            {
                if (i + 1 < b.Length && b[i + 1] >= 0xA1 && b[i + 1] <= 0xFE) { i += 2; continue; }
                if (i + 1 >= b.Length) { i++; continue; }
            }
            invalid++; i++;
        }
        return new Score(invalid, chars, 0);
    }
}
