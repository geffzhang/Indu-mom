using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StbImageNext;

namespace HoneyMOM.Infrastructure.Labeling;

public sealed record PrinterSettings(string Kind, int Dpi, int? Darkness = null, string? CjkFont = null);

public sealed record MarkerSettings(string? CommandTemplate = null, string? Symbology = null);

public sealed class LabelTemplateRenderer
{
    private const string SvgFont = "Microsoft YaHei, SimHei, sans-serif";
    private const string DefaultMarkerCommand = "MARK {job} {data}\\r\\n";
    private const string Gs1Separator = "\u001d";
    private static readonly Regex Placeholder = new(@"\{\{\s*([^{}|]*?)\s*(?:\|([^{}]*))?\}\}", RegexOptions.Compiled);
    private static readonly Regex MarkerField = new(@"\{([\w.]+)\}", RegexOptions.Compiled);
    private static readonly HashSet<string> Gs1FixedApplicationIdentifiers =
    [
        "00", "01", "02", "03", "04", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20",
        "31", "32", "33", "34", "35", "36", "41"
    ];

    private static readonly Encoding GbkEncoding = CreateGbkEncoding();

    public byte[] RenderPrinterPayload(
        JsonElement template,
        IReadOnlyDictionary<string, object?> data,
        PrinterSettings printer,
        int copies)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(printer);
        var dpi = printer.Dpi > 0 ? printer.Dpi : 203;
        return string.Equals(printer.Kind, "tspl", StringComparison.OrdinalIgnoreCase)
            ? RenderTspl(template, data, printer, dpi, copies)
            : RenderZpl(template, data, printer, dpi, copies);
    }

    public string RenderMarkerText(JsonElement template, IReadOnlyDictionary<string, object?> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var element in Elements(template))
        {
            if (GetString(element, "type") == "barcode")
            {
                return Fill(GetValue(element, "data"), data);
            }
        }

        var serial = Lookup(data, "sn");
        var value = IsTruthy(serial) ? serial : Lookup(data, "code");
        return IsTruthy(value) ? PythonString(value) : string.Empty;
    }

    public string RenderMarkerCommand(
        MarkerSettings marker,
        string jobCode,
        string text,
        IReadOnlyDictionary<string, object?> data)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(data);
        var template = string.IsNullOrEmpty(marker.CommandTemplate)
            ? DefaultMarkerCommand
            : marker.CommandTemplate;
        template = template.Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);
        var flattened = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["job"] = jobCode,
            ["data"] = text,
            ["sn"] = data.TryGetValue("sn", out var serial) ? serial : text,
            ["symbology"] = string.IsNullOrEmpty(marker.Symbology) ? "datamatrix" : marker.Symbology
        };
        foreach (var (key, value) in data)
        {
            flattened[$"fields.{key}"] = value;
        }

        return MarkerField.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return flattened.TryGetValue(key, out var value) ? PythonString(value) : string.Empty;
        });
    }

    private static byte[] RenderZpl(
        JsonElement template,
        IReadOnlyDictionary<string, object?> data,
        PrinterSettings printer,
        int dpi,
        int copies)
    {
        var output = new List<string> { "^XA", "^CI28" };
        if (printer.Darkness is not null)
        {
            output.Add($"~SD{printer.Darkness.Value:00}");
        }
        output.Add($"^PW{Dots(RequiredNumber(template, "width_mm"), dpi)}");
        output.Add($"^LL{Dots(RequiredNumber(template, "height_mm"), dpi)}");
        output.Add("^LH0,0");

        foreach (var element in Elements(template))
        {
            var type = GetString(element, "type");
            var x = Number(element, "x");
            var y = Number(element, "y");
            if (type == "text")
            {
                var text = Fill(GetValue(element, "text"), data);
                if (text.Length == 0)
                {
                    continue;
                }

                var height = Number(element, "h", 4);
                var heightDots = Math.Max(10, Dots(height, dpi));
                var rotation = Rotation(element);
                var rotate = ZplRotation(rotation);
                var font = text.All(character => character <= 0x7f)
                    ? $"^A0{rotate},{heightDots},{heightDots}"
                    : $"^A@{rotate},{heightDots},{heightDots},{(string.IsNullOrEmpty(printer.CjkFont) ? "E:SIMSUN.TTF" : printer.CjkFont)}";
                var fieldBlock = string.Empty;
                if (Truthy(element, "w"))
                {
                    var alignment = GetString(element, "align");
                    var justification = alignment switch { "center" => "C", "right" => "R", _ => "L" };
                    fieldBlock = $"^FB{Dots(Number(element, "w"), dpi)},1,0,{justification},0";
                }

                foreach (var (offsetX, offsetY) in BoldPasses(element, rotation))
                {
                    output.Add($"^FO{Dots(x, dpi) + offsetX},{Dots(y, dpi) + offsetY}{font}{fieldBlock}{ZplFieldData(text)}^FS");
                }
            }
            else if (type == "barcode")
            {
                var code = BuildCode(element, data);
                if (code is null)
                {
                    continue;
                }

                var width = Number(element, "w");
                var height = Number(element, "h");
                if (code.Kind == "1d")
                {
                    var moduleWidth = Math.Max(1, Math.Min(10, Dots(width, dpi) / ((string)code.Modules).Length));
                    var humanReadable = Truthy(element, "hr") ? "Y" : "N";
                    if (code.Gs1)
                    {
                        var invocation = Code128Invocations(
                            BarcodeEncoder.EncodeCode128Values(code.Data, gs1: true),
                            zpl: true);
                        output.Add($"^FO{Dots(x, dpi)},{Dots(y, dpi)}^BY{moduleWidth},2^BCN,{Dots(BarsHeight(element), dpi)},{humanReadable},N,N^FD{invocation}^FS");
                    }
                    else
                    {
                        output.Add($"^FO{Dots(x, dpi)},{Dots(y, dpi)}^BY{moduleWidth},2^BCN,{Dots(BarsHeight(element), dpi)},{humanReadable},N,N,A{ZplFieldData(code.Data)}^FS");
                    }
                }
                else if (code.Symbology == "qr")
                {
                    var modules = (IReadOnlyList<IReadOnlyList<bool>>)code.Modules;
                    var magnification = Math.Max(1, Math.Min(10, Dots(Math.Min(width, height), dpi) / modules.Count));
                    output.Add($"^FO{Dots(x, dpi)},{Dots(y, dpi)}^BQN,2,{magnification}{ZplFieldData(code.Data, code.Ecc + "A,")}^FS");
                }
                else
                {
                    var modules = (IReadOnlyList<IReadOnlyList<bool>>)code.Modules;
                    var moduleWidth = Math.Max(1, Dots(Math.Min(width, height), dpi) / modules.Count);
                    if (code.Gs1)
                    {
                        output.Add($"^FO{Dots(x, dpi)},{Dots(y, dpi)}^BXN,{moduleWidth},200,0,0,6,_^FD_1{code.Data.Replace(Gs1Separator, "_1", StringComparison.Ordinal)}^FS");
                    }
                    else
                    {
                        output.Add($"^FO{Dots(x, dpi)},{Dots(y, dpi)}^BXN,{moduleWidth},200{ZplFieldData(code.Data)}^FS");
                    }
                }
            }
            else if (type == "line")
            {
                var x2 = Number(element, "x2", x);
                var y2 = Number(element, "y2", y);
                var thickness = Math.Max(1, Dots(Number(element, "t", 0.3), dpi));
                if (Math.Abs(y2 - y) < 1e-9)
                {
                    output.Add($"^FO{Dots(Math.Min(x, x2), dpi)},{Math.Max(0, Dots(y, dpi) - thickness / 2)}^GB{Math.Max(thickness, Dots(Math.Abs(x2 - x), dpi))},{thickness},{thickness}^FS");
                }
                else if (Math.Abs(x2 - x) < 1e-9)
                {
                    output.Add($"^FO{Math.Max(0, Dots(x, dpi) - thickness / 2)},{Dots(Math.Min(y, y2), dpi)}^GB{thickness},{Math.Max(thickness, Dots(Math.Abs(y2 - y), dpi))},{thickness}^FS");
                }
                else
                {
                    var lean = (x2 - x) * (y2 - y) > 0 ? "L" : "R";
                    output.Add($"^FO{Dots(Math.Min(x, x2), dpi)},{Dots(Math.Min(y, y2), dpi)}^GD{Dots(Math.Abs(x2 - x), dpi)},{Dots(Math.Abs(y2 - y), dpi)},{thickness},B,{lean}^FS");
                }
            }
            else if (type == "box")
            {
                var width = Dots(Number(element, "w"), dpi);
                var height = Dots(Number(element, "h"), dpi);
                var thickness = Truthy(element, "fill")
                    ? Math.Min(width, height)
                    : Math.Max(1, Dots(Number(element, "t", 0.3), dpi));
                output.Add($"^FO{Dots(x, dpi)},{Dots(y, dpi)}^GB{Math.Max(width, thickness)},{Math.Max(height, thickness)},{thickness}^FS");
            }
            else if (type == "image")
            {
                var raw = ImageBytes(GetString(element, "src"));
                var rows = raw is null
                    ? null
                    : MonochromeRows(raw, Dots(Number(element, "w"), dpi), Dots(Number(element, "h"), dpi));
                if (rows is null)
                {
                    output.Add("^FX image skipped (data URI required)^FS");
                    continue;
                }

                var (bytesPerRow, packed) = Pack(rows, blackBit: 1);
                output.Add($"^FO{Dots(x, dpi)},{Dots(y, dpi)}^GFA,{packed.Length},{packed.Length},{bytesPerRow},{Convert.ToHexString(packed)}^FS");
            }
        }

        output.Add($"^PQ{Math.Max(1, copies)}");
        output.Add("^XZ");
        return Encoding.UTF8.GetBytes(string.Join('\n', output) + "\n");
    }

    private static byte[] RenderTspl(
        JsonElement template,
        IReadOnlyDictionary<string, object?> data,
        PrinterSettings printer,
        int dpi,
        int copies)
    {
        var output = new List<byte>();
        void Put(string line)
        {
            output.AddRange(GbkEncoding.GetBytes(line));
            output.Add(0x0d);
            output.Add(0x0a);
        }

        Put($"SIZE {MmFormat(RequiredNumber(template, "width_mm"))} mm,{MmFormat(RequiredNumber(template, "height_mm"))} mm");
        Put("GAP 2 mm,0 mm");
        Put("DIRECTION 1");
        Put("CLS");

        foreach (var element in Elements(template))
        {
            var type = GetString(element, "type");
            var x = Number(element, "x");
            var y = Number(element, "y");
            if (type == "text")
            {
                var text = Fill(GetValue(element, "text"), data);
                if (text.Length == 0)
                {
                    continue;
                }

                var heightDots = Dots(Number(element, "h", 4), dpi);
                var rotation = Rotation(element);
                var isAscii = text.All(character => character <= 0x7f);
                var multiplier = Math.Max(1, (int)Math.Round(heightDots / 24.0, MidpointRounding.ToEven));
                var font = isAscii
                    ? "3"
                    : (string.IsNullOrEmpty(printer.CjkFont) ? "TSS24.BF2" : printer.CjkFont);
                var textWidth = isAscii
                    ? text.Length * 16 * multiplier
                    : text.EnumerateRunes().Sum(character => CharacterWidth(character) == 1.0 ? 24 : 12) * multiplier;
                var textHeight = 24 * multiplier;
                var boxWidth = Truthy(element, "w") ? Dots(Number(element, "w"), dpi) : textWidth;
                var align = GetString(element, "align");
                var alignOffset = Truthy(element, "w")
                    ? (int)AlignOffset(align, boxWidth, textWidth)
                    : 0;
                var (offsetX, offsetY) = RotationOffset(rotation, boxWidth, textHeight);
                var (alignX, alignY) = rotation switch
                {
                    0 => (alignOffset, 0),
                    90 => (0, alignOffset),
                    180 => (-alignOffset, 0),
                    270 => (0, -alignOffset),
                    _ => (0, 0)
                };
                var printX = Dots(x, dpi) + (int)offsetX + alignX;
                var printY = Dots(y, dpi) + (int)offsetY + alignY;
                foreach (var (boldX, boldY) in BoldPasses(element, rotation))
                {
                    Put($"TEXT {printX + boldX},{printY + boldY},{QuoteTspl(font)},{rotation},{multiplier},{multiplier},{QuoteTspl(text)}");
                }
            }
            else if (type == "barcode")
            {
                var code = BuildCode(element, data);
                if (code is null)
                {
                    continue;
                }

                var width = Number(element, "w");
                var height = Number(element, "h");
                if (code.Kind == "1d")
                {
                    var moduleWidth = Math.Max(1, Math.Min(10, Dots(width, dpi) / ((string)code.Modules).Length));
                    var humanReadable = Truthy(element, "hr") ? 2 : 0;
                    if (code.Gs1)
                    {
                        var invocation = Code128Invocations(
                            BarcodeEncoder.EncodeCode128Values(code.Data, gs1: true),
                            zpl: false);
                        Put($"BARCODE {Dots(x, dpi)},{Dots(y, dpi)},\"128M\",{Dots(BarsHeight(element), dpi)},{humanReadable},0,{moduleWidth},{moduleWidth},{QuoteTspl(invocation)}");
                    }
                    else
                    {
                        Put($"BARCODE {Dots(x, dpi)},{Dots(y, dpi)},\"128\",{Dots(BarsHeight(element), dpi)},{humanReadable},0,{moduleWidth},{moduleWidth},{QuoteTspl(code.Data)}");
                    }
                }
                else if (code.Symbology == "qr")
                {
                    var modules = (IReadOnlyList<IReadOnlyList<bool>>)code.Modules;
                    var cell = Math.Max(1, Math.Min(10, Dots(Math.Min(width, height), dpi) / modules.Count));
                    Put($"QRCODE {Dots(x, dpi)},{Dots(y, dpi)},{code.Ecc},{cell},A,0,{QuoteTspl(code.Data)}");
                }
                else
                {
                    var size = Dots(Math.Min(width, height), dpi);
                    var modules = (IReadOnlyList<IReadOnlyList<bool>>)code.Modules;
                    var moduleWidth = Math.Max(1, size / modules.Count);
                    if (code.Gs1)
                    {
                        var gs1Data = "~1" + code.Data.Replace(Gs1Separator, "~1", StringComparison.Ordinal);
                        Put($"DMATRIX {Dots(x, dpi)},{Dots(y, dpi)},{size},{size},c126,x{moduleWidth},{QuoteTspl(gs1Data)}");
                    }
                    else
                    {
                        Put($"DMATRIX {Dots(x, dpi)},{Dots(y, dpi)},{size},{size},x{moduleWidth},{QuoteTspl(code.Data)}");
                    }
                }
            }
            else if (type == "line")
            {
                var x2 = Number(element, "x2", x);
                var y2 = Number(element, "y2", y);
                var thickness = Math.Max(1, Dots(Number(element, "t", 0.3), dpi));
                if (Math.Abs(y2 - y) < 1e-9)
                {
                    Put($"BAR {Dots(Math.Min(x, x2), dpi)},{Math.Max(0, Dots(y, dpi) - thickness / 2)},{Math.Max(thickness, Dots(Math.Abs(x2 - x), dpi))},{thickness}");
                }
                else if (Math.Abs(x2 - x) < 1e-9)
                {
                    Put($"BAR {Math.Max(0, Dots(x, dpi) - thickness / 2)},{Dots(Math.Min(y, y2), dpi)},{thickness},{Math.Max(thickness, Dots(Math.Abs(y2 - y), dpi))}");
                }
                else
                {
                    Put($"DIAGONAL {Dots(x, dpi)},{Dots(y, dpi)},{Dots(x2, dpi)},{Dots(y2, dpi)},{thickness}");
                }
            }
            else if (type == "box")
            {
                var width = Dots(Number(element, "w"), dpi);
                var height = Dots(Number(element, "h"), dpi);
                if (Truthy(element, "fill"))
                {
                    Put($"BAR {Dots(x, dpi)},{Dots(y, dpi)},{width},{height}");
                }
                else
                {
                    Put($"BOX {Dots(x, dpi)},{Dots(y, dpi)},{Dots(x, dpi) + width},{Dots(y, dpi) + height},{Math.Max(1, Dots(Number(element, "t", 0.3), dpi))}");
                }
            }
            else if (type == "image")
            {
                var raw = ImageBytes(GetString(element, "src"));
                var rows = raw is null
                    ? null
                    : MonochromeRows(raw, Dots(Number(element, "w"), dpi), Dots(Number(element, "h"), dpi));
                if (rows is null)
                {
                    continue;
                }

                var (bytesPerRow, packed) = Pack(rows, blackBit: 0);
                output.AddRange(Encoding.ASCII.GetBytes(
                    $"BITMAP {Dots(x, dpi)},{Dots(y, dpi)},{bytesPerRow},{rows.Length},0,"));
                output.AddRange(packed);
                output.Add(0x0d);
                output.Add(0x0a);
            }
        }

        Put($"PRINT {Math.Max(1, copies)},1");
        return output.ToArray();
    }

    private static RenderedCode? BuildCode(JsonElement element, IReadOnlyDictionary<string, object?> data)
    {
        var text = Fill(GetValue(element, "data"), data);
        if (text.Length == 0)
        {
            return null;
        }

        var symbology = NormalizeSymbology(GetString(element, "symbology"));
        var gs1 = Truthy(element, "gs1") && symbology != "qr";
        var humanText = text;
        if (gs1)
        {
            (text, humanText) = ParseGs1Data(text);
        }
        var ecc = (string.IsNullOrEmpty(GetString(element, "ecc")) ? "M" : GetString(element, "ecc")!).ToUpperInvariant();

        if (symbology == "code128")
        {
            return new RenderedCode(symbology, "1d", BarcodeEncoder.EncodeCode128(text, gs1), text, humanText, gs1, ecc);
        }
        if (symbology == "qr")
        {
            return new RenderedCode(symbology, "2d", BarcodeEncoder.EncodeQr(text, ecc), text, humanText, gs1, ecc);
        }
        return new RenderedCode(symbology, "2d", BarcodeEncoder.EncodeDataMatrix(text, gs1), text, humanText, gs1, ecc);
    }

    private static string NormalizeSymbology(string? value)
    {
        var normalized = (string.IsNullOrWhiteSpace(value) ? "code128" : value.Trim())
            .ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized switch
        {
            "code128" or "128" or "c128" => "code128",
            "qr" or "qrcode" => "qr",
            "datamatrix" or "dm" or "dmatrix" or "data_matrix" => "datamatrix",
            _ => throw new ArgumentException($"Unsupported barcode symbology: {value}", nameof(value))
        };
    }

    private static (string Data, string HumanText) ParseGs1Data(string value)
    {
        if (!value.StartsWith('('))
        {
            return (value, value.Replace(Gs1Separator, string.Empty, StringComparison.Ordinal));
        }

        var matches = Regex.Matches(value, @"\((\d{2,4})\)([^(]*)");
        if (matches.Count == 0)
        {
            return (value, value.Replace(Gs1Separator, string.Empty, StringComparison.Ordinal));
        }

        var output = new StringBuilder();
        for (var index = 0; index < matches.Count; index++)
        {
            var applicationIdentifier = matches[index].Groups[1].Value;
            output.Append(applicationIdentifier);
            output.Append(matches[index].Groups[2].Value);
            if (index < matches.Count - 1 && !Gs1FixedApplicationIdentifiers.Contains(applicationIdentifier[..2]))
            {
                output.Append(Gs1Separator);
            }
        }
        return (output.ToString(), value);
    }

    private static double BarsHeight(JsonElement element)
    {
        var height = Number(element, "h", 10);
        return Truthy(element, "hr") ? height - HumanReadableHeight(height) - 0.4 : height;
    }

    private static double HumanReadableHeight(double height) => Math.Max(2.0, Math.Min(3.5, height * 0.22));

    private static byte[]? ImageBytes(string? source)
    {
        if (source is null || !source.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        var comma = source.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }
        var header = source[..comma];
        var body = source[(comma + 1)..];
        try
        {
            return header.Contains(";base64", StringComparison.Ordinal)
                ? Convert.FromBase64String(body)
                : Encoding.Latin1.GetBytes(body);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool[][] MonochromeRows(byte[] bytes, int width, int height)
    {
        var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        var source = new byte[image.Width * image.Height];
        for (var index = 0; index < source.Length; index++)
        {
            var pixel = index * 4;
            var red = CompositeOnWhite(image.Data[pixel], image.Data[pixel + 3]);
            var green = CompositeOnWhite(image.Data[pixel + 1], image.Data[pixel + 3]);
            var blue = CompositeOnWhite(image.Data[pixel + 2], image.Data[pixel + 3]);
            source[index] = (byte)((299 * red + 587 * green + 114 * blue + 500) / 1000);
        }

        var targetWidth = Math.Max(1, width);
        var targetHeight = Math.Max(1, height);
        var resized = ResizeGrayBicubic(source, image.Width, image.Height, targetWidth, targetHeight);

        var rows = new bool[targetHeight][];
        for (var y = 0; y < targetHeight; y++)
        {
            rows[y] = new bool[targetWidth];
            for (var x = 0; x < targetWidth; x++)
            {
                rows[y][x] = resized[y * targetWidth + x] < 128;
            }
        }
        return rows;
    }

    private static byte[] ResizeGrayBicubic(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var horizontalWeights = CreateBicubicWeights(sourceWidth, targetWidth);
        var horizontal = new byte[sourceHeight * targetWidth];
        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < targetWidth; x++)
            {
                var weights = horizontalWeights[x];
                var value = 0d;
                for (var offset = 0; offset < weights.Values.Length; offset++)
                {
                    value += source[y * sourceWidth + weights.Start + offset] * weights.Values[offset];
                }
                horizontal[y * targetWidth + x] = RoundToByte(value);
            }
        }

        var verticalWeights = CreateBicubicWeights(sourceHeight, targetHeight);
        var resized = new byte[targetHeight * targetWidth];
        for (var y = 0; y < targetHeight; y++)
        {
            var weights = verticalWeights[y];
            for (var x = 0; x < targetWidth; x++)
            {
                var value = 0d;
                for (var offset = 0; offset < weights.Values.Length; offset++)
                {
                    value += horizontal[(weights.Start + offset) * targetWidth + x] * weights.Values[offset];
                }
                resized[y * targetWidth + x] = RoundToByte(value);
            }
        }
        return resized;
    }

    private static AxisWeights[] CreateBicubicWeights(int sourceSize, int targetSize)
    {
        var scale = (double)sourceSize / targetSize;
        var filterScale = Math.Max(1, scale);
        var support = 2 * filterScale;
        var result = new AxisWeights[targetSize];
        for (var target = 0; target < targetSize; target++)
        {
            var center = (target + 0.5) * scale;
            var start = Math.Max(0, (int)Math.Floor(center - support + 0.5));
            var end = Math.Min(sourceSize, (int)Math.Floor(center + support + 0.5));
            var weights = new double[end - start];
            var total = 0d;
            for (var offset = 0; offset < weights.Length; offset++)
            {
                var distance = (start + offset - center + 0.5) / filterScale;
                weights[offset] = BicubicWeight(distance);
                total += weights[offset];
            }
            for (var offset = 0; offset < weights.Length; offset++)
            {
                weights[offset] /= total;
            }
            result[target] = new AxisWeights(start, weights);
        }
        return result;
    }

    private static double BicubicWeight(double value)
    {
        var distance = Math.Abs(value);
        if (distance < 1)
        {
            return ((1.5 * distance - 2.5) * distance) * distance + 1;
        }
        if (distance < 2)
        {
            return ((-0.5 * distance + 2.5) * distance - 4) * distance + 2;
        }
        return 0;
    }

    private static byte RoundToByte(double value) =>
        (byte)Math.Clamp((int)Math.Floor(value + 0.5), 0, 255);

    private static int CompositeOnWhite(byte channel, byte alpha) =>
        (channel * alpha + 255 * (255 - alpha) + 127) / 255;

    private static (int BytesPerRow, byte[] Packed) Pack(bool[][] rows, int blackBit)
    {
        var bytesPerRow = (rows[0].Length + 7) / 8;
        var packed = new byte[bytesPerRow * rows.Length];
        for (var y = 0; y < rows.Length; y++)
        {
            for (var byteIndex = 0; byteIndex < bytesPerRow; byteIndex++)
            {
                var value = 0;
                for (var bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    var x = byteIndex * 8 + bitIndex;
                    var dark = x < rows[y].Length && rows[y][x];
                    var bit = blackBit == 1 ? (dark ? 1 : 0) : (dark ? 0 : 1);
                    value = (value << 1) | bit;
                }
                packed[y * bytesPerRow + byteIndex] = (byte)value;
            }
        }
        return (bytesPerRow, packed);
    }

    private static IEnumerable<(int X, int Y)> BoldPasses(JsonElement element, int rotation)
    {
        if (!Truthy(element, "bold"))
        {
            return [(0, 0)];
        }
        return rotation is 0 or 180 ? [(0, 0), (1, 0)] : [(0, 0), (0, 1)];
    }

    private static string ZplRotation(int rotation) =>
        rotation switch { 0 => "N", 90 => "R", 180 => "I", 270 => "B", _ => "N" };

    private static int Rotation(JsonElement element)
    {
        var rotation = (int)Number(element, "rotate") % 360;
        rotation = (rotation + 360) % 360;
        return rotation is 0 or 90 or 180 or 270 ? rotation : 0;
    }

    private static (double X, double Y) RotationOffset(int rotation, double length, double height) =>
        rotation switch
        {
            90 => (height, 0),
            180 => (length, height),
            270 => (0, length),
            _ => (0, 0)
        };

    private static double AlignOffset(string? alignment, double boxWidth, double textWidth) =>
        alignment switch
        {
            "center" => (boxWidth - textWidth) / 2,
            "right" => boxWidth - textWidth,
            _ => 0
        };

    private static double CharacterWidth(System.Text.Rune value)
    {
        var codePoint = value.Value;
        return IsEastAsianWide(codePoint) ? 1.0 : 0.55;
    }

    private static bool IsEastAsianWide(int codePoint) =>
        codePoint is >= 0x1100 and <= 0x115f
        or 0x2329 or 0x232a
        or >= 0x2e80 and <= 0xa4cf
        or >= 0xac00 and <= 0xd7a3
        or >= 0xf900 and <= 0xfaff
        or >= 0xfe10 and <= 0xfe19
        or >= 0xfe30 and <= 0xfe6f
        or >= 0xff01 and <= 0xff60
        or >= 0xffe0 and <= 0xffe6
        or >= 0x1f300 and <= 0x1faff
        or >= 0x20000 and <= 0x3fffd;

    private static string Code128Invocations(IReadOnlyList<int> values, bool zpl)
    {
        var output = new StringBuilder();
        var codeSet = ' ';
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (index == 0)
            {
                codeSet = value switch { 103 => 'A', 104 => 'B', 105 => 'C', _ => throw new ArgumentException("Invalid Code128 start code.", nameof(values)) };
                output.Append(zpl ? codeSet switch { 'A' => ">9", 'B' => ">:", _ => ">;" } : $"!{value:000}");
                continue;
            }
            if (value == 102)
            {
                output.Append(zpl ? ">8" : "!102");
            }
            else if (codeSet == 'C' && value < 100)
            {
                output.Append(value.ToString("00", CultureInfo.InvariantCulture));
            }
            else if (value == 99)
            {
                output.Append(zpl ? ">5" : "!099");
                codeSet = 'C';
            }
            else if ((value == 100 && codeSet != 'B') || (value == 101 && codeSet != 'A'))
            {
                var next = value == 100 ? 'B' : 'A';
                output.Append(zpl ? next switch { 'B' => ">6", _ => ">7" } : $"!{value:000}");
                codeSet = next;
            }
            else
            {
                var character = (char)(codeSet == 'B' || value < 64 ? value + 32 : value - 64);
                output.Append(zpl && character == '>' ? "><" : character.ToString());
            }
        }
        return output.ToString();
    }

    private static string ZplFieldData(string value, string prefix = "")
    {
        if (!value.Contains('^') && !value.Contains('~'))
        {
            return "^FD" + prefix + value;
        }
        return "^FH_^FD" + prefix + value
            .Replace("_", "_5F", StringComparison.Ordinal)
            .Replace("^", "_5E", StringComparison.Ordinal)
            .Replace("~", "_7E", StringComparison.Ordinal);
    }

    private static string MmFormat(double value)
    {
        var rounded = Math.Round(value, 2, MidpointRounding.ToEven);
        return rounded == Math.Truncate(rounded)
            ? rounded.ToString("0", CultureInfo.InvariantCulture)
            : rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string QuoteTspl(string value) =>
        "\"" + value.Replace("\"", "\\[\"]", StringComparison.Ordinal) + "\"";

    private static int Dots(double millimeters, int dpi) =>
        (int)Math.Floor(millimeters * dpi / 25.4 + 0.5);

    private static string Fill(object? source, IReadOnlyDictionary<string, object?> data)
    {
        if (source is null || IsJsonNull(source))
        {
            return string.Empty;
        }
        var text = PythonString(source);
        return Placeholder.Replace(text, match =>
        {
            var value = Lookup(data, match.Groups[1].Value.Trim());
            if (value is null || IsJsonNull(value) || value is string empty && empty.Length == 0)
            {
                return match.Groups[2].Success ? match.Groups[2].Value.Trim() : string.Empty;
            }
            return PythonString(value);
        });
    }

    private static object? Lookup(IReadOnlyDictionary<string, object?> data, string path) =>
        Lookup((object)data, path);

    private static object? Lookup(object? current, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (current is JsonElement json)
            {
                if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty(part, out var property))
                {
                    current = property;
                    continue;
                }
                if (json.ValueKind == JsonValueKind.Array
                    && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && index >= 0
                    && index < json.GetArrayLength())
                {
                    current = json[index];
                    continue;
                }
                return null;
            }
            if (current is IReadOnlyDictionary<string, object?> readOnlyDictionary
                && readOnlyDictionary.TryGetValue(part, out var readOnlyValue))
            {
                current = readOnlyValue;
                continue;
            }
            if (current is IDictionary<string, object?> dictionary
                && dictionary.TryGetValue(part, out var value))
            {
                current = value;
                continue;
            }
            if (current is System.Collections.IList list
                && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var listIndex)
                && listIndex >= 0
                && listIndex < list.Count)
            {
                current = list[listIndex];
                continue;
            }
            return null;
        }
        return current;
    }

    private static bool IsJsonNull(object? value) =>
        value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    private static string PythonString(object? value)
    {
        if (value is null || IsJsonNull(value))
        {
            return "None";
        }
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                JsonValueKind.Number => element.GetRawText(),
                _ => element.GetRawText()
            };
        }
        if (value is bool boolean)
        {
            return boolean ? "True" : "False";
        }
        return value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty
            : value.ToString() ?? string.Empty;
    }

    private static bool IsTruthy(object? value)
    {
        if (value is null || IsJsonNull(value))
        {
            return false;
        }
        if (value is string text)
        {
            return text.Length > 0;
        }
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
                JsonValueKind.String => element.GetString()?.Length > 0,
                JsonValueKind.Number => element.GetDouble() != 0,
                _ => true
            };
        }
        return value switch
        {
            bool boolean => boolean,
            byte number => number != 0,
            sbyte number => number != 0,
            short number => number != 0,
            ushort number => number != 0,
            int number => number != 0,
            uint number => number != 0,
            long number => number != 0,
            ulong number => number != 0,
            float number => number != 0,
            double number => number != 0,
            decimal number => number != 0,
            _ => true
        };
    }

    private static JsonElement[] Elements(JsonElement template)
    {
        if (template.ValueKind != JsonValueKind.Object
            || !template.TryGetProperty("elements", out var elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Label template must contain an elements array.");
        }
        return elements.EnumerateArray().ToArray();
    }

    private static object? GetValue(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
            ? value
            : null;

    private static string? GetString(JsonElement element, string propertyName)
    {
        var value = GetValue(element, propertyName);
        return value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            _ => PythonString(value)
        };
    }

    private static bool Truthy(JsonElement element, string propertyName) =>
        IsTruthy(GetValue(element, propertyName));

    private static double Number(JsonElement element, string propertyName, double defaultValue = 0)
    {
        var value = GetValue(element, propertyName);
        if (value is null || IsJsonNull(value) || value is string { Length: 0 })
        {
            return defaultValue;
        }
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.String && json.GetString() == string.Empty)
            {
                return defaultValue;
            }
            if (json.ValueKind == JsonValueKind.Number)
            {
                return json.GetDouble();
            }
            if (json.ValueKind == JsonValueKind.True)
            {
                return 1;
            }
            if (json.ValueKind == JsonValueKind.False)
            {
                return 0;
            }
            value = PythonString(json);
        }
        if (value is bool boolean)
        {
            return boolean ? 1 : 0;
        }
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static double RequiredNumber(JsonElement element, string propertyName)
    {
        var value = GetValue(element, propertyName);
        if (value is null || IsJsonNull(value))
        {
            throw new ArgumentException($"Template is missing required numeric property '{propertyName}'.", nameof(element));
        }
        return Number(element, propertyName);
    }

    private static Encoding CreateGbkEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    private sealed record RenderedCode(
        string Symbology,
        string Kind,
        object Modules,
        string Data,
        string HumanText,
        bool Gs1,
        string Ecc);

    private sealed record AxisWeights(int Start, double[] Values);
}
