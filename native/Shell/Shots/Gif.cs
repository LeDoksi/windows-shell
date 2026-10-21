using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace WindowsShell.Shots;

/// «Сохранить как GIF» для коротких записей (design-v2 п. 4): 10 кадров в секунду, не шире
/// 800 px. Кодировщик свой: общая палитра из 256 цветов, подобранная по кадрам записи (без
/// ряби от смешивания точек, экран остаётся чётким), и в каждом кадре только то, что поменялось.
public static class Gif
{
    public const int MaxSeconds = 60;
    const int Fps = 10, MaxWidth = 800;

    /// Длина записи.
    public static async Task<TimeSpan> Duration(string video)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(video));
        return clip.OriginalDuration;
    }

    /// Сделать GIF рядом с записью. Возвращает путь или null.
    public static async Task<string?> FromVideo(string video)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(video));
        var duration = clip.OriginalDuration;
        if (duration > TimeSpan.FromSeconds(MaxSeconds)) duration = TimeSpan.FromSeconds(MaxSeconds);
        var props = clip.GetVideoEncodingProperties();
        int w = (int)props.Width, h = (int)props.Height;
        if (w > MaxWidth) { h = (int)Math.Round(h * (double)MaxWidth / w); w = MaxWidth; }
        w = Math.Max(2, w);
        h = Math.Max(2, h);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var times = new List<TimeSpan>();
        for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromMilliseconds(1000 / Fps)) times.Add(t);
        if (times.Count == 0) return null;

        // Кадры кусками: сразу все держать в памяти дорого.
        var frames = new List<byte[]>(times.Count);
        for (var i = 0; i < times.Count; i += 50)
        {
            var part = times.Skip(i).Take(50).ToList();
            var images = await composition.GetThumbnailsAsync(part, w, h, VideoFramePrecision.NearestFrame);
            foreach (var image in images)
            {
                using (image)
                {
                    var decoder = await BitmapDecoder.CreateAsync(image);
                    var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                        new BitmapTransform { ScaledWidth = (uint)w, ScaledHeight = (uint)h }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                    frames.Add(pixels.DetachPixelData());
                }
            }
        }
        var path = ShotFiles.GifFor(video);
        await Task.Run(() => Write(path, w, h, frames, 100 / Fps));
        Log.Write($"GIF готов: {Path.GetFileName(path)}, {w}×{h}, кадров {frames.Count}, {new FileInfo(path).Length / 1024} КБ");
        return path;
    }

    // ---------- кодировщик ----------

    static void Write(string path, int w, int h, List<byte[]> frames, int delay)
    {
        var palette = Palette(frames);
        var lookup = Lookup(palette);
        using var f = new BufferedStream(File.Create(path));
        void U16(int v) { f.WriteByte((byte)v); f.WriteByte((byte)(v >> 8)); }
        f.Write("GIF89a"u8);
        U16(w);
        U16(h);
        f.WriteByte(0xF7); // общая палитра на 256 цветов
        f.WriteByte(0);
        f.WriteByte(0);
        foreach (var c in palette) { f.WriteByte((byte)(c >> 16)); f.WriteByte((byte)(c >> 8)); f.WriteByte((byte)c); }
        // Повторять бесконечно.
        f.Write([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);

        byte[]? prev = null;
        var pending = new List<(byte[] Indices, int X, int Y, int W, int H, int Delay)>();
        foreach (var bgra in frames)
        {
            var idx = new byte[w * h];
            for (var i = 0; i < idx.Length; i++)
            {
                var b = bgra[i * 4]; var g = bgra[i * 4 + 1]; var r = bgra[i * 4 + 2];
                idx[i] = lookup[(r >> 3) << 10 | (g >> 3) << 5 | (b >> 3)];
            }
            // Только изменившийся прямоугольник; не изменилось ничего — дольше показываем прошлый кадр.
            int x0 = 0, y0 = 0, x1 = w - 1, y1 = h - 1;
            if (prev != null)
            {
                x0 = w; y0 = h; x1 = -1; y1 = -1;
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    if (idx[y * w + x] == prev[y * w + x]) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
                if (x1 < 0)
                {
                    var last = pending[^1];
                    pending[^1] = last with { Delay = last.Delay + delay };
                    continue;
                }
            }
            var cw = x1 - x0 + 1;
            var ch = y1 - y0 + 1;
            var sub = new byte[cw * ch];
            for (var y = 0; y < ch; y++) Buffer.BlockCopy(idx, (y0 + y) * w + x0, sub, y * cw, cw);
            pending.Add((sub, x0, y0, cw, ch, delay));
            prev = idx;
        }
        foreach (var (indices, x, y, fw, fh, d) in pending)
        {
            f.Write([0x21, 0xF9, 0x04, 0x04 /* не стирать прошлый кадр */]);
            U16(d);
            f.WriteByte(0);
            f.WriteByte(0);
            f.WriteByte(0x2C);
            U16(x); U16(y); U16(fw); U16(fh);
            f.WriteByte(0);
            Lzw(f, indices);
        }
        f.WriteByte(0x3B);
    }

    /// 256 цветов по кадрам записи (каждый 10-й кадр, каждая 4-я точка): самые частые
    /// оттенки 5:5:5, похожие сливаются. Простой экран — точные цвета, видео — близкие.
    static int[] Palette(List<byte[]> frames)
    {
        var counts = new int[32768];
        for (var fi = 0; fi < frames.Count; fi += Math.Max(1, frames.Count / 30))
        {
            var px = frames[fi];
            for (var i = 0; i < px.Length; i += 16)
                counts[(px[i + 2] >> 3) << 10 | (px[i + 1] >> 3) << 5 | (px[i] >> 3)]++;
        }
        var order = Enumerable.Range(0, 32768).Where(i => counts[i] > 0).OrderByDescending(i => counts[i]).ToList();
        var chosen = new List<int>(256);
        // Сначала берём непохожие (расстояние больше 2 ступеней), потом добираем оставшиеся.
        foreach (var c in order)
        {
            if (chosen.Count >= 256) break;
            if (chosen.All(p => Dist555(p, c) > 6)) chosen.Add(c);
        }
        foreach (var c in order)
        {
            if (chosen.Count >= 256) break;
            if (!chosen.Contains(c)) chosen.Add(c);
        }
        while (chosen.Count < 256) chosen.Add(0);
        return chosen.Select(c =>
        {
            int r = (c >> 10) & 31, g = (c >> 5) & 31, b = c & 31;
            return (r << 3 | r >> 2) << 16 | (g << 3 | g >> 2) << 8 | (b << 3 | b >> 2);
        }).ToArray();
    }

    static int Dist555(int a, int b)
    {
        int dr = ((a >> 10) & 31) - ((b >> 10) & 31), dg = ((a >> 5) & 31) - ((b >> 5) & 31), db = (a & 31) - (b & 31);
        return dr * dr + dg * dg + db * db;
    }

    /// Для каждого цвета 5:5:5 — ближайший в палитре.
    static byte[] Lookup(int[] palette)
    {
        var table = new byte[32768];
        Parallel.For(0, 32768, c =>
        {
            int r = ((c >> 10) & 31) << 3 | 4, g = ((c >> 5) & 31) << 3 | 4, b = (c & 31) << 3 | 4;
            var best = 0;
            var bestD = int.MaxValue;
            for (var i = 0; i < palette.Length; i++)
            {
                int dr = ((palette[i] >> 16) & 255) - r, dg = ((palette[i] >> 8) & 255) - g, db = (palette[i] & 255) - b;
                var d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (d < bestD) { bestD = d; best = i; }
            }
            table[c] = (byte)best;
        });
        return table;
    }

    /// Сжатие LZW, как требует GIF (код от 9 до 12 бит, кусками по 255 байт).
    static void Lzw(Stream output, byte[] pixels)
    {
        const int initBits = 9, clear = 256, eoi = 257, maxCodes = 4096;
        output.WriteByte(8);
        var block = new byte[255];
        var blockLen = 0;
        int accum = 0, bits = 0, nBits = initBits, maxCode = (1 << nBits) - 1, free = clear + 2;
        var clearFlag = false;
        var dict = new Dictionary<int, int>();

        void Byte(byte b)
        {
            block[blockLen++] = b;
            if (blockLen == 255) { output.WriteByte(255); output.Write(block, 0, 255); blockLen = 0; }
        }

        void Output(int code)
        {
            accum |= code << bits;
            bits += nBits;
            while (bits >= 8) { Byte((byte)accum); accum >>= 8; bits -= 8; }
            if (free > maxCode || clearFlag)
            {
                if (clearFlag) { nBits = initBits; maxCode = (1 << nBits) - 1; clearFlag = false; }
                else { nBits++; maxCode = nBits == 12 ? maxCodes : (1 << nBits) - 1; }
            }
            if (code == eoi)
            {
                while (bits > 0) { Byte((byte)accum); accum >>= 8; bits -= 8; }
                bits = 0;
            }
        }

        Output(clear);
        int ent = pixels[0];
        for (var i = 1; i < pixels.Length; i++)
        {
            int c = pixels[i];
            var key = (c << 12) | ent;
            if (dict.TryGetValue(key, out var code)) { ent = code; continue; }
            Output(ent);
            ent = c;
            if (free < maxCodes) dict[key] = free++;
            else
            {
                dict.Clear();
                free = clear + 2;
                clearFlag = true;
                Output(clear);
            }
        }
        Output(ent);
        Output(eoi);
        if (blockLen > 0) { output.WriteByte((byte)blockLen); output.Write(block, 0, blockLen); }
        output.WriteByte(0);
    }
}
