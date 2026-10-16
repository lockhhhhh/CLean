// ========================================================== report reading side
//
// WHY THIS FILE EXISTS: every cleanup script already writes a JSON report (<tag>.json), but
// until now the window only ever showed a one-line conclusion, so the list the user
// actually wanted (the catalogue, the discovered caches, the diagnosis findings) was
// readable only inside the separate log window. The scripts were doing real work and the
// window looked like it did nothing.
//
// This is the reading half of the writer in GuiPages.cs (class Json, which only escapes and
// appends). Same rule as the rest of the tool: no serializer reference, no NuGet, nothing
// beyond what csc.exe plus the in-box framework already gives us. That is why the parser
// below is hand written - and it stays tiny on purpose, because the only JSON it will ever
// see is what our own PowerShell scripts emit (plus a hand-edited file once in a while).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

// ============================================================== json node tree

// The whole parsed document. One node type for objects, arrays and scalars keeps the
// reader short: the report shapes are shallow and uniform, so a visitor hierarchy would
// cost more code than it explains.
internal sealed class JNode
{
    internal bool IsObject;
    internal bool IsArray;
    internal bool IsScalar;
    internal string Scalar;                        // scalar text, already unescaped
    internal List<string> Keys;                    // object: keys in file order
    internal List<JNode> Values;                   // object: parallel to Keys / array: items

    internal JNode Get(string key)
    {
        if (!IsObject || Keys == null || key == null) return null;
        // Ordinal, not ignore-case: JSON keys are case sensitive, and two keys that differ
        // only in case are two different fields in the file.
        for (int i = 0; i < Keys.Count; i++)
            if (String.Equals(Keys[i], key, StringComparison.Ordinal)) return Values[i];
        return null;
    }

    internal int Count
    {
        get
        {
            if (IsArray) return Values == null ? 0 : Values.Count;
            if (IsObject) return Keys == null ? 0 : Keys.Count;
            return 0;
        }
    }

    internal JNode At(int i)
    {
        if (!IsArray || Values == null || i < 0 || i >= Values.Count) return null;
        return Values[i];
    }

