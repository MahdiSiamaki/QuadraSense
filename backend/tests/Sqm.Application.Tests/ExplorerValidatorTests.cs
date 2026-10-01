using Sqm.Application.Explorer;
using Sqm.Application.Identity;
using Sqm.Contracts.Explorer;

namespace Sqm.Application.Tests;

/// <summary>
/// A query is either checked and typed, or refused with problems that say where and why - and what
/// it needs permission for.
/// </summary>
public sealed class ExplorerValidatorTests
{
    private static readonly ExplorerOptions Options = new();

    private static ExplorerFilter Is(string field, ExplorerOperator op, params string[] values) => new(Field: field, Operator: op, Values: values);

    private static CheckedExplorerQuery Valid(ExplorerQueryRequest request)
    {
        var (query, problems) = ExplorerValidator.Check(request, Options);
        Assert.True(query is not null, string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}")));
        return query!;
    }

    private static IReadOnlyList<ExplorerProblem> Problems(ExplorerQueryRequest request)
    {
        var (query, problems) = ExplorerValidator.Check(request, Options);
        Assert.Null(query);
        Assert.NotEmpty(problems);
        return problems;
    }

    [Fact]
    public void A_number_is_read_however_it_was_typed()
    {
        foreach (var typed in new[] { "9131234567", "0913 123 4567", "+98 913 123 4567", "0098-913-123-4567" })
        {
            var condition = (CheckedCondition)Valid(new(ExplorerDataset.Bindings, Is("msisdn", ExplorerOperator.Equals, typed))).Where!;
            Assert.Equal(9131234567UL, condition.Values[0]);
        }
    }

    [Fact]
    public void Default_columns_are_used_when_none_are_named_and_need_the_permissions_they_show()
    {
        var query = Valid(new(ExplorerDataset.Bindings, Is("model", ExplorerOperator.Equals, "Galaxy A01")));

        Assert.Equal(["msisdn", "imsi", "imei", "brand", "model", "active", "lastChangeDate"], query.Columns.Select(c => c.Name));
        Assert.Equal(
            new[] { Permissions.ExplorerQuery, Permissions.LookupImei, Permissions.LookupImsi, Permissions.LookupSubscriber }.Order(StringComparer.Ordinal),
            query.Permissions.Order(StringComparer.Ordinal));
    }

    /// <summary>Counting numbers names nobody; grouping by them lists them.</summary>
    [Fact]
    public void Counting_identifiers_needs_no_lookup_permission_but_grouping_by_them_does()
    {
        var counting = Valid(new(ExplorerDataset.Bindings, Is("tac", ExplorerOperator.Equals, "35480111"),
            GroupBy: ["model"], Measures: [new("numbers", ExplorerAggregate.CountDistinct, "msisdn")]));
        var grouping = Valid(new(ExplorerDataset.Bindings, Is("tac", ExplorerOperator.Equals, "35480111"),
            GroupBy: ["msisdn"], Measures: [new("rows", ExplorerAggregate.Count)]));

        Assert.Equal([Permissions.ExplorerQuery], counting.Permissions);
        Assert.Contains(Permissions.LookupSubscriber, grouping.Permissions);
    }

    [Theory]
    [InlineData("nope", ExplorerOperator.Equals, "x", "where.field")]
    [InlineData("msisdn", ExplorerOperator.Contains, "912", "where.operator")]
    [InlineData("tac", ExplorerOperator.Equals, "3548011", "where.values[0]")]
    [InlineData("imei", ExplorerOperator.Equals, "35480111abc", "where.values[0]")]
    [InlineData("lastChangeDate", ExplorerOperator.Equals, "2026-02-30", "where.values[0]")]
    [InlineData("active", ExplorerOperator.Equals, "yes", "where.values[0]")]
    public void A_bad_condition_is_refused_where_it_is(string field, ExplorerOperator op, string value, string path)
    {
        var problems = Problems(new(ExplorerDataset.Bindings, Is(field, op, value)));

        Assert.Equal(path, Assert.Single(problems).Path);
    }

    [Fact]
    public void Between_takes_the_lower_value_first()
    {
        Assert.Equal("where.values", Assert.Single(Problems(new(ExplorerDataset.Bindings,
            Is("lastChangeDate", ExplorerOperator.Between, "2026-09-30", "2026-09-01")))).Path);
    }

    [Fact]
    public void Limits_on_size_and_depth_are_enforced()
    {
        var tooMany = Is("msisdn", ExplorerOperator.In, [.. Enumerable.Range(0, Options.MaxInValues + 1).Select(i => (9120000000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture))]);

        var deep = Is("active", ExplorerOperator.Equals, "true");
        for (var i = 0; i < Options.MaxDepth; i++)
        {
            deep = new ExplorerFilter(ExplorerLogic.And, [deep]);
        }

        var wide = new ExplorerFilter(ExplorerLogic.Or,
            [.. Enumerable.Range(0, Options.MaxConditions + 1).Select(_ => Is("active", ExplorerOperator.Equals, "true"))]);

        Assert.Contains(Problems(new(ExplorerDataset.Bindings, tooMany)), p => p.Path == "where.values");
        Assert.Contains(Problems(new(ExplorerDataset.Bindings, deep)), p => p.Message.Contains("nest", StringComparison.Ordinal));
        Assert.Contains(Problems(new(ExplorerDataset.Bindings, wide)), p => p.Message.Contains("conditions in one query", StringComparison.Ordinal));
    }

