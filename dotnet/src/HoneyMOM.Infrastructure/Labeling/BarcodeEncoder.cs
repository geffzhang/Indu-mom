using System.Text;

namespace HoneyMOM.Infrastructure.Labeling;

public static class BarcodeEncoder
{
    private static readonly GaloisField QrField = new(0x11d);
    private static readonly GaloisField DataMatrixField = new(0x12d);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly string[] Code128Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112"
    ];

    private static readonly int[][] QrEccPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30]
    ];

    private static readonly int[][] QrNumberOfBlocks =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81]
    ];

    private static readonly int[] QrFormatBits = [1, 0, 3, 2];
    private const string QrAlphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private static readonly (int Size, int RegionDataSize, int RegionsPerEdge, int DataCodewords, int ErrorCodewords, int Blocks)[] DataMatrixSizes =
    [
        (10, 8, 1, 3, 5, 1), (12, 10, 1, 5, 7, 1), (14, 12, 1, 8, 10, 1), (16, 14, 1, 12, 12, 1),
        (18, 16, 1, 18, 14, 1), (20, 18, 1, 22, 18, 1), (22, 20, 1, 30, 20, 1), (24, 22, 1, 36, 24, 1),
        (26, 24, 1, 44, 28, 1), (32, 14, 2, 62, 36, 1), (36, 16, 2, 86, 42, 1), (40, 18, 2, 114, 48, 1),
        (44, 20, 2, 144, 56, 1), (48, 22, 2, 174, 68, 1), (52, 24, 2, 204, 84, 2), (64, 14, 4, 280, 112, 2),
        (72, 16, 4, 368, 144, 4), (80, 18, 4, 456, 192, 4), (88, 20, 4, 576, 224, 4), (96, 22, 4, 696, 272, 4),
        (104, 24, 4, 816, 336, 6), (120, 18, 6, 1050, 408, 6), (132, 20, 6, 1304, 496, 8), (144, 22, 6, 1558, 620, 10)
    ];

    public static string EncodeCode128(string data, bool gs1)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var character in data)
        {
            if (character > byte.MaxValue)
            {
                throw new ArgumentException($"Code128 does not support character U+{(int)character:X4}; only Latin-1 is supported.", nameof(data));
            }
        }

        var value = data;
        if (gs1 && value.StartsWith('\x1d'))
        {
            value = value[1..];
        }

        var length = value.Length;
        bool IsFnc1(int index) => gs1 && value[index] == '\x1d';

        int DigitRun(int index)
        {
            var end = index;
            while (end < length && IsAsciiDigit(value[end]))
            {
                end++;
            }
            return end - index;
        }

        bool RunEnds(int index, int run) => index + run == length || IsFnc1(index + run);

        var initialRun = length == 0 ? 0 : DigitRun(0);
        var codeSet = initialRun >= 4
            || (initialRun == length && initialRun >= 2 && initialRun % 2 == 0)
            || (gs1 && initialRun >= 2 && initialRun % 2 == 0 && RunEnds(0, initialRun))
            ? 'C'
            : 'B';
        if (length > 0 && !IsFnc1(0) && ((value[0] & 0x7f) < 32))
        {
            codeSet = 'A';
        }

        var codewords = new List<int> { codeSet switch { 'A' => 103, 'B' => 104, _ => 105 } };
        if (gs1)
        {
            codewords.Add(102);
        }

        var position = 0;
        while (position < length)
        {
            if (IsFnc1(position))
            {
                codewords.Add(102);
                position++;
                continue;
            }

            if (codeSet == 'C')
            {
                if (position + 1 < length && IsAsciiDigit(value[position]) && IsAsciiDigit(value[position + 1]))
                {
                    codewords.Add((value[position] - '0') * 10 + value[position + 1] - '0');
                    position += 2;
                    continue;
                }

                var baseValue = value[position] & 0x7f;
                codeSet = baseValue < 32 ? 'A' : 'B';
                codewords.Add(codeSet == 'A' ? 101 : 100);
                continue;
            }

            var run = DigitRun(position);
            if (run >= 4 && (RunEnds(position, run) || run >= 6))
            {
                if (run % 2 != 0)
                {
                    codewords.Add(value[position] - 32);
                    position++;
                }
                codewords.Add(99);
                codeSet = 'C';
                continue;
            }

            var character = value[position];
            var baseCharacter = character >= 128 ? character - 128 : character;
            var fits = codeSet == 'A' ? baseCharacter < 96 : baseCharacter >= 32 && baseCharacter < 128;
            if (!fits)
            {
                codeSet = baseCharacter < 32 ? 'A' : 'B';
                codewords.Add(codeSet == 'A' ? 101 : 100);
            }
            if (character >= 128)
            {
                codewords.Add(codeSet == 'A' ? 101 : 100);
            }

            codewords.Add(codeSet == 'A' && baseCharacter < 32 ? baseCharacter + 64 : baseCharacter - 32);
            position++;
        }

        var checksum = codewords[0];
        for (var index = 1; index < codewords.Count; index++)
        {
            checksum += index * codewords[index];
        }
        codewords.Add(checksum % 103);
        codewords.Add(106);

        var result = new StringBuilder();
        foreach (var codeword in codewords)
        {
            var bar = true;
            foreach (var width in Code128Patterns[codeword])
            {
                result.Append(bar ? '1' : '0', width - '0');
                bar = !bar;
            }
        }
        return result.ToString();
    }

    public static IReadOnlyList<IReadOnlyList<bool>> EncodeQr(string data, string ecc)
    {
        ArgumentNullException.ThrowIfNull(data);
        var level = string.IsNullOrEmpty(ecc) ? "M" : ecc.ToUpperInvariant();
        var levelIndex = level switch
        {
            "L" => 0,
            "M" => 1,
            "Q" => 2,
            "H" => 3,
            _ => throw new ArgumentException($"QR error correction level must be L/M/Q/H: {level}", nameof(ecc))
        };

        var segment = MakeQrSegment(data);
        var version = 0;
        for (var candidate = 1; candidate <= 40; candidate++)
        {
            var countWidth = segment.CountWidths[candidate <= 9 ? 0 : candidate <= 26 ? 1 : 2];
            if (segment.Count >= (1 << countWidth))
            {
                continue;
            }
            if (4 + countWidth + segment.Bits.Count <= QrDataCodewords(candidate, levelIndex) * 8)
            {
                version = candidate;
                break;
            }
        }
        if (version == 0)
        {
            throw new ArgumentException("QR data exceeds version 40 capacity.", nameof(data));
        }

        var countBits = segment.CountWidths[version <= 9 ? 0 : version <= 26 ? 1 : 2];
        var capacity = QrDataCodewords(version, levelIndex) * 8;
        var bits = new List<int>();
        AppendBits(bits, segment.Mode, 4);
        AppendBits(bits, segment.Count, countBits);
        bits.AddRange(segment.Bits);
        bits.AddRange(Enumerable.Repeat(0, Math.Min(4, capacity - bits.Count)));
        while (bits.Count % 8 != 0)
        {
            bits.Add(0);
        }

        var codewords = new List<int>(capacity / 8);
        for (var index = 0; index < bits.Count; index += 8)
        {
            var word = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                word = (word << 1) | bits[index + bit];
            }
            codewords.Add(word);
        }
        var pad = 0xec;
        while (codewords.Count < capacity / 8)
        {
            codewords.Add(pad);
            pad ^= 0xec ^ 0x11;
        }

        var blockCount = QrNumberOfBlocks[levelIndex][version];
        var eccPerBlock = QrEccPerBlock[levelIndex][version];
        var rawCodewords = QrRawModules(version) / 8;
        var shortBlockCount = blockCount - rawCodewords % blockCount;
        var shortBlockLength = rawCodewords / blockCount;
        var blocks = new List<int[]>(blockCount);
        var codewordOffset = 0;
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            var dataLength = shortBlockLength - eccPerBlock + (blockIndex < shortBlockCount ? 0 : 1);
            var blockData = codewords.Skip(codewordOffset).Take(dataLength).ToList();
            codewordOffset += dataLength;
            var errorCorrection = QrField.ReedSolomon(blockData, eccPerBlock, firstRoot: 0);
            if (blockIndex < shortBlockCount)
            {
                blockData.Add(0);
            }
            blockData.AddRange(errorCorrection);
            blocks.Add(blockData.ToArray());
        }

        var finalCodewords = new List<int>(rawCodewords);
        for (var index = 0; index < blocks[0].Length; index++)
        {
            for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
            {
                if (index != shortBlockLength - eccPerBlock || blockIndex >= shortBlockCount)
                {
                    finalCodewords.Add(blocks[blockIndex][index]);
                }
            }
        }

        var size = version * 4 + 17;
        var modules = CreateMatrix(size);
        var functionModules = CreateMatrix(size);
        void SetFunction(int x, int y, bool dark)
        {
            modules[y][x] = dark;
            functionModules[y][x] = true;
        }

        for (var index = 0; index < size; index++)
        {
            SetFunction(6, index, index % 2 == 0);
            SetFunction(index, 6, index % 2 == 0);
        }
        foreach (var (centerX, centerY) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    var x = centerX + dx;
                    var y = centerY + dy;
                    if (x >= 0 && x < size && y >= 0 && y < size)
                    {
                        SetFunction(x, y, Math.Max(Math.Abs(dx), Math.Abs(dy)) is not (2 or 4));
                    }
                }
            }
        }

        var alignmentPositions = QrAlignmentPositions(version);
        for (var i = 0; i < alignmentPositions.Length; i++)
        {
            for (var j = 0; j < alignmentPositions.Length; j++)
            {
                if ((i == 0 && j == 0)
                    || (i == 0 && j == alignmentPositions.Length - 1)
                    || (i == alignmentPositions.Length - 1 && j == 0))
                {
                    continue;
                }
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        SetFunction(
                            alignmentPositions[i] + dx,
                            alignmentPositions[j] + dy,
                            Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }
        }

        void DrawFormat(int mask)
        {
            var formatData = (QrFormatBits[levelIndex] << 3) | mask;
            var remainder = formatData;
            for (var index = 0; index < 10; index++)
            {
                remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
            }
            var format = ((formatData << 10) | remainder) ^ 0x5412;
            var formatModules = Enumerable.Range(0, 15).Select(index => ((format >> index) & 1) != 0).ToArray();
            for (var index = 0; index < 6; index++)
            {
                SetFunction(8, index, formatModules[index]);
            }
            SetFunction(8, 7, formatModules[6]);
            SetFunction(8, 8, formatModules[7]);
            SetFunction(7, 8, formatModules[8]);
            for (var index = 9; index < 15; index++)
            {
                SetFunction(14 - index, 8, formatModules[index]);
            }
            for (var index = 0; index < 8; index++)
            {
                SetFunction(size - 1 - index, 8, formatModules[index]);
            }
            for (var index = 8; index < 15; index++)
            {
                SetFunction(8, size - 15 + index, formatModules[index]);
            }
            SetFunction(8, size - 8, true);
        }

        DrawFormat(0);
        if (version >= 7)
        {
            var remainder = version;
            for (var index = 0; index < 12; index++)
            {
                remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1f25);
            }
            var versionInfo = (version << 12) | remainder;
            for (var index = 0; index < 18; index++)
            {
                var dark = ((versionInfo >> index) & 1) != 0;
                var x = size - 11 + index % 3;
                var y = index / 3;
                SetFunction(x, y, dark);
                SetFunction(y, x, dark);
            }
        }

        var totalBits = finalCodewords.Count * 8;
        var bitIndex = 0;
        var right = size - 1;
        while (right >= 1)
        {
            if (right == 6)
            {
                right = 5;
            }
            for (var vertical = 0; vertical < size; vertical++)
            {
                for (var columnOffset = 0; columnOffset < 2; columnOffset++)
                {
                    var x = right - columnOffset;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vertical : vertical;
                    if (!functionModules[y][x] && bitIndex < totalBits)
                    {
                        modules[y][x] = ((finalCodewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                        bitIndex++;
                    }
                }
            }
            right -= 2;
        }

        var unmaskedModules = modules.Select(row => row.ToArray()).ToArray();
        bool[][]? best = null;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            var masked = CreateMatrix(size);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    masked[y][x] = functionModules[y][x]
                        ? unmaskedModules[y][x]
                        : unmaskedModules[y][x] ^ QrMask(mask, x, y);
                }
            }
            modules = masked;
            DrawFormat(mask);
            masked = modules;
            modules = unmaskedModules;
            var penalty = QrPenalty(masked);
            if (penalty < bestPenalty)
            {
                best = masked;
                bestPenalty = penalty;
            }
        }

        return AsReadOnlyMatrix(best ?? throw new InvalidOperationException("No QR mask was evaluated."));
    }

    public static IReadOnlyList<IReadOnlyList<bool>> EncodeDataMatrix(string data, bool gs1)
    {
        ArgumentNullException.ThrowIfNull(data);
        var codewords = EncodeDataMatrixCodewords(data, gs1);
        var specification = DataMatrixSizes.FirstOrDefault(size => size.DataCodewords >= codewords.Count);
        if (specification == default)
        {
            throw new ArgumentException($"Data Matrix data exceeds the maximum capacity of 1558 codewords (received {codewords.Count}).", nameof(data));
        }

        if (codewords.Count < specification.DataCodewords)
        {
            codewords.Add(129);
            while (codewords.Count < specification.DataCodewords)
            {
                var position = codewords.Count + 1;
                var value = 129 + (149 * position) % 253 + 1;
                codewords.Add(value > 254 ? value - 254 : value);
            }
        }

        var errorCorrectionPerBlock = specification.ErrorCodewords / specification.Blocks;
        var allCodewords = codewords.Concat(Enumerable.Repeat(0, specification.ErrorCodewords)).ToArray();
        for (var block = 0; block < specification.Blocks; block++)
        {
            var blockData = codewords.Where((_, index) => index % specification.Blocks == block).ToList();
            var errorCorrection = DataMatrixField.ReedSolomon(blockData, errorCorrectionPerBlock, firstRoot: 1);
            for (var index = 0; index < errorCorrection.Length; index++)
            {
                allCodewords[specification.DataCodewords + index * specification.Blocks + block] = errorCorrection[index];
            }
        }

        var dataModulesPerEdge = specification.RegionDataSize * specification.RegionsPerEdge;
        var placement = PlaceDataMatrixModules(dataModulesPerEdge, dataModulesPerEdge);
        var placedModules = CreateMatrix(dataModulesPerEdge);
        for (var row = 0; row < dataModulesPerEdge; row++)
        {
            for (var column = 0; column < dataModulesPerEdge; column++)
            {
                var value = placement[row][column];
                placedModules[row][column] = value < 10
                    ? value == 1
                    : ((allCodewords[value / 10 - 1] >> (8 - value % 10)) & 1) != 0;
            }
        }

        var symbol = CreateMatrix(specification.Size);
        var regionSize = specification.RegionDataSize + 2;
        for (var regionRow = 0; regionRow < specification.RegionsPerEdge; regionRow++)
        {
            for (var regionColumn = 0; regionColumn < specification.RegionsPerEdge; regionColumn++)
            {
                var rowStart = regionRow * regionSize;
                var columnStart = regionColumn * regionSize;
                for (var index = 0; index < regionSize; index++)
                {
                    symbol[rowStart + regionSize - 1][columnStart + index] = true;
                    symbol[rowStart + index][columnStart] = true;
                    symbol[rowStart][columnStart + index] = index % 2 == 0;
                    symbol[rowStart + index][columnStart + regionSize - 1] = index % 2 == 1;
                }
                symbol[rowStart + regionSize - 1][columnStart + regionSize - 1] = true;
                for (var row = 0; row < specification.RegionDataSize; row++)
                {
                    for (var column = 0; column < specification.RegionDataSize; column++)
                    {
                        symbol[rowStart + 1 + row][columnStart + 1 + column] =
                            placedModules[regionRow * specification.RegionDataSize + row]
                                [regionColumn * specification.RegionDataSize + column];
                    }
                }
            }
        }

        return AsReadOnlyMatrix(symbol);
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static bool[][] CreateMatrix(int size) =>
        Enumerable.Range(0, size).Select(_ => new bool[size]).ToArray();

    private static IReadOnlyList<IReadOnlyList<bool>> AsReadOnlyMatrix(bool[][] matrix) =>
        matrix.Select(row => (IReadOnlyList<bool>)row).ToArray();

    private static int[] QrBits(int value, int width)
    {
        var result = new int[width];
        for (var index = 0; index < width; index++)
        {
            result[index] = (value >> (width - 1 - index)) & 1;
        }
        return result;
    }

    private static void AppendBits(List<int> destination, int value, int width) =>
        destination.AddRange(QrBits(value, width));

    private static int QrRawModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var alignmentCount = version / 7 + 2;
            result -= (25 * alignmentCount - 10) * alignmentCount - 55;
            if (version >= 7)
            {
                result -= 36;
            }
        }
        return result;
    }

    private static int QrDataCodewords(int version, int level) =>
        QrRawModules(version) / 8 - QrEccPerBlock[level][version] * QrNumberOfBlocks[level][version];

    private static int[] QrAlignmentPositions(int version)
    {
        if (version == 1)
        {
            return [];
        }

        var count = version / 7 + 2;
        var size = version * 4 + 17;
        var step = ((version * 8 + count * 3 + 5) / (count * 4 - 4)) * 2;
        return Enumerable.Range(0, count - 1)
            .Select(index => size - 7 - index * step)
            .Append(6)
            .Reverse()
            .ToArray();
    }

    private static QrSegment MakeQrSegment(string data)
    {
        if (data.Length > 0 && data.All(IsAsciiDigit))
        {
            var bits = new List<int>();
            for (var index = 0; index < data.Length; index += 3)
            {
                var group = data.Substring(index, Math.Min(3, data.Length - index));
                AppendBits(bits, int.Parse(group, System.Globalization.CultureInfo.InvariantCulture), group.Length * 3 + 1);
            }
            return new QrSegment(0x1, data.Length, bits, [10, 12, 14]);
        }

        if (data.Length > 0 && data.All(character => QrAlphanumeric.Contains(character, StringComparison.Ordinal)))
        {
            var bits = new List<int>();
            for (var index = 0; index + 1 < data.Length; index += 2)
            {
                AppendBits(bits, QrAlphanumeric.IndexOf(data[index]) * 45 + QrAlphanumeric.IndexOf(data[index + 1]), 11);
            }
            if (data.Length % 2 != 0)
            {
                AppendBits(bits, QrAlphanumeric.IndexOf(data[^1]), 6);
            }
            return new QrSegment(0x2, data.Length, bits, [9, 11, 13]);
        }

        var bytes = StrictUtf8.GetBytes(data);
        var byteBits = new List<int>(bytes.Length * 8);
        foreach (var value in bytes)
        {
            AppendBits(byteBits, value, 8);
        }
        return new QrSegment(0x4, bytes.Length, byteBits, [8, 16, 16]);
    }

    private static bool QrMask(int mask, int x, int y) =>
        mask switch
        {
            0 => (x + y) % 2 == 0,
            1 => y % 2 == 0,
            2 => x % 3 == 0,
            3 => (x + y) % 3 == 0,
            4 => (x / 3 + y / 2) % 2 == 0,
            5 => (x * y % 2) + (x * y % 3) == 0,
            6 => ((x * y % 2) + (x * y % 3)) % 2 == 0,
            7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
            _ => throw new ArgumentOutOfRangeException(nameof(mask))
        };

    private static int QrPenalty(bool[][] matrix)
    {
        var size = matrix.Length;
        var penalty = 0;
        var columns = Enumerable.Range(0, size)
            .Select(x => Enumerable.Range(0, size).Select(y => matrix[y][x]).ToArray())
            .ToArray();

        foreach (var line in matrix.Concat(columns))
        {
            var run = 0;
            bool? previous = null;
            foreach (var value in line)
            {
                if (value == previous)
                {
                    run++;
                }
                else
                {
                    if (run >= 5)
                    {
                        penalty += 3 + run - 5;
                    }
                    run = 1;
                    previous = value;
                }
            }
            if (run >= 5)
            {
                penalty += 3 + run - 5;
            }

            var pattern = "0000" + new string(line.Select(value => value ? '1' : '0').ToArray()) + "0000";
            foreach (var finderPattern in new[] { "10111010000", "00001011101" })
            {
                var start = 0;
                while ((start = pattern.IndexOf(finderPattern, start, StringComparison.Ordinal)) >= 0)
                {
                    penalty += 40;
                    start++;
                }
            }
        }

        for (var y = 0; y < size - 1; y++)
        {
            for (var x = 0; x < size - 1; x++)
            {
                var value = matrix[y][x];
                if (value == matrix[y][x + 1] && value == matrix[y + 1][x] && value == matrix[y + 1][x + 1])
                {
                    penalty += 3;
                }
            }
        }

        var dark = matrix.Sum(row => row.Count(value => value));
        var total = size * size;
        var balance = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return penalty + balance * 10;
    }

    private static List<int> EncodeDataMatrixCodewords(string data, bool gs1)
    {
        var codewords = new List<int>();
        var value = data;
        if (gs1)
        {
            codewords.Add(232);
            if (value.StartsWith('\x1d'))
            {
                value = value[1..];
            }
        }

        var parts = gs1 ? value.Split('\x1d') : [value];
        for (var partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            if (partIndex > 0)
            {
                codewords.Add(232);
            }
            var bytes = StrictUtf8.GetBytes(parts[partIndex]);
            for (var index = 0; index < bytes.Length;)
            {
                var current = bytes[index];
                if (current is >= (byte)'0' and <= (byte)'9'
                    && index + 1 < bytes.Length
                    && bytes[index + 1] is >= (byte)'0' and <= (byte)'9')
                {
                    codewords.Add(130 + (current - '0') * 10 + bytes[index + 1] - '0');
                    index += 2;
                    continue;
                }
                if (current >= 128)
                {
                    codewords.Add(235);
                    codewords.Add(current - 127);
                }
                else
                {
                    codewords.Add(current + 1);
                }
                index++;
            }
        }
        return codewords;
    }

    private static int[][] PlaceDataMatrixModules(int rowCount, int columnCount)
    {
        var modules = Enumerable.Range(0, rowCount)
            .Select(_ => Enumerable.Repeat(-1, columnCount).ToArray())
            .ToArray();

        void SetModule(int row, int column, int codeword, int bit)
        {
            if (row < 0)
            {
                row += rowCount;
                column += 4 - ((rowCount + 4) % 8);
            }
            if (column < 0)
            {
                column += columnCount;
                row += 4 - ((columnCount + 4) % 8);
            }
            modules[row][column] = codeword * 10 + bit;
        }

        void Utah(int row, int column, int codeword)
        {
            SetModule(row - 2, column - 2, codeword, 1);
            SetModule(row - 2, column - 1, codeword, 2);
            SetModule(row - 1, column - 2, codeword, 3);
            SetModule(row - 1, column - 1, codeword, 4);
            SetModule(row - 1, column, codeword, 5);
            SetModule(row, column - 2, codeword, 6);
            SetModule(row, column - 1, codeword, 7);
            SetModule(row, column, codeword, 8);
        }

        void Corner(int codeword, (int Row, int Column)[] points)
        {
            for (var index = 0; index < points.Length; index++)
            {
                SetModule(points[index].Row, points[index].Column, codeword, index + 1);
            }
        }

        var codeword = 1;
        var row = 4;
        var column = 0;
        while (true)
        {
            if (row == rowCount && column == 0)
            {
                Corner(codeword++,
                [
                    (rowCount - 1, 0), (rowCount - 1, 1), (rowCount - 1, 2), (0, columnCount - 2),
                    (0, columnCount - 1), (1, columnCount - 1), (2, columnCount - 1), (3, columnCount - 1)
                ]);
            }
            if (row == rowCount - 2 && column == 0 && columnCount % 4 != 0)
            {
                Corner(codeword++,
                [
                    (rowCount - 3, 0), (rowCount - 2, 0), (rowCount - 1, 0), (0, columnCount - 4),
                    (0, columnCount - 3), (0, columnCount - 2), (0, columnCount - 1), (1, columnCount - 1)
                ]);
            }
            if (row == rowCount - 2 && column == 0 && columnCount % 8 == 4)
            {
                Corner(codeword++,
                [
                    (rowCount - 3, 0), (rowCount - 2, 0), (rowCount - 1, 0), (0, columnCount - 2),
                    (0, columnCount - 1), (1, columnCount - 1), (2, columnCount - 1), (3, columnCount - 1)
                ]);
            }
            if (row == rowCount + 4 && column == 2 && columnCount % 8 == 0)
            {
                Corner(codeword++,
                [
                    (rowCount - 1, 0), (rowCount - 1, columnCount - 1), (0, columnCount - 3), (0, columnCount - 2),
                    (0, columnCount - 1), (1, columnCount - 3), (1, columnCount - 2), (1, columnCount - 1)
                ]);
            }

            do
            {
                if (row < rowCount && column >= 0 && modules[row][column] == -1)
                {
                    Utah(row, column, codeword++);
                }
                row -= 2;
                column += 2;
            }
            while (row >= 0 && column < columnCount);

            row++;
            column += 3;
            do
            {
                if (row >= 0 && column < columnCount && modules[row][column] == -1)
                {
                    Utah(row, column, codeword++);
                }
                row += 2;
                column -= 2;
            }
            while (row < rowCount && column >= 0);

            row += 3;
            column++;
            if (!(row < rowCount || column < columnCount))
            {
                break;
            }
        }

        if (modules[rowCount - 1][columnCount - 1] == -1)
        {
            modules[rowCount - 1][columnCount - 1] = 1;
            modules[rowCount - 2][columnCount - 2] = 1;
            modules[rowCount - 1][columnCount - 2] = 0;
            modules[rowCount - 2][columnCount - 1] = 0;
        }
        return modules;
    }

    private sealed record QrSegment(int Mode, int Count, List<int> Bits, int[] CountWidths);

    private sealed class GaloisField
    {
        private readonly int[] _exponents = new int[512];
        private readonly int[] _logarithms = new int[256];

        public GaloisField(int primitive)
        {
            var value = 1;
            for (var index = 0; index < 255; index++)
            {
                _exponents[index] = value;
                _logarithms[value] = index;
                value <<= 1;
                if ((value & 0x100) != 0)
                {
                    value ^= primitive;
                }
            }
            for (var index = 255; index < _exponents.Length; index++)
            {
                _exponents[index] = _exponents[index - 255];
            }
        }

        private int Multiply(int left, int right) =>
            left == 0 || right == 0 ? 0 : _exponents[_logarithms[left] + _logarithms[right]];

        private int[] Generator(int count, int firstRoot)
        {
            var generator = new List<int> { 1 };
            for (var index = 0; index < count; index++)
            {
                var root = _exponents[(firstRoot + index) % 255];
                var next = generator.Concat([0]).ToArray();
                for (var coefficient = 0; coefficient < generator.Count; coefficient++)
                {
                    next[coefficient + 1] ^= Multiply(generator[coefficient], root);
                }
                generator = next.ToList();
            }
            return generator.ToArray();
        }

        public int[] ReedSolomon(IReadOnlyList<int> data, int count, int firstRoot)
        {
            var generator = Generator(count, firstRoot);
            var message = data.Concat(Enumerable.Repeat(0, count)).ToArray();
            for (var index = 0; index < data.Count; index++)
            {
                var coefficient = message[index];
                if (coefficient == 0)
                {
                    continue;
                }
                for (var offset = 1; offset <= count; offset++)
                {
                    message[index + offset] ^= Multiply(generator[offset], coefficient);
                }
            }
            return message.Skip(data.Count).ToArray();
        }
    }
}