    internal double AsDouble(double fallback)
    {
        if (!IsScalar || Scalar == null) return fallback;
        double d;
        // InvariantCulture: reports are written with '.' as the decimal separator, and a
        // machine set to a comma-decimal locale must still parse them (see FormatNumber).
        if (!Double.TryParse(Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return fallback;
        return d;
    }
}

// ================================================================= json reader

internal static class JsonRead
{
    // A pathological file must not take the process down with a stack overflow, and the
    // real ones nest three levels at most.
    private const int MaxDepth = 64;

    // Never throws at the caller: a report that cannot be read is a normal outcome here
    // (the script may still be writing it), and the page has an empty state for it.
    internal static JNode Parse(string text)
    {
        if (text == null) return null;
        try
        {
            int i = 0;
            // Belt and braces: LoadFile already lets the StreamReader eat the BOM, but a
            // BOM that survived (or one pasted mid-file) is whitespace as far as we care.
            SkipWs(text, ref i);
            if (i >= text.Length) return null;
            JNode root = ParseValue(text, ref i, 0);
            if (root == null) return null;
            SkipWs(text, ref i);
            // Trailing junk means the write was cut short or two documents were appended:
            // refuse the whole file rather than silently showing a half-parsed table.
            if (i != text.Length) return null;
            return root;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Missing / unreadable / unparseable all collapse to null on purpose.
    internal static JNode LoadFile(string path)
    {
        if (path == null || path.Length == 0) return null;
        try
        {
            if (!File.Exists(path)) return null;

            // File.ReadAllText + Encoding.UTF8 is the important bit here: PowerShell 5.1's
            // `Set-Content -Encoding UTF8` writes a BOM, and a leading U+FEFF that reached
            // the parser as a character would be an "unexpected character" at offset 0 -
            // every report would read as unreadable. StreamReader's BOM sniffing drops it,
            // and it also rescues a UTF-16 file someone saved by hand.
            string text = File.ReadAllText(path, Encoding.UTF8);
            return Parse(text);
        }
        catch (Exception)
        {
            // Sharing violation while a script is mid-write, permission problem, broken
            // encoding: all of them mean "no usable report right now".
            return null;
        }
    }

    // ------------------------------------------------------------------ parsing

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\uFEFF') i++;
            else break;
        }
    }

    private static JNode ParseValue(string s, ref int i, int depth)
    {
        if (depth > MaxDepth) return null;
        char c = s[i];
        if (c == '{') return ParseObject(s, ref i, depth + 1);
        if (c == '[') return ParseArray(s, ref i, depth + 1);
        if (c == '"')
        {
            string str = ParseString(s, ref i);
            if (str == null) return null;
            JNode node = new JNode();
            node.IsScalar = true;
            node.Scalar = str;
            return node;
        }
        return ParseLiteral(s, ref i);
    }

    private static JNode ParseObject(string s, ref int i, int depth)
    {
        JNode node = new JNode();
        node.IsObject = true;
        node.Keys = new List<string>();
        node.Values = new List<JNode>();
        i++;                                                   // '{'
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '}')
        {
            i++;
            return node;
        }
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') return null;       // keys must be quoted
            string key = ParseString(s, ref i);
            if (key == null) return null;
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ':') return null;
            i++;
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            JNode value = ParseValue(s, ref i, depth);
            if (value == null) return null;
            node.Keys.Add(key);
            node.Values.Add(value);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ',')
            {
                i++;
                continue;
            }
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return node;
            }
            // EOF or any other character: the object never closed.
            return null;
        }
    }

    private static JNode ParseArray(string s, ref int i, int depth)
    {
        JNode node = new JNode();
        node.IsArray = true;
        node.Values = new List<JNode>();
        i++;                                                   // '['
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ']')
        {
            i++;
            return node;
        }
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            JNode value = ParseValue(s, ref i, depth);
            if (value == null) return null;
            node.Values.Add(value);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ',')
            {
                i++;
                continue;
            }
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return node;
            }
            return null;
        }
    }

    private static string ParseString(string s, ref int i)
    {
        i++;                                                   // opening quote
        StringBuilder sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\')
            {
                // Unescaped control characters are invalid JSON, but tolerating them costs
                // nothing and a stray TAB in a hand-edited report should not blank the page.
                sb.Append(c);
                continue;
            }
            if (i >= s.Length) return null;
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length) return null;
                    int code = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int digit = HexDigit(s[i + k]);
                        if (digit < 0) return null;
                        code = code * 16 + digit;
                    }
                    i += 4;
                    // Surrogate pairs arrive as two \u escapes and are appended as two
                    // UTF-16 units, which is exactly what a .NET string wants.
                    sb.Append((char)code);
                    break;
                default: return null;
            }
        }
        return null;                                           // truncated mid-string
    }

    private static JNode ParseLiteral(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && IsLiteralChar(s[i])) i++;
        if (i == start) return null;
        JNode node = new JNode();
        node.IsScalar = true;
        node.Scalar = s.Substring(start, i - start);
        return node;
    }

    // Letters are included so true/false/null (and, harmlessly, NaN) come through as bare
    // scalars; anything that is not a valid literal is rejected by AsDouble / the renderer
    // rather than by the tokenizer.
    private static bool IsLiteralChar(char c)
    {
        if (c >= '0' && c <= '9') return true;
        if (c >= 'a' && c <= 'z') return true;
        if (c >= 'A' && c <= 'Z') return true;
        return c == '-' || c == '+' || c == '.' || c == '_';
    }

    private static int HexDigit(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
}

// ============================================================== derived table

// One table derived from "a field whose value is an array of objects". The derivation is
// generic on purpose: it must not care whether the file is before.json or catalog.json.
internal sealed class ReportTable
{
    internal string Title = "";
    internal string[] Columns = new string[0];
    internal List<string[]> Rows = new List<string[]>();

    // Index of the volume column, or -1. Used for right-aligned mono cells, the section
    // total, and the table ordering.
    internal int SizeColumn = -1;
}

// =========================================================== one rendered report

internal sealed class ReportDoc
{
    // Both limits exist because one real report blows past them: system.json's Rows holds
    // 22 keys per element (free-space readings for every target), which as 22 columns is a
    // wall of 5px slivers, and the Failures array of a big run can list thousands of lines.
    private const int MaxColumns = 6;
    private const int MaxRows = 500;

    internal string SourcePath = "";
    internal string Title = "";
    internal List<KeyValuePair<string, string>> Facts = new List<KeyValuePair<string, string>>();
    internal List<ReportTable> Tables = new List<ReportTable>();

    internal bool IsEmpty
    {
        get
        {
            bool noFacts = Facts == null || Facts.Count == 0;
            bool noTables = Tables == null || Tables.Count == 0;
            return noFacts && noTables;
        }
    }

