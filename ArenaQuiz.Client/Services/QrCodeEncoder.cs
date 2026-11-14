using System.Text;

namespace ArenaQuiz.Client.Services;

/// <summary>
/// Pure C# ISO/IEC 18004 QR Code encoder (Version 4-L, 33x33 modules, up to 78 UTF-8 bytes)
/// for generating scannable projector join QR codes without external dependencies.
/// </summary>
public static class QrCodeEncoder
{
    private const int Size = 33;
    private const int DataCapacity = 80;
    private const int EcCount = 20;

    public static bool[,] Encode(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length > 78)
        {
            utf8 = utf8[..78];
        }

        byte[] dataCodewords = BuildDataCodewords(utf8);
        byte[] ecCodewords = ComputeReedSolomon(dataCodewords, EcCount);
        byte[] allBytes = [.. dataCodewords, .. ecCodewords];

        var grid = new bool[Size, Size];
        var reserved = new bool[Size, Size];

        PlaceFinderPattern(grid, reserved, 0, 0);
        PlaceFinderPattern(grid, reserved, Size - 7, 0);
        PlaceFinderPattern(grid, reserved, 0, Size - 7);
        PlaceAlignmentPattern(grid, reserved, 26, 26);
        PlaceTimingAndDarkModule(grid, reserved);
        ReserveFormatAreas(reserved);

        PlaceDataBits(grid, reserved, allBytes);

