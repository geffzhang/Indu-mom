using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using HoneyMOM.Application.Labeling;
using HoneyMOM.Application.Metadata;
using HoneyMOM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HoneyMOM.Infrastructure.Labeling;

public sealed class WorkOrderReleaseLabelService : IWorkOrderReleaseLabelService
{
    private const string ReleaseEvent = "wo_release";

    private const string RulesSql = """
        SELECT "template" AS "TemplateId",
               "printer" AS "PrinterId",
               "marker" AS "MarkerId",
               "copies" AS "Copies",
               "customer" AS "Customer",
               "material" AS "MaterialId"
        FROM "label_rule"
        WHERE "event" = @Event AND "enabled" = @Enabled
        ORDER BY "id"
        """;

    private const string TemplateSql = """
        SELECT "id" AS "Id",
               "usage" AS "Usage",
               "status" AS "Status",
               "width_mm" AS "WidthMm",
               "height_mm" AS "HeightMm",
               "layout" AS "Layout"
        FROM "label_template"
        WHERE "id" = @Id
        """;

    private const string PrinterSql = """
        SELECT "id" AS "Id",
               "kind" AS "Kind",
               "enabled" AS "Enabled",
               "dpi" AS "Dpi",
               "darkness" AS "Darkness",
               "cjk_font" AS "CjkFont"
        FROM "printer"
        WHERE "id" = @Id
        """;

    private const string MarkerSql = """
        SELECT "id" AS "Id",
               "enabled" AS "Enabled",
               "cmd_template" AS "CommandTemplate",
               "symbology" AS "Symbology"
        FROM "marker"
        WHERE "id" = @Id
        """;

    private const string WorkOrderSql = """
        SELECT "code" AS "Code",
               "product" AS "ProductId",
               "qty" AS "Quantity",
               "due" AS "Due",
               "customer" AS "Customer"
        FROM "work_order"
        WHERE "id" = @Id
        """;

    private const string MaterialSql = """
        SELECT "code" AS "Code",
               "name" AS "Name"
        FROM "material"
        WHERE "id" = @Id
        """;

    private const string CompanySql = """
        SELECT "value"
        FROM "setting"
        WHERE "key" = @Key
        LIMIT 1
        """;

    private static readonly LabelTemplateRenderer Renderer = new();
    private static readonly JsonElement EmptyElements = JsonSerializer.SerializeToElement(Array.Empty<object>());

    private readonly HoneyMomDbContext _context;
    private readonly DynamicEntityStore _store;
    private readonly IEntitySchemaCatalog _schemaCatalog;
    private readonly TimeProvider _timeProvider;