    // Any failure - no file, unreadable, malformed, or a document that is not an object -
    // returns null, and every caller has an empty state for it.
    internal static ReportDoc Load(string path, string fallbackTitle)
    {
        JNode root = JsonRead.LoadFile(path);
        if (root == null || !root.IsObject) return null;

        ReportDoc doc = new ReportDoc();
        doc.SourcePath = path == null ? "" : path;
        doc.Title = fallbackTitle == null ? "" : fallbackTitle;

        JNode title = root.Get("Title");
        if (title != null && title.IsScalar && !IsNullLiteral(title.Scalar) && title.Scalar.Length > 0)
            doc.Title = title.Scalar;

        List<TableRank> ranks = new List<TableRank>();
        for (int i = 0; i < root.Keys.Count; i++)
        {
            string key = root.Keys[i];
            JNode value = root.Values[i];
            if (value == null) continue;
            if (IsSame(key, "Title")) continue;        // already became doc.Title
            if (IsMetaKey(key)) continue;              // audit trail, not a conclusion

            if (value.IsArray)
            {
                AddArray(doc, ranks, key, value);
            }
            else if (value.IsObject)
            {
                AddNestedFacts(doc, key, value);
            }
            else
            {
                doc.Facts.Add(new KeyValuePair<string, string>(key, ScalarText(key, value)));
            }
        }

        // Volume tables first, biggest first; the rest by how much they list. The explicit
        // order index keeps ties deterministic (List.Sort is not stable, and a report that
        // reshuffles between two openings looks broken).
        ranks.Sort(delegate(TableRank a, TableRank b)
        {
            bool av = a.Table.SizeColumn >= 0;
            bool bv = b.Table.SizeColumn >= 0;
            if (av != bv) return av ? -1 : 1;
            if (av)
            {
                if (a.Volume != b.Volume) return a.Volume > b.Volume ? -1 : 1;
            }
            else
            {
                int ac = a.Table.Rows.Count;
                int bc = b.Table.Rows.Count;
                if (ac != bc) return ac > bc ? -1 : 1;
            }
            return a.Order.CompareTo(b.Order);
        });
        for (int i = 0; i < ranks.Count; i++) doc.Tables.Add(ranks[i].Table);

        return doc;
    }

    // ---------------------------------------------------------------- assembly

    private sealed class TableRank
    {
        internal ReportTable Table;
        internal double Volume;
        internal int Order;
    }

    private sealed class KeyRank
    {
        internal string Key;
        internal int Tier;
        internal int Sub;
        internal int Index;
    }

    private static void AddArray(ReportDoc doc, List<TableRank> ranks, string key, JNode array)
    {
        if (array.Count == 0)
        {
            // No element means no keys to derive columns from. It still gets a one-column
            // table showing "0 items": "the script looked and found nothing" is itself the
            // conclusion (SystemFiles: [] in the space-growth report), and dropping the
            // field would make the page look like the run never wrote anything.
            ReportTable empty = new ReportTable();
            empty.Title = key;
            empty.Columns = new string[] { key };
            empty.SizeColumn = -1;
            TableRank emptyRank = new TableRank();
            emptyRank.Table = empty;
            emptyRank.Order = ranks.Count;
            ranks.Add(emptyRank);
            return;
        }

        if (!HasObjectElement(array))
        {
            // An array of scalars is a list in its own right (the Failures lines): one
            // column, the field name as the header.
            ReportTable list = new ReportTable();
            list.Title = key;
            list.Columns = new string[] { key };
            int limit = Math.Min(array.Count, MaxRows);
            for (int i = 0; i < limit; i++)
            {
                JNode item = array.At(i);
                list.Rows.Add(new string[] { item == null ? "" : ScalarText(key, item) });
            }
            if (array.Count > MaxRows) list.Title += "（已截断，显示前 " + MaxRows + " 行）";
            list.SizeColumn = -1;
            TableRank listRank = new TableRank();
            listRank.Table = list;
            listRank.Order = ranks.Count;
            ranks.Add(listRank);
            return;
        }

        double volume;
        ReportTable table = BuildTable(key, array, out volume);
        TableRank rank = new TableRank();
        rank.Table = table;
        rank.Volume = volume;
        rank.Order = ranks.Count;
        ranks.Add(rank);
    }

    private static bool HasObjectElement(JNode array)
    {
        for (int i = 0; i < array.Count; i++)
        {
            JNode item = array.At(i);
            if (item != null && item.IsObject) return true;
        }
        return false;
    }

    // Rule 4: a nested object contributes its scalar fields, one level deep.
    private static void AddNestedFacts(ReportDoc doc, string parent, JNode obj)
    {
        if (obj.Keys == null) return;
        for (int i = 0; i < obj.Keys.Count; i++)
        {
            JNode value = obj.Values[i];
            // Deeper arrays/objects are summaries of their own (NetGuard.Steps), and
            // "Parent.Child.Grandchild" stops being a fact line and becomes a tree.
            if (value == null || !value.IsScalar) continue;
            string key = obj.Keys[i];
            if (IsMetaKey(key)) continue;   // same noise filter as the top level
            doc.Facts.Add(new KeyValuePair<string, string>(parent + "." + key, ScalarText(key, value)));
        }
    }