    [Fact]
    public void A_node_is_a_group_or_a_condition_never_both()
    {
        var both = new ExplorerFilter(ExplorerLogic.And, [Is("active", ExplorerOperator.Equals, "true")], Field: "msisdn");

        Assert.Equal("where", Assert.Single(Problems(new(ExplorerDataset.Bindings, both))).Path);
    }

    [Fact]
    public void Grouping_and_measures_are_checked_against_each_other()
    {
        var noMeasure = Problems(new(ExplorerDataset.Bindings, GroupBy: ["model"]));
        var clash = Problems(new(ExplorerDataset.Bindings, GroupBy: ["model"], Measures: [new("imei", ExplorerAggregate.Count)]));
        var havingOnField = Problems(new(ExplorerDataset.Bindings, GroupBy: ["model"],
            Measures: [new("n", ExplorerAggregate.Count)], Having: Is("model", ExplorerOperator.Equals, "x")));
        var minOfText = Problems(new(ExplorerDataset.Bindings, GroupBy: ["model"], Measures: [new("m", ExplorerAggregate.Min, "brand")]));
        var columnsToo = Problems(new(ExplorerDataset.Bindings, Columns: ["imei"], GroupBy: ["model"], Measures: [new("n", ExplorerAggregate.Count)]));

        Assert.Contains(noMeasure, p => p.Path == "measures");
        Assert.Contains(clash, p => p.Path == "measures[0].name");
        Assert.Contains(havingOnField, p => p.Path == "having.field");
        Assert.Contains(minOfText, p => p.Path == "measures[0].field");
        Assert.Contains(columnsToo, p => p.Path == "columns");
    }

    [Fact]
    public void A_grouped_query_returns_its_groups_then_its_measures()
    {
        var query = Valid(new(ExplorerDataset.Bindings, Is("tac", ExplorerOperator.Equals, "35480111"),
            GroupBy: ["imei"],
            Measures: [new("sims", ExplorerAggregate.CountDistinct, "imsi", ActiveOnly: true), new("last", ExplorerAggregate.Max, "lastChangeDate")],
            Having: Is("sims", ExplorerOperator.GreaterThan, "10")));

        Assert.Equal(["imei", "sims", "last"], query.Columns.Select(c => c.Name));
        Assert.Equal([ExplorerFieldType.Imei, ExplorerFieldType.Number, ExplorerFieldType.Date], query.Columns.Select(c => c.Type));
        Assert.Equal(10L, ((CheckedCondition)query.Having!).Values[0]);
    }

    [Fact]
    public void Events_need_a_date_range_every_row_must_meet()
    {
        var none = Problems(new(ExplorerDataset.Events, Is("msisdn", ExplorerOperator.Equals, "9120000001")));
        var oneEnd = Problems(new(ExplorerDataset.Events, Is("date", ExplorerOperator.GreaterOrEqual, "2026-09-01")));
        var insideOr = Problems(new(ExplorerDataset.Events, new ExplorerFilter(ExplorerLogic.Or,
            [Is("date", ExplorerOperator.Between, "2026-09-01", "2026-09-30"), Is("msisdn", ExplorerOperator.Equals, "9120000001")])));

        var pair = Valid(new(ExplorerDataset.Events, new ExplorerFilter(ExplorerLogic.And,
            [Is("date", ExplorerOperator.GreaterThan, "2026-08-31"), Is("date", ExplorerOperator.LessOrEqual, "2026-09-30")])));

        Assert.All([none, oneEnd, insideOr], p => Assert.Contains(p, x => x.Path == "where"));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), (pair.FromDate, pair.ToDate));
    }

    [Fact]
    public void Paging_stops_at_the_reachable_rows()
    {
        var last = Valid(new(ExplorerDataset.Bindings, Page: Options.MaxReachableRows / 500, PageSize: 500));
        var past = Problems(new(ExplorerDataset.Bindings, Page: (Options.MaxReachableRows / 500) + 1, PageSize: 500));
        var tooBig = Problems(new(ExplorerDataset.Bindings, PageSize: Options.MaxPageSize + 1));

        Assert.Equal(Options.MaxReachableRows - 500, last.Offset);
        Assert.Contains(past, p => p.Path == "page");
        Assert.Contains(tooBig, p => p.Path == "pageSize");
    }

    [Fact]
    public void Sorting_is_by_a_returned_column_only()
    {
        Assert.Contains(Problems(new(ExplorerDataset.Bindings, Columns: ["imei"], Sort: [new("msisdn")])), p => p.Path == "sort[0].field");
        Assert.Single(Valid(new(ExplorerDataset.Bindings, Columns: ["imei"], Sort: [new("imei", Descending: true)])).Sort);
    }
}