        // Apply Mask 0: (row + col) % 2 == 0
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (!reserved[r, c] && (r + c) % 2 == 0)
                {
                    grid[r, c] = !grid[r, c];
                }
            }
        }

        // Format info for Error Correction L (01) + Mask 0 (000) => 0x77C4 (111011111000100)
        PlaceFormatInfo(grid, 0b111011111000100);
        return grid;
    }

    private static byte[] BuildDataCodewords(byte[] payload)
    {
        var bits = new List<int>(DataCapacity * 8);
        void AppendBits(int val, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add((val >> i) & 1);
            }
        }

        AppendBits(0b0100, 4); // Byte mode
        AppendBits(payload.Length, 8);
        foreach (byte b in payload)
        {
            AppendBits(b, 8);
        }

        int maxBits = DataCapacity * 8;
        int terminator = Math.Min(4, maxBits - bits.Count);
        AppendBits(0, terminator);
        while (bits.Count % 8 != 0)
        {
            bits.Add(0);
        }

        byte[] result = new byte[DataCapacity];
        int byteIdx = 0;
        for (int i = 0; i < bits.Count; i += 8)
        {
            int val = 0;
            for (int b = 0; b < 8; b++)
            {
                val = (val << 1) | bits[i + b];
            }
            result[byteIdx++] = (byte)val;
        }

        byte[] padBytes = [0xEC, 0x11];
        int p = 0;
        while (byteIdx < DataCapacity)
        {
            result[byteIdx++] = padBytes[p++ % 2];
        }

        return result;
    }

    private static byte[] ComputeReedSolomon(byte[] data, int ecLength)
    {
        byte[] gen = new byte[ecLength + 1];
        gen[0] = 1;
        for (int i = 0; i < ecLength; i++)
        {
            byte root = GfPow(2, i);
            for (int j = i + 1; j >= 1; j--)
            {
                gen[j] = (byte)(gen[j] ^ GfMultiply(gen[j - 1], root));
            }
        }

        byte[] work = new byte[data.Length + ecLength];
        Array.Copy(data, work, data.Length);

        for (int i = 0; i < data.Length; i++)
        {
            byte factor = work[i];
            if (factor == 0) continue;
            for (int j = 0; j < gen.Length; j++)
            {
                work[i + j] ^= GfMultiply(gen[j], factor);
            }
        }

        return work[data.Length..];
    }

    private static byte GfMultiply(byte a, byte b)
    {
        int res = 0;
        int aa = a;
        int bb = b;
        while (bb > 0)
        {
            if ((bb & 1) != 0) res ^= aa;
            aa <<= 1;
            if ((aa & 0x100) != 0) aa ^= 0x11D;
            bb >>= 1;
        }
        return (byte)res;
    }

    private static byte GfPow(byte baseVal, int exp)
    {
        byte r = 1;
        for (int i = 0; i < exp; i++) r = GfMultiply(r, baseVal);
        return r;
    }

    private static void PlaceFinderPattern(bool[,] grid, bool[,] reserved, int topRow, int leftCol)
    {
        for (int r = -1; r <= 7; r++)
        {
            for (int c = -1; c <= 7; c++)
            {
                int rr = topRow + r;
                int cc = leftCol + c;
                if (rr < 0 || rr >= Size || cc < 0 || cc >= Size) continue;
                reserved[rr, cc] = true;
                bool dark = r is >= 0 and <= 6 && c is >= 0 and <= 6 &&
                    (r == 0 || r == 6 || c == 0 || c == 6 || (r is >= 2 and <= 4 && c is >= 2 and <= 4));
                grid[rr, cc] = dark;
            }
        }
    }

    private static void PlaceAlignmentPattern(bool[,] grid, bool[,] reserved, int centerR, int centerC)
    {
        for (int r = -2; r <= 2; r++)
        {
            for (int c = -2; c <= 2; c++)
            {
                int rr = centerR + r;
                int cc = centerC + c;
                reserved[rr, cc] = true;
                grid[rr, cc] = Math.Max(Math.Abs(r), Math.Abs(c)) is 0 or 2;
            }
        }
    }

    private static void PlaceTimingAndDarkModule(bool[,] grid, bool[,] reserved)
    {
        for (int i = 8; i < Size - 8; i++)
        {
            reserved[6, i] = true;
            grid[6, i] = i % 2 == 0;
            reserved[i, 6] = true;
            grid[i, 6] = i % 2 == 0;
        }
        reserved[25, 8] = true;
        grid[25, 8] = true;
    }

    private static void ReserveFormatAreas(bool[,] reserved)
    {
        for (int i = 0; i <= 8; i++)
        {
            reserved[8, i] = true;
            reserved[i, 8] = true;
        }
        for (int i = Size - 8; i < Size; i++)
        {
            reserved[8, i] = true;
        }
        for (int i = Size - 7; i < Size; i++)
        {
            reserved[i, 8] = true;
        }
    }

    private static void PlaceDataBits(bool[,] grid, bool[,] reserved, byte[] allBytes)
    {
        int bitIndex = 0;
        int totalBits = allBytes.Length * 8;
        bool goingUp = true;

        for (int col = Size - 1; col >= 1; col -= 2)
        {
            if (col == 6) col--;
            for (int step = 0; step < Size; step++)
            {
                int row = goingUp ? (Size - 1 - step) : step;
                for (int offset = 0; offset < 2; offset++)
                {
                    int c = col - offset;
                    if (reserved[row, c]) continue;
                    bool bit = false;
                    if (bitIndex < totalBits)
                    {
                        byte current = allBytes[bitIndex / 8];
                        bit = ((current >> (7 - (bitIndex % 8))) & 1) != 0;
                        bitIndex++;
                    }
                    grid[row, c] = bit;
                }
            }
            goingUp = !goingUp;
        }
    }

    private static void PlaceFormatInfo(bool[,] grid, int formatBits)
    {
        for (int i = 0; i < 15; i++)
        {
            bool bit = ((formatBits >> (14 - i)) & 1) != 0;

            if (i < 6) grid[8, i] = bit;
            else if (i < 8) grid[8, i + 1] = bit;
            else grid[8, Size - 15 + i] = bit;

            if (i < 7) grid[Size - 1 - i, 8] = bit;
            else if (i == 7) grid[8, 8] = bit;
            else grid[14 - i, 8] = bit;
        }
    }
}