    private static ReportTable BuildTable(string title, JNode array, out double volumeMB)
    {
        volumeMB = 0;
        ReportTable table = new ReportTable();

        // Columns are the keys of the first element - the shape the script declared - plus
        // any key that shows up in nearly every element but is missing from that first one.
        // The 90% floor is what keeps a mixed report readable: before-v2.json's Targets
        // combines discovery rows (Category/Path/MB/Files) with catalogue rows that also
        // carry Id/Risk/Mode, and a plain union of all keys would spend three of the six
        // column slots on cells that stay blank for the first 23 rows.
        const int CommonPercent = 90;
        List<string> keys = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        JNode first = null;
        int objects = 0;
        for (int i = 0; i < array.Count; i++)
        {
            JNode item = array.At(i);
            if (item == null || !item.IsObject) continue;
            if (first == null) first = item;
            objects++;
            for (int k = 0; k < item.Keys.Count; k++)
            {
                int c;
                counts.TryGetValue(item.Keys[k], out c);
                counts[item.Keys[k]] = c + 1;
            }
        }
        if (first != null)
        {
            for (int k = 0; k < first.Keys.Count; k++)
                if (seen.Add(first.Keys[k])) keys.Add(first.Keys[k]);
        }
        if (objects > 0)
        {
            for (int i = 0; i < array.Count; i++)
            {
                JNode item = array.At(i);
                if (item == null || !item.IsObject) continue;
                for (int k = 0; k < item.Keys.Count; k++)
                {
                    string candidate = item.Keys[k];
                    if (seen.Contains(candidate)) continue;
                    if (counts[candidate] * 100 >= objects * CommonPercent && seen.Add(candidate))
                        keys.Add(candidate);
                }
            }
        }

        List<KeyRank> ranked = new List<KeyRank>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            KeyRank r = new KeyRank();
            r.Key = keys[i];
            r.Index = i;
            RankKey(r.Key, i, out r.Tier, out r.Sub);
            ranked.Add(r);
        }
        ranked.Sort(delegate(KeyRank a, KeyRank b)
        {
            if (a.Tier != b.Tier) return a.Tier < b.Tier ? -1 : 1;
            if (a.Sub != b.Sub) return a.Sub < b.Sub ? -1 : 1;
            return a.Index.CompareTo(b.Index);
        });

        int kept = Math.Min(MaxColumns, ranked.Count);
        table.Columns = new string[kept];
        for (int i = 0; i < kept; i++)
        {
            table.Columns[i] = ranked[i].Key;
            if (table.SizeColumn < 0 && IsByteVolumeKey(ranked[i].Key)) table.SizeColumn = i;
        }

        int limit = Math.Min(array.Count, MaxRows);
        for (int i = 0; i < limit; i++)
        {
            JNode item = array.At(i);
            string[] cells = new string[kept];
            for (int c = 0; c < kept; c++)
            {
                string key = table.Columns[c];
                JNode value = null;
                if (item != null && item.IsObject) value = item.Get(key);
                // A mixed array (a scalar next to objects) at least shows its text in the
                // first column instead of an empty row.
                else if (item != null && c == 0) value = item;
                cells[c] = FormatValue(key, value);
            }
            table.Rows.Add(cells);
            if (table.SizeColumn >= 0 && item != null && item.IsObject)
                volumeMB += VolumeOf(table.Columns[table.SizeColumn], item.Get(table.Columns[table.SizeColumn]));
        }

