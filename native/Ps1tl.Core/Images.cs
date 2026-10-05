namespace Ps1tl;

/// <summary>
/// Text baked into pictures: menus, the notebook, title and map labels.
/// Script.Images lists every picture. Claude looks at them in sheets; pictures with Japanese text become
/// ordinary lines (Key 'img:&lt;key&gt;', Image = key, no glyphs), edited and shared like dialogue. On build the
/// English is drawn into the same box with the picture's own palette (text colour and outline are taken from
/// the Japanese), so nothing else moves.
/// </summary>
public static class Images
{
    public const string Prefix = "img:";
    /// <summary>taller pictures are scenery/photos; text painted over a photo is not handled</summary>
    public const int MaxH = 64;

    static Font Big => Font.Load("en_pixel");
    static Font Small => Font.Load("en_small");

    public static void MergeCatalog(Script script, Dictionary<string, ImageEntry> catalog)
    {
        script.Images ??= new();
        foreach (var (k, v) in catalog)
        {
            if (script.Images.TryGetValue(k, out var e)) e.Refs = e.Refs.Union(v.Refs).Order(StringComparer.Ordinal).ToList();
            else script.Images[k] = new ImageEntry { W = v.W, H = v.H, Refs = v.Refs, Checked = false };
        }
    }

    public static (byte[,] Idx, ushort[] Pal) Pixels(IGame game, Dictionary<string, byte[]> files, ImageEntry e)
    {
        var r = e.Refs[0];
        int c = r.LastIndexOf(':');
        return game.ImageData(files[r[..c]], int.Parse(r[(c + 1)..]));
    }

    // --- pictures -> raster / PNG ----------------------------------------------------------------------------
    public static uint Color(ushort c) => c == 0 ? 0u
        : Raster.Rgb((c & 31) << 3, (c >> 5 & 31) << 3, (c >> 10 & 31) << 3);