    public WorkOrderReleaseLabelService(
        HoneyMomDbContext context,
        DynamicEntityStore store,
        IEntitySchemaCatalog schemaCatalog,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _store = store;
        _schemaCatalog = schemaCatalog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<int> QueueAsync(
        WorkOrderReleaseLabelContext workOrder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        var transaction = _context.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException(
                "Work-order release label jobs must use the caller's active transaction.");
        var connection = _context.Database.GetDbConnection();
        var now = _timeProvider.GetLocalNow().DateTime;
        var rules = await QueryAsync<LabelRuleRow>(
            connection,
            transaction,
            RulesSql,
            new { Event = ReleaseEvent, Enabled = true },
            cancellationToken);

        var createdJobs = 0;
        foreach (var rule in rules)
        {
            if (!MatchesWorkOrder(rule, workOrder) || rule.TemplateId is not long templateId)
            {
                continue;
            }

            var template = await QuerySingleOrDefaultAsync<LabelTemplateRow>(
                connection,
                transaction,
                TemplateSql,
                new { Id = templateId },
                cancellationToken);
            if (template is null || string.Equals(template.Status, "obsolete", StringComparison.Ordinal))
            {
                continue;
            }

            var data = await BuildWorkOrderDataAsync(
                connection,
                transaction,
                workOrder,
                now,
                cancellationToken);
            var printer = rule.PrinterId is long printerId
                ? await QuerySingleOrDefaultAsync<PrinterRow>(
                    connection,
                    transaction,
                    PrinterSql,
                    new { Id = printerId },
                    cancellationToken)
                : null;
            var marker = rule.MarkerId is long markerId
                ? await QuerySingleOrDefaultAsync<MarkerRow>(
                    connection,
                    transaction,
                    MarkerSql,
                    new { Id = markerId },
                    cancellationToken)
                : null;
            if (printer is not null && printer.Enabled is not true ||
                marker is not null && marker.Enabled is not true)
            {
                continue;
            }

            var copies = ResolveCopies(rule.Copies);
            await QueueJobAsync(
                connection,
                transaction,
                workOrder,
                template,
                data,
                printer,
                marker,
                copies,
                now,
                cancellationToken);
            createdJobs++;
        }

        return createdJobs;
    }

    private async Task<Dictionary<string, object?>> BuildWorkOrderDataAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkOrderReleaseLabelContext workOrder,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var row = await QuerySingleOrDefaultAsync<WorkOrderRow>(
            connection,
            transaction,
            WorkOrderSql,
            new { Id = workOrder.WorkOrderId },
            cancellationToken)
            ?? throw new KeyNotFoundException($"Work order '{workOrder.WorkOrderCode}' was not found.");

        var material = row.ProductId is long productId
            ? await QuerySingleOrDefaultAsync<MaterialRow>(
                connection,
                transaction,
                MaterialSql,
                new { Id = productId },
                cancellationToken)
            : null;
        var company = await QuerySingleOrDefaultAsync<string>(
            connection,
            transaction,
            CompanySql,
            new { Key = "company" },
            cancellationToken);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = row.Code ?? throw new InvalidDataException("The work order has no code."),
            ["product"] = material is null ? string.Empty : $"{material.Code} {material.Name}",
            ["qty"] = row.Quantity,
            ["due"] = (row.Due ?? now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["customer"] = row.Customer ?? string.Empty,
            ["company"] = company ?? string.Empty,
            ["date"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
    }

    private async Task QueueJobAsync(
        DbConnection connection,
        DbTransaction transaction,
        WorkOrderReleaseLabelContext workOrder,
        LabelTemplateRow template,
        IReadOnlyDictionary<string, object?> data,
        PrinterRow? printer,
        MarkerRow? marker,
        int copies,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["template"] = checked((int)template.Id),
            ["target"] = $"wo:{workOrder.WorkOrderCode}",
            ["ref"] = workOrder.WorkOrderCode,
            ["data"] = JsonSerializer.Serialize(data),
            ["copies"] = copies,
            ["attempts"] = 0,
            ["last_error"] = null,
            ["reprint"] = false,
            ["reason"] = null,
            ["requested_by"] = string.IsNullOrEmpty(workOrder.RequestedBy)
                ? "系统"
                : workOrder.RequestedBy,
            ["event"] = ReleaseEvent,
            ["created_at"] = now,
            ["updated_at"] = now
        };

        if (marker is not null)
        {
            var renderTemplate = CreateRenderTemplate(template);
            var text = Renderer.RenderMarkerText(renderTemplate, data);
            values["kind"] = "mark";
            values["printer"] = null;
            values["marker"] = checked((int)marker.Id);
            values["copies"] = 1;
            values["status"] = "queued";
            values["next_try"] = now;
            values["sent_at"] = null;
            values["log"] = "[]";
            values["payload"] = null;
            await AssignJobCodeAsync(values, now, cancellationToken);
            var jobCode = Convert.ToString(values["code"], CultureInfo.InvariantCulture)
                ?? throw new InvalidDataException("The print job code was not generated.");
            values["ref"] = string.IsNullOrEmpty(workOrder.WorkOrderCode)
                ? text
                : workOrder.WorkOrderCode;
            values["payload"] = Renderer.RenderMarkerCommand(
                new MarkerSettings(marker.CommandTemplate, marker.Symbology),
                jobCode,
                text,
                data);
            await _store.InsertAsync("print_job", values, cancellationToken);
            return;
        }

        if (printer is null || string.Equals(printer.Kind, "browser", StringComparison.Ordinal))
        {
            values["kind"] = "browser";
            values["printer"] = printer is null ? null : checked((int)printer.Id);
            values["marker"] = null;
            values["payload"] = null;
            values["status"] = "done";
            values["next_try"] = null;
            values["sent_at"] = now;
            values["log"] = JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["t"] = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    ["ok"] = true,
                    ["msg"] = "浏览器打印（PDF）"
                }
            });
            await InsertJobAsync(values, now, cancellationToken);
            return;
        }

        var printerKind = printer.Kind ?? "zpl";
        var renderTemplateForPrinter = CreateRenderTemplate(template);
        var payloadBytes = Renderer.RenderPrinterPayload(
            renderTemplateForPrinter,
            data,
            new PrinterSettings(
                printerKind,
                printer.Dpi ?? 203,
                printer.Darkness,
                printer.CjkFont),
            copies);
        values["kind"] = "print";
        values["printer"] = checked((int)printer.Id);
        values["marker"] = null;
        values["payload"] = string.Equals(printerKind, "tspl", StringComparison.Ordinal)
            ? Encoding.GetEncoding(936).GetString(payloadBytes)
            : Encoding.UTF8.GetString(payloadBytes);
        values["status"] = "queued";
        values["next_try"] = now;
        values["sent_at"] = null;
        values["log"] = "[]";
        await InsertJobAsync(values, now, cancellationToken);
    }

    private async Task InsertJobAsync(
        Dictionary<string, object?> values,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await AssignJobCodeAsync(values, now, cancellationToken);
        await _store.InsertAsync("print_job", values, cancellationToken);
    }

    private Task AssignJobCodeAsync(
        IDictionary<string, object?> values,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var jobSchema = _schemaCatalog.Get("print_job")
            ?? throw new InvalidDataException("The print_job entity schema is missing.");
        return EntityAutoCodeGenerator.GenerateAsync(
            _context,
            jobSchema,
            values,
            now,
            cancellationToken);
    }

    private static JsonElement CreateRenderTemplate(LabelTemplateRow template)
    {
        var layout = ParseLayout(template.Layout);
        var elements = layout.ValueKind switch
        {
            JsonValueKind.Object when layout.TryGetProperty("elements", out var value) => value.Clone(),
            JsonValueKind.Array => layout.Clone(),
            _ => EmptyElements
        };
        var width = template.WidthMm is > 0 ? template.WidthMm.Value : 100d;
        var height = template.HeightMm is > 0 ? template.HeightMm.Value : 60d;

        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["width_mm"] = width,
            ["height_mm"] = height,
            ["elements"] = elements
        });
    }

    private static JsonElement ParseLayout(object? value)
    {
        switch (value)
        {
            case string json when !string.IsNullOrWhiteSpace(json):
                using (var document = JsonDocument.Parse(json))
                {
                    return document.RootElement.Clone();
                }
            case JsonDocument document:
                return document.RootElement.Clone();
            case JsonElement element when element.ValueKind != JsonValueKind.Null:
                return element.Clone();
            case null:
            case string:
            case JsonElement:
                return JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
            default:
                return JsonSerializer.SerializeToElement(value);
        }
    }

    private static bool MatchesWorkOrder(
        LabelRuleRow rule,
        WorkOrderReleaseLabelContext workOrder)
    {
        if (!string.IsNullOrEmpty(rule.Customer) &&
            !string.IsNullOrEmpty(workOrder.Customer) &&
            !string.Equals(rule.Customer, workOrder.Customer, StringComparison.Ordinal))
        {
            return false;
        }

        return rule.MaterialId.GetValueOrDefault() == 0 ||
               workOrder.ProductId.GetValueOrDefault() == 0 ||
               rule.MaterialId == workOrder.ProductId;
    }

    private static int ResolveCopies(int? copies) =>
        Math.Clamp(copies is null or 0 ? 1 : copies.Value, 1, 500);

    private static async Task<List<T>> QueryAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<T>(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    private static Task<T?> QuerySingleOrDefaultAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object parameters,
        CancellationToken cancellationToken)
        where T : class =>
        connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));

    private sealed class LabelRuleRow
    {
        public long? TemplateId { get; set; }
        public long? PrinterId { get; set; }
        public long? MarkerId { get; set; }
        public int? Copies { get; set; }
        public string? Customer { get; set; }
        public long? MaterialId { get; set; }
    }

    private sealed class LabelTemplateRow
    {
        public long Id { get; set; }
        public string? Usage { get; set; }
        public string? Status { get; set; }
        public double? WidthMm { get; set; }
        public double? HeightMm { get; set; }
        public object? Layout { get; set; }
    }

    private sealed class PrinterRow
    {
        public long Id { get; set; }
        public string? Kind { get; set; }
        public bool? Enabled { get; set; }
        public int? Dpi { get; set; }
        public int? Darkness { get; set; }
        public string? CjkFont { get; set; }
    }

    private sealed class MarkerRow
    {
        public long Id { get; set; }
        public bool? Enabled { get; set; }
        public string? CommandTemplate { get; set; }
        public string? Symbology { get; set; }
    }

    private sealed class WorkOrderRow
    {
        public string? Code { get; set; }
        public long? ProductId { get; set; }
        public long? Quantity { get; set; }
        public DateTime? Due { get; set; }
        public string? Customer { get; set; }
    }

    private sealed class MaterialRow
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
    }
}