        table.Title = title;
        if (ranked.Count > MaxColumns)
            table.Title += "（还有 " + (ranked.Count - MaxColumns) + " 列未显示）";
        if (array.Count > MaxRows)
            table.Title += "（已截断，显示前 " + MaxRows + " 行）";
        return table;
    }

    // ------------------------------------------------------------------- ranking

    // Column priority, highest first:
    //   1 name column        Path / Name / Label / Target / Title / Id
    //   2 volume column      anything MB / GB / Bytes / Size
    //   3 conclusion column  Risk / Verdict / Mode / Group / Type, and *Count / *Total
    //   4 the rest, in file order
    // Inside a tier the sub-rank decides; only tier 4 is pure file order. Path leads the
    // name tier because it is the column a reader scans, and Id trails it because an id
    // like "temp.user" is a classifier, not the thing being cleaned.
    private static void RankKey(string key, int index, out int tier, out int sub)
    {
        tier = 4;
        sub = index;
        string k = key == null ? "" : key.Trim();

        if (IsSame(k, "Path")) { tier = 1; sub = 0; return; }
        if (IsSame(k, "Name")) { tier = 1; sub = 1; return; }
        if (IsSame(k, "Label")) { tier = 1; sub = 2; return; }
        if (IsSame(k, "Target")) { tier = 1; sub = 3; return; }
        if (IsSame(k, "Title")) { tier = 1; sub = 4; return; }
        if (IsSame(k, "Id")) { tier = 1; sub = 5; return; }

        int volSub;
        if (IsVolumeKey(k, out volSub)) { tier = 2; sub = volSub; return; }

        if (IsSame(k, "Risk")) { tier = 3; sub = 0; return; }
        if (IsSame(k, "Verdict")) { tier = 3; sub = 1; return; }
        if (IsSame(k, "Mode")) { tier = 3; sub = 2; return; }
        if (IsSame(k, "Group")) { tier = 3; sub = 3; return; }
        if (IsSame(k, "Type")) { tier = 3; sub = 4; return; }
        if (Contains(k, "Count") || Contains(k, "Total")) { tier = 3; sub = 5; return; }
    }

    // MB / Bytes / Size rank above GB, because those are the columns a cleanup report
    // measures its result in. The match is on whole names or suffixes, never on any
    // substring: "Number" contains "mb" and would otherwise be mistaken for a volume.
    private static bool IsVolumeKey(string k, out int sub)
    {
        sub = 9;
        if (IsSame(k, "MB")) { sub = 0; return true; }
        if (IsSame(k, "Bytes")) { sub = 1; return true; }
        if (IsSame(k, "Size")) { sub = 2; return true; }
        if (EndsWith(k, "MB")) { sub = 3; return true; }
        if (EndsWith(k, "Bytes")) { sub = 4; return true; }
        if (EndsWith(k, "Size")) { sub = 5; return true; }
        if (IsSame(k, "GB")) { sub = 6; return true; }
        if (EndsWith(k, "GB")) { sub = 7; return true; }
        return false;
    }

    // A GB column is a volume, but it is not the "size in MB" the table is ranked and
    // totalled by. Keeping GB out of SizeColumn is also what stops a 3-row disk-capacity
    // table (Disks: TotalGB/FreeGB) from sorting above the 63-row list of actual targets.
    private static bool IsByteVolumeKey(string k)
    {
        return EndsWith(k, "MB") || EndsWith(k, "Bytes") || EndsWith(k, "Size");
    }

    // ------------------------------------------------------------------ rendering

    private static string Mb(double mb)
    {
        return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    // The one place a value becomes display text. Reports record sizes in MB (the scripts
    // divide by 1MB before writing), so a *MB field is printed as "123.4 MB" and a *Bytes
    // field is converted first. A *GB field keeps GB - printing 232.77 GB as "238369.5 MB"
    // would be technically true and useless - and anything else keeps one decimal so a
    // count never grows a unit it does not have.
    private static string ScalarText(string key, JNode value)
    {
        if (value == null || value.Scalar == null) return "";
        string s = value.Scalar;
        if (s.Length == 0) return "";
        if (IsNullLiteral(s)) return "-";
        if (s == "true") return "是";
        if (s == "false") return "否";

        double d;
        if (Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
        {
            if (EndsWith(key, "Bytes")) return Mb(d / 1048576.0);
            if (EndsWith(key, "MB")) return Mb(d);
            if (EndsWith(key, "GB")) return d.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            return d.ToString("0.0", CultureInfo.InvariantCulture);
        }
        return s;                       // a path, a verdict, a label: already display-ready
    }

    private static string FormatValue(string key, JNode value)
    {
        if (value == null) return "";
        if (value.IsArray)
        {
            // Include lists reach twelve entries; the list painter ellipsises them, and the
            // CSV export gets the whole thing because there the cell is not clipped.
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < value.Count; i++)
            {
                JNode item = value.At(i);
                if (item == null || !item.IsScalar) continue;
                string part = ScalarText(key, item);
                if (part.Length == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(part);
            }
            return sb.ToString();
        }
        if (value.IsObject)
        {
            // Only reachable for a nested object inside a table row. Show its scalars as
            // key=value pairs; a nested array inside it is skipped, same as in Facts.
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < value.Keys.Count; i++)
            {
                JNode item = value.Values[i];
                if (item == null || !item.IsScalar) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(value.Keys[i]).Append('=').Append(ScalarText(value.Keys[i], item));
            }
            return sb.ToString();
        }
        return ScalarText(key, value);
    }

    private static double VolumeOf(string key, JNode value)
    {
        if (value == null || !value.IsScalar) return 0;
        double d;
        if (!Double.TryParse(value.Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
        if (EndsWith(key, "Bytes")) return d / 1048576.0;
        return d;                       // SizeColumn only ever holds MB / Bytes / Size keys
    }

    // -------------------------------------------------------------------- filtering

    // Audit metadata: when and how the script ran, never what it found. Kept out of Facts
    // so the conclusion (FreedMB, Verdict, TotalMB) is not buried under bookkeeping.
    private static bool IsMetaKey(string key)
    {
        return IsSame(key, "Time") || IsSame(key, "Timestamp") || IsSame(key, "Tag") ||
               IsSame(key, "Mode") || IsSame(key, "Tool") || IsSame(key, "Page") ||
               IsSame(key, "Root") || IsSame(key, "Roots");
    }

    // A bare null literal. A quoted "null" would land here too - no report emits one, and
    // "-" is the honest rendering for both.
    private static bool IsNullLiteral(string s)
    {
        return s != null && s.Length == 4 && s[0] == 'n' && s[1] == 'u' && s[2] == 'l' && s[3] == 'l';
    }

    private static bool IsSame(string a, string b)
    {
        return String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EndsWith(string s, string suffix)
    {
        if (s == null) return false;
        return s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string s, string part)
    {
        if (s == null) return false;
        return s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ================================================================== csv export

    // Excel friendly: CRLF rows, doubled quotes, and quoting for anything that would
    // otherwise break a row. No BOM is embedded here on purpose - the caller writes the
    // file and already picks UTF8Encoding(true) (see ScriptPage.ExportCsv), and a BOM
    // inside the text would end up duplicated.
    internal string ToCsv()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("# Report,").Append(CsvField(SourcePath)).Append("\r\n");
        sb.Append("# Title,").Append(CsvField(Title)).Append("\r\n");

        if (Facts != null && Facts.Count > 0)
        {
            sb.Append("\r\n");
            for (int i = 0; i < Facts.Count; i++)
            {
                sb.Append(CsvField(Facts[i].Key)).Append(',');
                sb.Append(CsvField(Facts[i].Value)).Append("\r\n");
            }
        }

        if (Tables != null)
        {
            for (int i = 0; i < Tables.Count; i++)
            {
                ReportTable t = Tables[i];
                sb.Append("\r\n");
                sb.Append("# ").Append(CsvField(t.Title)).Append("\r\n");
                AppendCsvRow(sb, t.Columns);
                for (int r = 0; r < t.Rows.Count; r++) AppendCsvRow(sb, t.Rows[r]);
            }
        }
        return sb.ToString();
    }

    private static void AppendCsvRow(StringBuilder sb, string[] cells)
    {
        if (cells == null) return;
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CsvField(cells[i]));
        }
        sb.Append("\r\n");
    }

    private static string CsvField(string s)
    {
        if (s == null) return "";
        bool quote = s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 ||
                     s.IndexOf('\r') >= 0 || s.IndexOf('\n') >= 0;
        if (!quote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}

// ================================================================ the list view

// Owner-drawn, read-only report list: section headings, column headings and data rows.
//
// WHY THERE IS NO HOVER HIGHLIGHT HERE (unlike ResultList / the rail buttons): a native
// ListBox cannot be double buffered in practice - AllPaintingInWmPaint + OptimizedDoubleBuffer
// do not stop the white flash when the whole control is invalidated for a hover, which is
// exactly the trap the README records for this repo. Without hover there is no per-pixel
// repaint to get wrong, so the whole list only ever invalidates once per palette change.
//
// Accessibility: the visible text of every row lives in that row object's ToString(), which
// is what ListBox.GetItemText (and therefore MSAA / UI Automation) reads - the panel has no
// child controls to expose, so there is nowhere else for it to come from.
internal sealed class ReportList : ListBox, IThemed
{
    private const int RowHeight = 24;
    private const int SidePad = 10;
    private const int ColGap = 12;
    private const int MinValueColumn = 76;      // measured: "MinAgeDays" 74px, "232.77 GB" 70px
    private const int MinNameColumn = 110;

    private const int KindSection = 0;
    private const int KindHeader = 1;
    private const int KindData = 2;
    private const int KindEmpty = 3;

    private const TextFormatFlags RightFlags =
        TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

    // One drawn line. The list has no column model of its own, so the line carries
    // everything the painter needs, including which cells are numbers (mono font, right
    // aligned, muted) and which are prose.
    private sealed class Line
    {
        internal int Kind;
        internal string Text;          // section / empty-state text
        internal string[] Cells;       // header / data cells
        internal bool[] Numeric;       // per column: a number column -> right aligned mono

        public override string ToString()
        {
            if (Kind == KindSection || Kind == KindEmpty) return Text == null ? "" : Text;
            if (Cells == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Cells.Length; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(Cells[i]);
            }
            return sb.ToString();
        }
    }

    internal ReportList()
    {
        // NOT ControlStyles.UserPaint, even though every hand-painted control in this repo
        // sets it: a native ListBox paints its items by handling WM_PAINT itself and asking
        // us back through WM_DRAWITEM, and UserPaint makes Control.WmPaint skip that native
        // path - the list then renders as one empty rectangle. Measured, not guessed: two
        // otherwise identical owner-drawn list boxes, one flag apart, painted 0 and 27062
        // pixels in the same DrawToBitmap capture. ResultList omits the flag for the same
        // reason; OptimizedDoubleBuffer + AllPaintingInWmPaint + ResizeRedraw are safe
        // (30695 painted pixels with them, 21215 with no styles at all).
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.ResizeRedraw, true);
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = RowHeight;
        BorderStyle = BorderStyle.None;
        Font = Theme.FontUI;
        // Same as ResultList: without this the control snaps its height to whole rows and
        // fights the page's docking.
        IntegralHeight = false;
        SelectionMode = SelectionMode.One;
        TabStop = true;
        ApplyPalette();
    }

    // IThemed: the painter reads Theme.* live, so the only thing to do is repaint once.
    public void ApplyPalette()
    {
        // BackColor also becomes the colour the native control erases a row with before it
        // asks us to draw it - keeping it on the card colour is what makes a selection
        // change invisible in any flash-prone path.
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Invalidate();
    }

    internal void SetDoc(ReportDoc doc)
    {
        Items.Clear();
        if (doc == null || doc.IsEmpty)
        {
            Line empty = new Line();
            empty.Kind = KindEmpty;
            empty.Text = "没有可显示的报告内容（脚本可能还没写出报告）";
            Items.Add(empty);
            Invalidate();
            return;
        }

        if (doc.Facts != null && doc.Facts.Count > 0) BuildFacts(doc);
        if (doc.Tables != null)
        {
            for (int i = 0; i < doc.Tables.Count; i++) BuildTable(doc.Tables[i]);
        }
        Invalidate();
    }

    private void BuildFacts(ReportDoc doc)
    {
        string head = (doc.Title != null && doc.Title.Length > 0) ? doc.Title : "报告摘要";
        Items.Add(Section(head + "   ·   共 " + doc.Facts.Count + " 项"));

        string[] columns = new string[] { "字段", "值" };
        // Facts are a definition list (field -> value), not a numeric matrix: one anchor
        // position for every value reads as a list, while right-aligning the few numbers
        // would leave each row's value somewhere different.
        bool[] leftAll = new bool[2];
        Items.Add(Header(columns, leftAll));
        for (int i = 0; i < doc.Facts.Count; i++)
            Items.Add(Data(new string[] { doc.Facts[i].Key, doc.Facts[i].Value }, leftAll));
    }

    private void BuildTable(ReportTable table)
    {
        if (table == null) return;
        int rows = table.Rows == null ? 0 : table.Rows.Count;

        string head = table.Title + "   ·   共 " + rows + " 项";
        string size = SizeSummary(table);
        if (size.Length > 0) head += " · " + size;
        Items.Add(Section(head));

        if (table.Columns == null || table.Columns.Length == 0) return;
        // Alignment is decided per column, not per cell: a column that is mostly numbers
        // (BeforeMB, FreedMB) lines up on its right edge, and a column of prose
        // (Category, Note) stays left even if one row in it happens to hold a number.
        bool[] numeric = NumericColumns(table);
        Items.Add(Header(table.Columns, numeric));
        for (int i = 0; i < rows; i++) Items.Add(Data(table.Rows[i], numeric));
    }

    private static bool[] NumericColumns(ReportTable table)
    {
        bool[] numeric = new bool[table.Columns.Length];
        for (int c = 1; c < table.Columns.Length; c++)
        {
            int seen = 0;
            int hits = 0;
            for (int r = 0; r < table.Rows.Count; r++)
            {
                string[] row = table.Rows[r];
                if (row == null || c >= row.Length || row[c] == null || row[c].Length == 0) continue;
                seen++;
                if (IsNumericCell(row[c])) hits++;
            }
            numeric[c] = seen > 0 && hits * 2 >= seen;
        }
        // The column the table is ranked by is a number whatever the other rows look like.
        if (table.SizeColumn >= 0 && table.SizeColumn < numeric.Length) numeric[table.SizeColumn] = true;
        return numeric;
    }

    private static Line Section(string text)
    {
        Line line = new Line();
        line.Kind = KindSection;
        line.Text = text;
        return line;
    }

    private static Line Header(string[] columns, bool[] numeric)
    {
        Line line = new Line();
        line.Kind = KindHeader;
        line.Cells = columns;
        line.Numeric = numeric;
        return line;
    }

    private static Line Data(string[] cells, bool[] numeric)
    {
        Line line = new Line();
        line.Kind = KindData;
        line.Cells = cells;
        line.Numeric = numeric;
        return line;
    }

    // "1.4 GB" for the section line, reusing Fmt.Bytes so the page's sizes all read the
    // same way. CellMb is the inverse of the formatting ReportDoc does; both sides are ours,
    // so the round trip is exact enough for a heading.
    private static string SizeSummary(ReportTable table)
    {
        if (table.SizeColumn < 0 || table.SizeColumn >= table.Columns.Length) return "";
        double mb = 0;
        for (int r = 0; r < table.Rows.Count; r++)
        {
            string[] row = table.Rows[r];
            if (row == null || table.SizeColumn >= row.Length) continue;
            mb += CellMb(row[table.SizeColumn]);
        }
        if (mb <= 0) return "";
        return Fmt.Bytes((long)Math.Round(mb * 1048576.0));
    }

    private static double CellMb(string cell)
    {
        if (cell == null) return 0;
        string s = cell.Trim();
        double scale = 1;
        if (s.EndsWith(" GB", StringComparison.Ordinal))
        {
            scale = 1024;
            s = s.Substring(0, s.Length - 3);
        }
        else if (s.EndsWith(" MB", StringComparison.Ordinal))
        {
            s = s.Substring(0, s.Length - 3);
        }
        double d;
        if (!Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
        return d * scale;
    }

    private static bool IsNumericCell(string cell)
    {
        if (cell == null) return false;
        string s = cell.Trim();
        if (s.Length == 0) return false;
        if (s.EndsWith(" MB", StringComparison.Ordinal) || s.EndsWith(" GB", StringComparison.Ordinal))
            s = s.Substring(0, s.Length - 3);
        double d;
        return Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }

    // ---------------------------------------------------------------- painting

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        Line line = Items[e.Index] as Line;
        if (line == null) return;

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (SolidBrush clear = new SolidBrush(BackColor))
            g.FillRectangle(clear, e.Bounds);

        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        Rectangle row = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 1, Width - 6, e.Bounds.Height - 2);
        if (selected) Theme.FillRound(g, row, 8, Theme.AccentSoft);

        if (line.Kind == KindEmpty)
        {
            TextRenderer.DrawText(g, line.Text, Theme.FontUI,
                new Rectangle(SidePad, e.Bounds.Y, Math.Max(40, Width - SidePad * 2), e.Bounds.Height),
                Theme.TextMuted, Theme.LeftFlags);
            return;
        }

        if (line.Kind == KindSection)
        {
            // Drawn low inside its 24px row: the empty strip above is the section break, so
            // no divider row and no second row height are needed.
            TextRenderer.DrawText(g, line.Text, Theme.FontUIB,
                new Rectangle(SidePad, e.Bounds.Y + 7, Math.Max(40, Width - SidePad * 2), RowHeight - 7),
                selected ? Theme.AccentDark : Theme.Text, Theme.LeftFlags);
            return;
        }

        int[] xs;
        int[] ws;
        MeasureColumns(line.Cells.Length, out xs, out ws);
        bool header = line.Kind == KindHeader;
        for (int i = 0; i < line.Cells.Length; i++)
        {
            // Columns that do not fit are not drawn at all (MeasureColumns explains when
            // that happens); drawing them anyway would overlap the previous column.
            if (xs[i] >= Width) break;

            bool numeric = i > 0 && line.Numeric[i];
            TextFormatFlags flags = numeric ? RightFlags : Theme.LeftFlags;
            Rectangle cell = new Rectangle(xs[i], e.Bounds.Y, ws[i], e.Bounds.Height);

            if (header)
            {
                TextRenderer.DrawText(g, line.Cells[i], Theme.FontSmall, cell, Theme.TextMuted, flags);
                continue;
            }

            Color ink = i == 0 ? (selected ? Theme.AccentDark : Theme.Text) : Theme.TextMuted;
            TextRenderer.DrawText(g, line.Cells[i], numeric ? Theme.FontMono : Theme.FontUI, cell, ink, flags);
        }
    }

    // Name column 62% of the width, the rest shared by the value columns, 12px between
    // columns. The floors below are the concession to real data: system.json's Rows reach
    // 15+ columns, and 38% split fourteen ways is a 5px sliver per number. In that case the
    // name column gives ground first, and whatever still does not fit is dropped from the
    // painting (its text is still in the accessible text and in the CSV).
    //
    // Deliberately not called Layout: Control already has a Layout event, and shadowing it
    // earns a CS0108 warning on every build.
    private void MeasureColumns(int count, out int[] xs, out int[] ws)
    {
        xs = new int[count];
        ws = new int[count];
        int usable = Width - SidePad * 2 - 6;
        if (usable < 40) usable = 40;

        int space = usable - (count - 1) * ColGap;
        if (space < count * 8) space = count * 8;

        int nameW = count == 1 ? space : (int)Math.Round(space * 0.62);
        int each = 0;
        if (count > 1)
        {
            each = (space - nameW) / (count - 1);
            if (each < MinValueColumn)
            {
                each = MinValueColumn;
                nameW = space - each * (count - 1);
                if (nameW < MinNameColumn) nameW = MinNameColumn;
            }
        }

        int x = SidePad;
        for (int i = 0; i < count; i++)
        {
            ws[i] = i == 0 ? nameW : each;
            xs[i] = x;
            x += ws[i] + ColGap;
        }
    }
}