    public static Raster ToRaster(byte[,] idx, ushort[] pal)
    {
        int h = idx.GetLength(0), w = idx.GetLength(1);
        var r = new Raster(w, h, 0);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) r.Px[y * w + x] = Color(pal[idx[y, x]]);
        return r;
    }

    public static byte[] Png(byte[,] idx, ushort[] pal, int scale = 2)
    {
        var src = ToRaster(idx, pal);
        var bg = new Raster(src.W, src.H, Raster.Rgb(24, 24, 32));
        bg.Blit(src, 0, 0);
        return bg.Scale(scale).Png();
    }

    /// <summary>numbered rows, each picture shown on black and on white (dark or light text both readable)</summary>
    public static byte[] Sheet(IList<(byte[,] Idx, ushort[] Pal)> pics)
    {
        const int s = 2;
        var ims = pics.Select(p => ToRaster(p.Idx, p.Pal).Scale(s)).ToList();
        int h = ims.Sum(r => r.H + 6), w = 44 + ims.Max(r => r.W * 2 + 12);
        var outp = new Raster(w, h, Raster.Rgb(90, 90, 90));
        int y = 0;
        for (int n = 0; n < ims.Count; n++)
        {
            var big = ims[n];
            outp.FillRect(44, y, big.W, big.H, Raster.Rgb(0, 0, 0)); outp.Blit(big, 44, y);
            outp.FillRect(44 + big.W + 6, y, big.W, big.H, Raster.Rgb(255, 255, 255)); outp.Blit(big, 44 + big.W + 6, y);
            outp.Text(4, y + 2, (n + 1).ToString(), Raster.Rgb(255, 255, 0), Big);
            y += big.H + 6;
        }
        return outp.Png();
    }

    /// <summary>rough letter budget of a picture: small font ~4px per letter, wrapped over the lines that fit</summary>
    public static int Budget(ImageEntry e) => Math.Max(1, (e.W - 2) / 4) * Math.Max(1, e.H / 9);

    // --- drawing English into a picture ------------------------------------------------------------------
    static int Lum(ushort c) => (c & 31) * 3 + (c >> 5 & 31) * 6 + (c >> 10 & 31);

    /// <summary>-> (background index, text index, outline index or null, text bbox (x0, y0, x1, y1))</summary>
    public static (int Bg, int Fill, int? Outline, (int X0, int Y0, int X1, int Y1) Box) Style(byte[,] idx, ushort[] pal)
    {
        int h = idx.GetLength(0), w = idx.GetLength(1);
        var present = new SortedSet<int>();
        foreach (var v in idx) present.Add(v);
        int bg;
        var clear = present.Where(i => pal[i] == 0).ToList();
        if (clear.Count > 0) bg = clear[0];
        else
        {
            var counts = new int[256];
            for (int x = 0; x < w; x++) { counts[idx[0, x]]++; counts[idx[h - 1, x]]++; }
            for (int y = 0; y < h; y++) { counts[idx[y, 0]]++; counts[idx[y, w - 1]]++; }
            bg = Array.IndexOf(counts, counts.Max());
        }
        bool Ink(int y, int x) => y >= 0 && x >= 0 && y < h && x < w && idx[y, x] != bg;
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        var n = new Dictionary<int, double>(); var e = new Dictionary<int, double>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!Ink(y, x)) continue;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                int c = idx[y, x];
                n[c] = n.GetValueOrDefault(c) + 1;
                bool edge = !Ink(y - 1, x) || !Ink(y + 1, x) || !Ink(y, x - 1) || !Ink(y, x + 1);
                if (edge) e[c] = e.GetValueOrDefault(c) + 1;
            }
        if (x1 < 0) return (bg, bg, null, (0, 0, w, h));
        var box = (x0, y0, x1 + 1, y1 + 1);
        // split the text colours into a dark and a light group (Otsu on brightness); the group that hugs the
        // background is the outline, the other the letters. Similar edge share -> plain text, no outline.
        var cols = n.Keys.Order().OrderBy(c => Lum(pal[c])).ToList();   // stable: by index, then brightness
        var N = cols.Select(c => n[c]).ToArray();
        var E = cols.Select(c => e.GetValueOrDefault(c)).ToArray();
        var L = cols.Select(c => (double)Lum(pal[c])).ToArray();
        int fill = cols[Array.IndexOf(N, N.Max())];
        int? outline = null;
        if (cols.Count > 1)
        {
            double Var(int a, int b)
            {
                double sw = 0, m = 0;
                for (int j = a; j < b; j++) { sw += N[j]; m += L[j] * N[j]; }
                m /= sw;
                double v = 0;
                for (int j = a; j < b; j++) v += (L[j] - m) * (L[j] - m) * N[j];
                return sw * (v / sw);
            }
            int k = Enumerable.Range(1, cols.Count - 1).MinBy(k => Var(0, k) + Var(k, cols.Count));
            int[] g0 = Enumerable.Range(0, k).ToArray(), g1 = Enumerable.Range(k, cols.Count - k).ToArray();
            double Share(int[] g) => g.Sum(j => E[j]) / g.Sum(j => N[j]);
            double s0 = Share(g0), s1 = Share(g1);
            if (Math.Abs(s0 - s1) >= 0.15)
            {
                var (og, fg) = s0 > s1 ? (g0, g1) : (g1, g0);
                int oj = og.MaxBy(j => N[j]);
                outline = cols[oj];
                double total = fg.Sum(j => N[j]);
                var big = fg.Where(j => N[j] >= 0.1 * total).DefaultIfEmpty(fg.MaxBy(j => N[j])).ToList();
                int lo = Lum(pal[outline.Value]);
                fill = cols[big.MaxBy(j => Math.Abs(L[j] - lo))];
            }
        }
        return (bg, fill, outline, box);
    }

    static Func<string, int> Advance(Font f, int gap) => s => s.Length == 0 ? 0 : s.Sum(c => f.Width(c) + gap) - gap;

    /// <summary>word wrap (and explicit line breaks) -> lines, or null if a word is wider than width</summary>
    static List<string>? Layout(string text, Font f, int gap, int width)
    {
        var adv = Advance(f, gap);
        var lines = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            var cur = "";
            foreach (var word in para.Split(' '))
            {
                if (adv(word) > width) return null;
                var t = cur.Length > 0 ? cur + " " + word : word;
                if (adv(t) <= width) cur = t;
                else { lines.Add(cur); cur = word; }
            }
            lines.Add(cur);
        }
        return lines;
    }

    /// <summary>-> (new indices, fits). Same size as the original; English replaces everything but the
    /// background. Tall narrow pictures (vertical Japanese) get the English rotated to read top to bottom.</summary>
    public static (byte[,] Idx, bool Fits) Draw(byte[,] idx, ushort[] pal, string text)
    {
        int h = idx.GetLength(0), w = idx.GetLength(1);
        if (h >= 2 * w && h >= 16)
        {
            var rot = new byte[w, h];   // counter-clockwise
            for (int i = 0; i < w; i++) for (int j = 0; j < h; j++) rot[i, j] = idx[j, w - 1 - i];
            var (nw, fits) = DrawFlat(rot, pal, text);
            var back = new byte[h, w];  // clockwise
            for (int i = 0; i < h; i++) for (int j = 0; j < w; j++) back[i, j] = nw[w - 1 - j, i];
            return (back, fits);
        }
        return DrawFlat(idx, pal, text);
    }

    /// <summary>
    /// A button: letters inside a ring of the letter colour (Gunparade's menu plates, a gradient inside a white
    /// border). -> (the picture with the letters painted over from the plate above/below them, inner box), or null.
    /// </summary>
    static (byte[,] Base, (int X0, int Y0, int X1, int Y1) Box, int Fill)? Plate(byte[,] idx, ushort[] pal, int bg,
        (int X0, int Y0, int X1, int Y1) ink)
    {
        int h = idx.GetLength(0), w = idx.GetLength(1);
        int lum = 0;   // the brightest colour used: letters and ring are near it
        foreach (var v in idx) if (v != bg) lum = Math.Max(lum, Lum(pal[v]));
        bool Light(int y, int x) => idx[y, x] != bg && Lum(pal[idx[y, x]]) * 4 >= lum * 3;
        var comp = new int[h, w];
        var boxes = new List<(int X0, int Y0, int X1, int Y1)> { default };
        for (int sy = 0; sy < h; sy++)
            for (int sx = 0; sx < w; sx++)
            {
                if (comp[sy, sx] != 0 || !Light(sy, sx)) continue;
                int id = boxes.Count, bx0 = sx, by0 = sy, bx1 = sx, by1 = sy;
                var q = new Queue<(int, int)>([(sy, sx)]);
                comp[sy, sx] = id;
                while (q.Count > 0)
                {
                    var (y, x) = q.Dequeue();
                    bx0 = Math.Min(bx0, x); bx1 = Math.Max(bx1, x); by0 = Math.Min(by0, y); by1 = Math.Max(by1, y);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int ny = y + dy, nx = x + dx;
                            if (ny < 0 || nx < 0 || ny >= h || nx >= w || comp[ny, nx] != 0 || !Light(ny, nx)) continue;
                            comp[ny, nx] = id; q.Enqueue((ny, nx));
                        }
                }
                boxes.Add((bx0, by0, bx1 + 1, by1 + 1));
            }
        int ring = Enumerable.Range(1, boxes.Count - 1).FirstOrDefault(i =>
            boxes[i].X1 - boxes[i].X0 >= (ink.X1 - ink.X0) * 0.9 && boxes[i].Y1 - boxes[i].Y0 >= (ink.Y1 - ink.Y0) * 0.9 && boxes[i].Y1 - boxes[i].Y0 >= 12);
        if (ring == 0) return null;
        var (rx0, ry0, rx1, ry1) = boxes[ring];
        var inner = (X0: rx0 + 2, Y0: ry0 + 2, X1: rx1 - 2, Y1: ry1 - 2);
        var counts = new Dictionary<int, int>();
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (comp[y, x] == ring) counts[idx[y, x]] = counts.GetValueOrDefault(idx[y, x]) + 1;
        // old letters: light pixels inside the ring (also those touching it, away from its rounded corners), plus two pixels
        // around them (shading)
        bool Letter(int y, int x) => comp[y, x] != 0 && (comp[y, x] != ring || (x - rx0 >= 3 && rx1 - 1 - x >= 3 && y - ry0 >= 3 && ry1 - 1 - y >= 3));
        var mask = new bool[h, w];
        for (int y = inner.Y0; y < inner.Y1; y++)
            for (int x = inner.X0; x < inner.X1; x++)
                for (int dy = -2; dy <= 2 && !mask[y, x]; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        if (y + dy >= 0 && y + dy < h && x + dx >= 0 && x + dx < w && Letter(y + dy, x + dx)) { mask[y, x] = true; break; }
        var plateCols = new HashSet<int>();
        for (int y = inner.Y0; y < inner.Y1; y++) for (int x = inner.X0; x < inner.X1; x++) if (!mask[y, x]) plateCols.Add(idx[y, x]);
        if (plateCols.Count == 0) return null;
        static (int R, int G, int B) Rgb(ushort c) => (c & 31, c >> 5 & 31, c >> 10 & 31);
        static double Dist((int R, int G, int B) a, (double R, double G, double B) b) =>
            (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);
        var outp = (byte[,])idx.Clone();
        for (int y = inner.Y0; y < inner.Y1; y++)
            for (int x = inner.X0; x < inner.X1; x++)
            {
                if (!mask[y, x]) continue;
                // the plate is a left-to-right gradient: blend the row's clean pixels either side, in the plate's colours
                int l = x, r = x;
                while (l >= inner.X0 && mask[y, l]) l--;
                while (r < inner.X1 && mask[y, r]) r++;
                if (l < inner.X0 && r >= inner.X1) { if (Near(y, x, 1, 0, inner.Y0, inner.Y1) is int v) outp[y, x] = (byte)v; continue; }
                if (l < inner.X0 || r >= inner.X1) { outp[y, x] = idx[y, l < inner.X0 ? r : l]; continue; }
                double t = (x - l) / (double)(r - l);
                var (cl, cr) = (Rgb(pal[idx[y, l]]), Rgb(pal[idx[y, r]]));
                var want = (cl.R + (cr.R - cl.R) * t, cl.G + (cr.G - cl.G) * t, cl.B + (cr.B - cl.B) * t);
                outp[y, x] = (byte)plateCols.MinBy(c => Dist(Rgb(pal[c]), want));
            }
        return (outp, inner, counts.MaxBy(kv => kv.Value).Key);

        int? Near(int y, int x, int dy, int dx, int lo, int hi)   // nearest unmasked pixel along (dy, dx), within [lo, hi)
        {
            for (int d = 1; d < Math.Max(h, w); d++)
            {
                int a = (dy != 0 ? y : x) - d, b = (dy != 0 ? y : x) + d;
                if (a >= lo && !mask[y - d * dy, x - d * dx]) return idx[y - d * dy, x - d * dx];
                if (b < hi && !mask[y + d * dy, x + d * dx]) return idx[y + d * dy, x + d * dx];
                if (a < lo && b >= hi) return null;
            }
            return null;
        }
    }

    static (byte[,], bool) DrawFlat(byte[,] idx, ushort[] pal, string text)
    {
        int h = idx.GetLength(0), w = idx.GetLength(1);
        var (bg, fill, outline, (x0, y0, x1, y1)) = Style(idx, pal);
        var plate = Plate(idx, pal, bg, (x0, y0, x1, y1));
        if (plate != null) { outline = null; (x0, y0, x1, y1) = plate.Value.Box; fill = plate.Value.Fill; }
        int o = outline != null ? 1 : 0;
        int roomW = plate != null ? x1 - x0 : w - 2 * o, roomH = plate != null ? y1 - y0 : h;
        text = new string(text.Select(c => Big.Has(c) || c == '\n' ? c : '?').ToArray());
        // roomiest first: big font, small font, then letters closer together (outlines may touch)
        var tries = new List<(Font F, int Cap, int Desc, int Gap)> { (Big, 9, 3, 1 + o), (Small, 5, 2, 1 + o) };
        if (o == 1) { tries.Add((Big, 9, 3, 1)); tries.Add((Small, 5, 2, 1)); }
        (Font F, List<string> Lines, int Cap, int Lh, int Gap)? choice = null;
        foreach (var (f, cap, desc, gap) in tries)
        {
            var lines = Layout(text, f, gap, roomW);
            if (lines == null) continue;
            int lh = cap + desc + 2 * o;
            if (lh * lines.Count - desc <= roomH) { choice = (f, lines, cap, lh, gap); break; }
        }
        bool fits = choice != null;
        var (font, ls, cp, lineH, g) = choice ?? (Small, [text.Replace('\n', ' ')], 5, 7 + 2 * o, 1);   // clip: small, tight, one line
        var adv = Advance(font, g);
        int block = lineH * ls.Count - (lineH - cp - 2 * o);
        int top = (int)Math.Clamp((y0 + y1) / 2.0 - block / 2.0, 0, Math.Max(0, h - block));
        var ink = new bool[h, w];
        for (int li = 0; li < ls.Count; li++)
        {
            int lw = adv(ls[li]);
            int x = x0 <= 2 && plate == null ? o + x0 : (int)Math.Clamp((x0 + x1) / 2.0 - lw / 2.0, o, Math.Max(o, w - lw - o));
            int y = top + o + li * lineH;
            foreach (var c in ls[li])
            {
                var rows = font[c];
                for (int ry = 0; ry < rows.Length; ry++)
                    for (int rx = 0; rx < rows[ry].Length; rx++)
                        if (rows[ry][rx] == '#' && y + ry < h && x + rx >= 0 && x + rx < w) ink[y + ry, x + rx] = true;
                x += rows.Max(r => r.Length) + g;
            }
        }
        var nw = new byte[h, w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = plate?.Base[y, x] ?? (byte)bg;
                if (outline != null)
                    for (int dy = -1; dy <= 1 && v == bg; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if (y + dy >= 0 && y + dy < h && x + dx >= 0 && x + dx < w && ink[y + dy, x + dx]) { v = (byte)outline; break; }
                if (ink[y, x]) v = (byte)fill;
                nw[y, x] = v;
            }
        return (nw, fits);
    }

    /// <summary>draw English into every picture line that has it -> ({path: new bytes}, [keys that did not fit])</summary>
    public static (Dictionary<string, byte[]> Files, List<string> Tight) Apply(IGame game, Dictionary<string, byte[]> files, Script script,
        Func<Line, string?> enFor, bool labels = false)
    {
        var outp = new Dictionary<string, byte[]>();
        var tight = new List<string>();
        foreach (var l in script.Lines)
        {
            if (!l.IsImage) continue;
            var text = labels ? l.Key.Substring(Prefix.Length, Math.Min(6, l.Key.Length - Prefix.Length)) : enFor(l);
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var r in script.Images![l.Image!].Refs)
            {
                int c = r.LastIndexOf(':');
                string path = r[..c]; int i = int.Parse(r[(c + 1)..]);
                var d = outp.GetValueOrDefault(path) ?? files[path];
                var (idx, pal) = game.ImageData(d, i);
                var (nw, fits) = Draw(idx, pal, text);
                if (!fits && !tight.Contains(l.Key)) tight.Add(l.Key);
                outp[path] = game.PutImage(d, i, nw);
            }
        }
        return (outp, tight);
    }
}
