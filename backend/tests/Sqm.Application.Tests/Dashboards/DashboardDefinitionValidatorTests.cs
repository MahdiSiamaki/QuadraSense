using System.Text.Json;
using System.Text.Json.Nodes;
using Sqm.Application.Dashboards;
using Sqm.Application.Explorer;

namespace Sqm.Application.Tests.Dashboards;

/// <summary>
/// A definition is stored only if every key at every level is one the widget contract names, every
/// value is inside its bounds, and an Explorer query in it could be shared without an identifier.
/// </summary>
public sealed class DashboardDefinitionValidatorTests
{
    private static readonly WidgetContract Contract = WidgetContract.Load();

    private static DashboardDefinitionValidator Validator(DashboardOptions? options = null) =>
        new(Contract, options ?? new DashboardOptions(), new ExplorerOptions(), TimeProvider.System);

    /// <summary>A shareable aggregate: Samsung handsets by model, identifiers only counted.</summary>
    private const string ShareableQuery = """
        {"dataset":"Bindings","where":{"field":"brand","operator":"Equals","values":["Samsung"]},
         "groupBy":["model"],"measures":[{"name":"handsets","aggregate":"CountDistinct","field":"imei","activeOnly":true}]}
        """;

    /// <summary>One widget of a type with every config field at its default, plus what a type has no default for.</summary>
    private static JsonObject Widget(string type, string id = "w_test0001", int x = 0, int y = 0)
    {
        var spec = Contract.Types[type];
        var config = new JsonObject();
        foreach (var (name, field) in spec.Config)
        {
            if (field.Default.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                config[name] = JsonNode.Parse(field.Default.GetRawText());
            }
        }

        if (type == "tools.modelNetworkAge")
        {
            config["tac"] = "35000000";
        }

        if (type == "tools.explorerAggregate")
        {
            config["query"] = JsonNode.Parse(ShareableQuery);
        }

        return new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["configVersion"] = spec.ConfigVersion,
            ["title"] = null,
            ["subtitle"] = null,
            ["config"] = config,
            ["overrides"] = new JsonObject(),
            ["layout"] = new JsonObject { ["x"] = x, ["y"] = y, ["w"] = spec.Size.W, ["h"] = spec.Size.H },
        };
    }

    private static JsonObject Definition(params JsonObject[] widgets) => new()
    {
        ["schemaVersion"] = 1,
        ["defaults"] = new JsonObject { ["countBy"] = "bindings" },
        ["refreshMinutes"] = 0,
        ["widgets"] = new JsonArray([.. widgets.Select(w => (JsonNode?)w.DeepClone())]),
    };

    private static DefinitionCheck Check(JsonNode definition, DashboardOptions? options = null) =>
        Validator(options).Check(JsonSerializer.SerializeToElement(definition));

    private static ValidatedDefinition Valid(JsonNode definition)
    {
        var result = Check(definition);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}")));
        return result.Definition!;
    }

    private static DefinitionError Refused(JsonNode definition, string pathStartsWith)
    {
        var result = Check(definition);
        Assert.False(result.IsValid);
        var error = result.Errors.FirstOrDefault(e => e.Path.StartsWith(pathStartsWith, StringComparison.Ordinal));
        Assert.True(error is not null, $"expected an error at {pathStartsWith}; got: {string.Join("; ", result.Errors.Select(e => e.Path))}");
        return error!;
    }

    [Fact]
    public void The_contract_loads_with_every_type_it_names()
    {
        Assert.Equal(33, Contract.Types.Count);
        Assert.Contains("risk.list", Contract.Types.Keys);
        Assert.All(Contract.Types.Values, t => Assert.True(t.Size.MinW <= t.Size.W && t.Size.MinH <= t.Size.H, t.Type));
    }

    [Fact]
    public void Every_type_with_its_defaults_is_a_valid_widget()
    {
        foreach (var type in Contract.Types.Keys)
        {
            var result = Check(Definition(Widget(type)));
            Assert.True(result.IsValid, $"{type}: {string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}"))}");
        }
    }

    [Fact]
    public void An_empty_dashboard_is_valid()
    {
        var definition = Valid(Definition());
        Assert.Empty(definition.Widgets);
    }

    [Fact]
    public void Normalisation_fills_defaults_and_writes_one_key_order()
    {
        var sparse = new JsonObject
        {
            ["widgets"] = new JsonArray(new JsonObject
            {
                ["layout"] = new JsonObject { ["h"] = 9, ["w"] = 6, ["y"] = 0, ["x"] = 0 },
                ["config"] = new JsonObject { ["dimension"] = "marketingName" },
                ["type"] = "device.top",
                ["id"] = "w_top00001",
                ["configVersion"] = 1,
            }),
            ["schemaVersion"] = 1,
        };

        var definition = Valid(sparse);
        var config = definition.Widgets[0].Config;

        Assert.Equal(10, config.GetProperty("limit").GetInt32());
        Assert.Equal("bar", config.GetProperty("display").GetString());
        Assert.Equal(
            ["dimension", "limit", "display", "sort", "showValues", "showPercent", "excludeUnknownBucket", "clickToFilter"],
            config.EnumerateObject().Select(p => p.Name));

        var json = JsonDocument.Parse(definition.NormalizedJson).RootElement;
        Assert.Equal(["schemaVersion", "defaults", "refreshMinutes", "widgets"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["id", "type", "configVersion", "title", "subtitle", "config", "overrides", "layout"],
            json.GetProperty("widgets")[0].EnumerateObject().Select(p => p.Name));

        // Equal dashboards are equal bytes, however they were written.
        Assert.Equal(definition.NormalizedJson, Valid(JsonNode.Parse(definition.NormalizedJson)!).NormalizedJson);
    }

    [Fact]
    public void A_title_is_trimmed_and_an_empty_one_is_no_title()
    {
        var widget = Widget("change.daily");
        widget["title"] = "   ";
        widget["subtitle"] = "  Shown on the wall  ";

        var checkedWidget = Valid(Definition(widget)).Widgets[0];
        Assert.Null(checkedWidget.Title);
        Assert.Equal("Shown on the wall", checkedWidget.Subtitle);
    }

    [Fact]
    public void An_unknown_key_anywhere_is_refused_not_dropped()
    {
        var root = Definition();
        root["results"] = new JsonArray();
        Refused(root, "results");

        var widget = Widget("change.daily");
        widget["data"] = new JsonArray();
        Refused(Definition(widget), "widgets[0].data");

        var layoutExtra = Widget("change.daily");
        layoutExtra["layout"]!["z"] = 1;
        Refused(Definition(layoutExtra), "widgets[0].layout.z");
    }

    [Fact]
    public void Instance_ids_have_one_shape_and_are_unique()
    {
        Refused(Definition(Widget("change.daily", id: "chart-1")), "widgets[0].id");
        Refused(Definition(Widget("change.daily", id: "w_UPPER123")), "widgets[0].id");
        Refused(Definition(Widget("change.daily", id: "w_same0001"), Widget("change.simHandset", id: "w_same0001", y: 20)), "widgets[1].id");
    }

    [Fact]
    public void An_unknown_type_is_refused()
    {
        var widget = Widget("change.daily");
        widget["type"] = "fraud.detected";
        Refused(Definition(widget), "widgets[0].type");
    }

    [Fact]
    public void A_config_version_this_build_does_not_read_is_refused()
    {
        var widget = Widget("change.daily");
        widget["configVersion"] = 2;
        Assert.Contains("upgrade", Refused(Definition(widget), "widgets[0].configVersion").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_setting_the_type_does_not_have_is_refused()
    {
        var widget = Widget("device.top");
        widget["config"]!["sql"] = "SELECT 1";
        Refused(Definition(widget), "widgets[0].config.sql");
    }

    [Theory]
    [InlineData("limit", 51)]
    [InlineData("limit", 2)]
    [InlineData("limit", 10.5)]
    [InlineData("display", "pie")]
    [InlineData("showValues", "yes")]
    public void Values_outside_the_contract_are_refused(string field, object value)
    {
        var widget = Widget("device.top");
        widget["config"]![field] = JsonSerializer.SerializeToNode(value);
        Refused(Definition(widget), $"widgets[0].config.{field}");
    }

    [Fact]
    public void A_list_setting_takes_each_allowed_value_once_within_its_bounds()
    {
        var duplicate = Widget("change.daily");
        duplicate["config"]!["series"] = new JsonArray("added", "added");
        Refused(Definition(duplicate), "widgets[0].config.series");

        var empty = Widget("change.daily");
        empty["config"]!["series"] = new JsonArray();
        Refused(Definition(empty), "widgets[0].config.series");

        var unknown = Widget("change.daily");
        unknown["config"]!["series"] = new JsonArray("added", "revenue");
        Refused(Definition(unknown), "widgets[0].config.series");
    }

    [Fact]
    public void A_required_setting_cannot_be_missing_or_empty()
    {
        var missing = Widget("device.top");
        missing["config"]!.AsObject().Remove("dimension");
        Refused(Definition(missing), "widgets[0].config.dimension");

        var noTac = Widget("tools.modelNetworkAge");
        noTac["config"]!["tac"] = "";
        Refused(Definition(noTac), "widgets[0].config.tac");

        var badTac = Widget("tools.modelNetworkAge");
        badTac["config"]!["tac"] = "3500000X";
        Refused(Definition(badTac), "widgets[0].config.tac");
    }

    [Fact]
    public void A_nullable_setting_takes_null_and_a_date_must_be_real()
    {
        var comparison = Widget("change.periodComparison");
        comparison["config"]!["baselineFrom"] = null;
        Valid(Definition(comparison));

        comparison["config"]!["baselineFrom"] = "2026-02-30";
        Refused(Definition(comparison), "widgets[0].config.baselineFrom");
    }

    [Fact]
    public void A_risk_list_threshold_override_is_a_whole_number_within_bounds()
    {
        var list = Widget("risk.list");
        list["config"]!["thresholdOverride"] = 6;
        Valid(Definition(list));

        list["config"]!["thresholdOverride"] = -1;
        Refused(Definition(list), "widgets[0].config.thresholdOverride");
    }

    [Fact]
    public void A_layout_must_stay_inside_the_twelve_columns_and_above_the_minimum_size()
    {
        Refused(Definition(Widget("change.daily", x: 1)), "widgets[0].layout.x");

        var narrow = Widget("change.daily");
        narrow["layout"]!["w"] = 3;
        Refused(Definition(narrow), "widgets[0].layout.w");

        var flat = Widget("change.daily");
        flat["layout"]!["h"] = 1;
        Refused(Definition(flat), "widgets[0].layout.h");

        var tall = Widget("change.daily");
        tall["layout"]!["h"] = 41;
        Refused(Definition(tall), "widgets[0].layout.h");

        var negative = Widget("metric.kpi");
        negative["layout"]!["y"] = -1;
        Refused(Definition(negative), "widgets[0].layout.y");
    }

    [Fact]
    public void An_override_must_be_one_the_type_supports()
    {
        var daily = Widget("change.daily");
        daily["overrides"] = new JsonObject { ["vendor"] = "Samsung" };
        Refused(Definition(daily), "widgets[0].overrides.vendor");

        var kpi = Widget("metric.kpi");
        kpi["overrides"] = new JsonObject { ["countBy"] = "handsets" };
        Refused(Definition(kpi), "widgets[0].overrides.countBy");

        var top = Widget("device.top");
        top["overrides"] = new JsonObject { ["countBy"] = "handsets", ["vendor"] = "  Samsung ", ["tac"] = "35000000" };
        var overrides = Valid(Definition(top)).Widgets[0].Overrides;
        Assert.Equal("handsets", overrides.CountBy);
        Assert.Equal("Samsung", overrides.Vendor);

        var movement = Widget("change.vendorMovement");
        movement["overrides"] = new JsonObject { ["range"] = "last90" };
        Assert.Equal("last90", Valid(Definition(movement)).Widgets[0].Overrides.Range);
    }

    [Fact]
    public void Defaults_take_only_lens_values_the_contract_allows()
    {
        var root = Definition();
        root["defaults"] = new JsonObject { ["countBy"] = "devices" };
        Refused(root, "defaults.countBy");

        root["defaults"] = new JsonObject { ["range"] = "2026-09-01..2026-08-01" };
        Refused(root, "defaults.range");

        root["defaults"] = new JsonObject { ["msisdn"] = "9121234567" };
        Refused(root, "defaults.msisdn");

        root["defaults"] = new JsonObject { ["range"] = "last30", ["vendor"] = "Apple", ["includeUnknownDevice"] = false };
        var defaults = Valid(root).Defaults;
        Assert.Equal("last30", defaults.Range);
        Assert.False(defaults.IncludeUnknownDevice);
    }

    [Fact]
    public void Refresh_is_one_of_the_allowed_intervals()
    {
        var root = Definition();
        root["refreshMinutes"] = 1;
        Refused(root, "refreshMinutes");

        root["refreshMinutes"] = 15;
        Assert.Equal(15, Valid(root).RefreshMinutes);
    }

    [Fact]
    public void The_schema_version_must_be_the_one_this_build_writes()
    {
        var root = Definition();
        root["schemaVersion"] = 2;
        Refused(root, "schemaVersion");
    }

    [Fact]
    public void Too_many_widgets_or_too_many_bytes_are_refused()
    {
        var widgets = Enumerable.Range(0, 3).Select(i => Widget("metric.kpi", id: $"w_kpi{i:0000}", y: i * 3)).ToArray();
        var tooMany = Check(Definition(widgets), new DashboardOptions { MaxWidgets = 2 });
        Assert.False(tooMany.IsValid);
        Assert.Contains(tooMany.Errors, e => e.Path == "widgets");

        Assert.False(Check(Definition(widgets), new DashboardOptions { MaxDefinitionBytes = 200 }).IsValid);

        var bigConfig = Widget("risk.list");
        bigConfig["config"]!["deviceTypes"] = new JsonArray([.. Enumerable.Range(0, 20).Select(i => (JsonNode?)new string((char)('a' + i), 100))]);
        Assert.False(Check(Definition(bigConfig), new DashboardOptions { MaxWidgetConfigBytes = 1000 }).IsValid);
    }

    [Fact]
    public void A_shareable_explorer_query_is_stored_in_the_api_shape()
    {
        var definition = Valid(Definition(Widget("tools.explorerAggregate")));
        var query = definition.Widgets[0].Config.GetProperty("query");
        Assert.Equal("Bindings", query.GetProperty("dataset").GetString());
        Assert.Equal("CountDistinct", query.GetProperty("measures")[0].GetProperty("aggregate").GetString());
    }

    [Fact]
    public void An_explorer_query_that_filters_or_groups_on_an_identifier_is_refused()
    {
        var byNumber = Widget("tools.explorerAggregate");
        byNumber["config"]!["query"] = JsonNode.Parse("""
            {"dataset":"Bindings","where":{"field":"msisdn","operator":"Equals","values":["9121234567"]},
             "measures":[{"name":"n","aggregate":"Count"}]}
            """);
        Assert.Contains("numbers, SIMs or handsets", Refused(Definition(byNumber), "widgets[0].config.query").Message, StringComparison.Ordinal);

        var groupedByImei = Widget("tools.explorerAggregate");
        groupedByImei["config"]!["query"] = JsonNode.Parse("""
            {"dataset":"Bindings","where":{"field":"brand","operator":"Equals","values":["Samsung"]},
             "groupBy":["imei"],"measures":[{"name":"n","aggregate":"Count"}]}
            """);
        Refused(Definition(groupedByImei), "widgets[0].config.query");
    }

    [Fact]
    public void An_explorer_query_must_be_an_aggregate()
    {
        var rows = Widget("tools.explorerAggregate");
        rows["config"]!["query"] = JsonNode.Parse("""
            {"dataset":"Bindings","where":{"field":"brand","operator":"Equals","values":["Samsung"]},"columns":["model"]}
            """);
        Refused(Definition(rows), "widgets[0].config.query");

        var unknownMember = Widget("tools.explorerAggregate");
        unknownMember["config"]!["query"] = JsonNode.Parse("""
            {"dataset":"Bindings","sql":"SELECT 1","measures":[{"name":"n","aggregate":"Count"}]}
            """);
        Refused(Definition(unknownMember), "widgets[0].config.query");
    }

    [Fact]
    public void An_events_query_with_last_days_is_checked_as_it_will_run()
    {
        var events = Widget("tools.explorerAggregate");
        events["config"]!["query"] = JsonNode.Parse("""
            {"dataset":"Events","where":{"field":"brand","operator":"Equals","values":["Samsung"]},
             "groupBy":["change"],"measures":[{"name":"n","aggregate":"Count"}]}
            """);

        // Without a range the Explorer refuses an Events query; the widget's N days supply one.
        Refused(Definition(events), "widgets[0].config.query");

        events["config"]!["lastDays"] = 1;
        var stored = Valid(Definition(events)).Widgets[0].Config.GetProperty("query");

        // The dates are filled in when it runs, never stored.
        Assert.DoesNotContain("between", stored.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
