using System.Text;
using System.Text.Json;
using HoneyMOM.Infrastructure.Labeling;

namespace HoneyMOM.Infrastructure.Tests.Labeling;

public sealed class LabelTemplateRendererTests
{
    private const string ImageDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC";

    [Fact]
    public void ZplRendersTextBarcodeLinesBoxesAndImagesLikeReference()
    {
        using var template = CreateRenderTemplate();
        var renderer = new LabelTemplateRenderer();
        var data = new Dictionary<string, object?> { ["code"] = "WO-42" };

        var payload = renderer.RenderPrinterPayload(
            template.RootElement,
            data,
            new PrinterSettings("zpl", 300, Darkness: 8, CjkFont: "E:FONT.TTF"),
            copies: 2);

        var expected = string.Join('\n',
        [
            "^XA",
            "^CI28",
            "~SD08",
            "^PW354",
            "^LL236",
            "^LH0,0",
            "^FO12,12^A@N,24,24,E:FONT.TTF^FB236,1,0,C,0^FD\u5de5\u5355 WO-42^FS",
            "^FO13,12^A@N,24,24,E:FONT.TTF^FB236,1,0,C,0^FD\u5de5\u5355 WO-42^FS",
            "^FO12,47^BY1,2^BCN,43,Y,N,N,A^FDAB123456cd^FS",
            "^FO213,47^BQN,2,2^FDHA,QR^FS",
            "^FO213,118^BXN,5,200^FDDM^FS",
            "^FO12,139^GB106,6,6^FS",
            "^FO142,142^GB59,47,47^FS",
            "^FO283,165^GFA,24,24,2,FFF0FFF0FFF0FFF0FFF0FFF0FFF0FFF0FFF0FFF0FFF0FFF0^FS",
            "^PQ2",
            "^XZ"
        ]) + "\n";

        Assert.Equal(expected, Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void TsplRendersTextBarcodeLinesBoxesAndImagesLikeReference()
    {
        using var template = CreateRenderTemplate();
        var renderer = new LabelTemplateRenderer();
        var data = new Dictionary<string, object?> { ["code"] = "WO-42" };

        var payload = renderer.RenderPrinterPayload(
            template.RootElement,
            data,
            new PrinterSettings("tspl", 300, CjkFont: "MYFONT"),
            copies: 2);

        Assert.Equal(
            "53495A45203330206D6D2C3230206D6D0D0A4741502032206D6D2C30206D6D0D0A444952454354494F4E20310D0A434C530D0A544558542037302C31322C224D59464F4E54222C302C312C312C22B9A4B5A520574F2D3432220D0A544558542037312C31322C224D59464F4E54222C302C312C312C22B9A4B5A520574F2D3432220D0A424152434F44452031322C34372C22313238222C34332C322C302C312C312C2241423132333435366364220D0A5152434F4445203231332C34372C482C322C412C302C225152220D0A444D4154524958203231332C3131382C35392C35392C78352C22444D220D0A4241522031322C3133392C3130362C360D0A424152203134322C3134322C35392C34370D0A4249544D4150203238332C3136352C322C31322C302C000F000F000F000F000F000F000F000F000F000F000F000F0D0A5052494E5420322C310D0A",
            Convert.ToHexString(payload));
    }

    [Fact]
    public void MarkerUsesFirstBarcodeOrCodeAndExpandsCommandFields()
    {
        using var template = JsonDocument.Parse(
            """
            {
              "width_mm": 20,
              "height_mm": 10,
              "elements": [
                { "type": "barcode", "data": "{{sn}}-{{line}}" },
                { "type": "barcode", "data": "SECOND" }
              ]
            }
            """);
        using var emptyTemplate = JsonDocument.Parse(
            """{"width_mm":20,"height_mm":10,"elements":[{"type":"text","text":"ignored"}]}""");
        var renderer = new LabelTemplateRenderer();
        var data = new Dictionary<string, object?>
        {
            ["sn"] = "SN-7",
            ["line"] = "L2",
            ["code"] = "WO-42"
        };
        var marker = new MarkerSettings(
            """RUN {job}|{data}|{sn}|{symbology}|{fields.code}\r\n""",
            "qr");

        var text = renderer.RenderMarkerText(template.RootElement, data);

        Assert.Equal("SN-7-L2", text);
        Assert.Equal(
            "RUN J-7|SN-7-L2|SN-7|qr|WO-42\r\n",
            renderer.RenderMarkerCommand(marker, "J-7", text, data));
        Assert.Equal("SN-7", renderer.RenderMarkerText(emptyTemplate.RootElement, data));
    }

    [Fact]
    public void RendererRejectsInvalidBarcodeData()
    {
        using var template = JsonDocument.Parse(
            """{"width_mm":10,"height_mm":10,"elements":[{"type":"barcode","x":0,"y":0,"w":5,"h":5,"symbology":"code128","data":"🙂"}]}""");

        Assert.Throws<ArgumentException>(() =>
            new LabelTemplateRenderer().RenderPrinterPayload(
                template.RootElement,
                new Dictionary<string, object?>(),
                new PrinterSettings("zpl", 203),
                copies: 1));
    }

    [Theory]
    [InlineData("""{"width_mm":10,"height_mm":5}""")]
    [InlineData("""{"width_mm":10,"height_mm":5,"elements":{}}""")]
    [InlineData("""{"width_mm":10,"height_mm":5,"elements":null}""")]
    public void RendererRejectsInvalidElementsCollection(string templateJson)
    {
        using var template = JsonDocument.Parse(templateJson);

        Assert.Throws<InvalidDataException>(() =>
            new LabelTemplateRenderer().RenderPrinterPayload(
                template.RootElement,
                new Dictionary<string, object?>(),
                new PrinterSettings("zpl", 203),
                copies: 1));
    }

    [Fact]
    public void TransparentImagePixelsAreCompositedAgainstWhiteBeforePacking()
    {
        using var template = JsonDocument.Parse(
            """{"width_mm":2,"height_mm":1,"elements":[{"type":"image","x":0,"y":0,"w":2,"h":1,"src":"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADElEQVR4nGNggID/AAEIAQBNGY85AAAAAElFTkSuQmCC"}]}""");

        var payload = new LabelTemplateRenderer().RenderPrinterPayload(
            template.RootElement,
            new Dictionary<string, object?>(),
            new PrinterSettings("zpl", 25),
            copies: 1);

        Assert.Equal(
            "^XA\n^CI28\n^PW2\n^LL1\n^LH0,0\n^FO0,0^GFA,1,1,1,40^FS\n^PQ1\n^XZ\n",
            Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void BicubicImageResizeMatchesReferenceThresholds()
    {
        using var template = JsonDocument.Parse(
            """{"width_mm":1,"height_mm":0.5,"elements":[{"type":"image","x":0,"y":0,"w":1,"h":0.5,"src":"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAAAAADRSSBWAAAAC0lEQVR4nGNg+A8AAQIBAEK+vGgAAAAASUVORK5CYII="}]}""");

        var payload = new LabelTemplateRenderer().RenderPrinterPayload(
            template.RootElement,
            new Dictionary<string, object?>(),
            new PrinterSettings("zpl", 203),
            copies: 1);

        Assert.Equal(
            "^XA\n^CI28\n^PW8\n^LL4\n^LH0,0\n^FO0,0^GFA,4,4,1,F0F0F0F0^FS\n^PQ1\n^XZ\n",
            Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void BicubicImageDownscaleMatchesReferenceThresholds()
    {
        using var template = JsonDocument.Parse(
            """{"width_mm":0.25,"height_mm":0.125,"elements":[{"type":"image","x":0,"y":0,"w":0.25,"h":0.125,"src":"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAgAAAABCAAAAADGa7CfAAAADElEQVR4nGNg+A+BAA/5A/3YOXFBAAAAAElFTkSuQmCC"}]}""");

        var payload = new LabelTemplateRenderer().RenderPrinterPayload(
            template.RootElement,
            new Dictionary<string, object?>(),
            new PrinterSettings("zpl", 203),
            copies: 1);

        Assert.Equal(
            "^XA\n^CI28\n^PW2\n^LL1\n^LH0,0\n^FO0,0^GFA,1,1,1,80^FS\n^PQ1\n^XZ\n",
            Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void EmptyBarcodePlaceholderIsSkipped()
    {
        using var template = JsonDocument.Parse(
            """
            {
              "width_mm": 10,
              "height_mm": 5,
              "elements": [
                { "type": "text", "x": 0, "y": 0, "text": "{{missing}}" },
                { "type": "barcode", "x": 0, "y": 0, "w": 5, "h": 5, "data": "{{missing}}" }
              ]
            }
            """);

        var payload = new LabelTemplateRenderer().RenderPrinterPayload(
            template.RootElement,
            new Dictionary<string, object?>(),
            new PrinterSettings("zpl", 203),
            copies: 1);

        Assert.Equal("^XA\n^CI28\n^PW80\n^LL40\n^LH0,0\n^PQ1\n^XZ\n", Encoding.UTF8.GetString(payload));
    }

    private static JsonDocument CreateRenderTemplate() =>
        JsonDocument.Parse(
            """
            {
              "width_mm": 30,
              "height_mm": 20,
              "elements": [
                { "type": "text", "x": 1, "y": 1, "h": 2, "w": 20, "align": "center", "text": "工单 {{code}}", "bold": true },
                { "type": "barcode", "x": 1, "y": 4, "w": 15, "h": 6, "symbology": "code128", "data": "AB123456cd", "hr": true },
                { "type": "barcode", "x": 18, "y": 4, "w": 5, "h": 5, "symbology": "qr", "data": "QR", "ecc": "H" },
                { "type": "barcode", "x": 18, "y": 10, "w": 5, "h": 5, "symbology": "datamatrix", "data": "DM" },
                { "type": "line", "x": 1, "y": 12, "x2": 10, "y2": 12, "t": 0.5 },
                { "type": "box", "x": 12, "y": 12, "w": 5, "h": 4, "fill": true },
                { "type": "image", "x": 24, "y": 14, "w": 1, "h": 1, "src": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC" }
              ]
            }
            """);
}
