using System.IO.MemoryMappedFiles;

namespace WindowsShell.Perf;

/// Общая память мониторинга MSI Afterburner (MAHM): её читает кто угодно, без прав администратора.
/// Afterburner сам читает датчики своим драйвером; у кого он запущен, берём температуры отсюда.
internal static class Afterburner
{
    /// Самая высокая температура источника: `src` — номер источника (0x80 процессор, 0x00 видеокарта),
    /// `prefix` — начало имени («CPU» → «CPU temperature», «CPU1 temperature»…).
    public static double? Temperature(uint src, string prefix)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("MAHMSharedMemory", MemoryMappedFileRights.Read);
            using var v = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (v.ReadUInt32(0) != 0x4D41484D) return null; // 'MAHM'; 0xDEAD — Afterburner закрыт
            var header = v.ReadUInt32(8);
            var count = v.ReadUInt32(12);
            var size = v.ReadUInt32(16);
            if (count > 512 || size < 1324) return null;
            double? best = null;
            for (var i = 0; i < count; i++)
            {
                long e = header + i * (long)size;
                var id = v.ReadUInt32(e + 1320);
                var name = ReadAnsi(v, e, 260);
                var named = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith("temperature", StringComparison.OrdinalIgnoreCase)
                    && name[prefix.Length..^"temperature".Length].Trim().All(char.IsDigit);
                if (!named && !(id == src && name.Contains("temp", StringComparison.OrdinalIgnoreCase))) continue;
                var t = v.ReadSingle(e + 1300);
                if (t is > 1 and < 130 && (best is null || t > best)) best = t;
            }
            return best;
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception e)
        {
            Log.Write($"Afterburner: {e.Message}");
            return null;
        }
    }

    static string ReadAnsi(MemoryMappedViewAccessor v, long at, int max)
    {
        var bytes = new byte[max];
        v.ReadArray(at, bytes, 0, max);
        var n = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.ASCII.GetString(bytes, 0, n < 0 ? max : n);
    }
}
