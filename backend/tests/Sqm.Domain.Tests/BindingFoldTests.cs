using Sqm.Domain.Bindings;

namespace Sqm.Domain.Tests;

/// <summary>
/// Tests for the state machine everything else depends on.
/// </summary>
/// <remarks>
/// These assert the behaviour that was <i>measured</i> in discovery, not behaviour that was invented.
/// Where a test cites a percentage, that figure came from replaying real events.
/// </remarks>
public class BindingFoldTests
{
    [Theory]
    // A binding never seen before, then added: becomes active.
    [InlineData(BindingState.Unknown, ChangeLabel.Add, BindingState.Active, FoldOutcome.Applied)]
    // A binding that was removed, then re-added: becomes active again. Common — bindings toggle
    // repeatedly across days as subscribers move a SIM between handsets.
    [InlineData(BindingState.Inactive, ChangeLabel.Add, BindingState.Active, FoldOutcome.Applied)]
    // The normal removal.
    [InlineData(BindingState.Active, ChangeLabel.Remove, BindingState.Inactive, FoldOutcome.Applied)]
    public void Normal_transitions_change_state(
        BindingState from, ChangeLabel label, BindingState expected, FoldOutcome outcome)
    {
        var result = BindingFold.Apply(from, label);

        Assert.Equal(expected, result.State);
        Assert.Equal(outcome, result.Outcome);
        Assert.True(result.RequiresWrite);
    }

    [Theory]
    // Measured at 1.77% of all removes. Must be a silent no-op, not a failure: a pipeline that
    // errored here would reject part of every daily file.
    [InlineData(BindingState.Unknown)]
    [InlineData(BindingState.Inactive)]
    public void Remove_of_an_inactive_binding_is_an_idempotent_no_op(BindingState from)
    {
        var result = BindingFold.Apply(from, ChangeLabel.Remove);

        Assert.Equal(from, result.State);
        Assert.Equal(FoldOutcome.OrphanRemove, result.Outcome);
        Assert.False(result.RequiresWrite);
    }

    [Fact]
    public void Add_of_an_already_active_binding_is_reported_but_does_not_change_state()
    {
        // Never observed across 8,062,257 real transitions, so if this ever fires in production it
        // means the source changed or our ordering broke. It is handled, counted, and alerted on.
        var result = BindingFold.Apply(BindingState.Active, ChangeLabel.Add);

        Assert.Equal(BindingState.Active, result.State);
        Assert.Equal(FoldOutcome.RedundantAdd, result.Outcome);
        Assert.False(result.RequiresWrite);
    }

    [Fact]
    public void Folding_is_idempotent_replaying_the_same_events_gives_the_same_state()
    {
        // This is what makes reprocessing safe. If it were false, re-running a batch after a
        // partial failure could not be trusted.
        ChangeLabel[] events =
        [
            ChangeLabel.Add, ChangeLabel.Remove, ChangeLabel.Add,
            ChangeLabel.Add, ChangeLabel.Remove, ChangeLabel.Remove,
        ];

        var once = BindingFold.FoldAll(BindingState.Unknown, events);
        var twice = BindingFold.FoldAll(BindingState.Unknown, events.Concat(events));

        Assert.Equal(BindingState.Inactive, once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Final_state_depends_only_on_the_last_event()
    {
        // The invariant that lets the pipeline be re-run safely and lets the current-state table be
        // rebuilt from the event log at any time.
        foreach (var initial in new[] { BindingState.Unknown, BindingState.Active, BindingState.Inactive })
        {
            var endingInAdd = BindingFold.FoldAll(initial,
                [ChangeLabel.Remove, ChangeLabel.Add, ChangeLabel.Remove, ChangeLabel.Add]);
            Assert.Equal(BindingState.Active, endingInAdd);

            var endingInRemove = BindingFold.FoldAll(initial,
                [ChangeLabel.Add, ChangeLabel.Remove, ChangeLabel.Add, ChangeLabel.Remove]);
            Assert.Equal(BindingState.Inactive, endingInRemove);
        }
    }

    [Fact]
    public void Order_matters_reversing_events_can_change_the_final_state()
    {
        // Proves why ingestion order is tracked explicitly. Measured: within a single batch the same
        // binding appears in 2, 3, 4 and 5+ different daily files, so this is not theoretical.
        ChangeLabel[] forward = [ChangeLabel.Add, ChangeLabel.Remove];
        ChangeLabel[] reversed = [ChangeLabel.Remove, ChangeLabel.Add];

        Assert.Equal(BindingState.Inactive, BindingFold.FoldAll(BindingState.Unknown, forward));
        Assert.Equal(BindingState.Active, BindingFold.FoldAll(BindingState.Unknown, reversed));
    }

    [Fact]
    public void An_empty_sequence_leaves_state_untouched()
    {
        // An empty daily file is a valid zero-change day, not an error.
        Assert.Equal(BindingState.Active, BindingFold.FoldAll(BindingState.Active, []));
    }

    [Fact]
    public void Net_balance_never_exceeds_plus_one_for_any_alternating_sequence()
    {
        // Discovery measured net (adds - removes) per binding and found it never exceeded +1 across
        // the full history. That is a direct consequence of set semantics, asserted here on every
        // alternating sequence up to a reasonable length.
        for (var length = 1; length <= 12; length++)
        {
            var startingWithAdd = Alternating(ChangeLabel.Add, length);
            var state = BindingFold.FoldAll(BindingState.Unknown, startingWithAdd);

            var expected = length % 2 == 1 ? BindingState.Active : BindingState.Inactive;
            Assert.Equal(expected, state);
        }

        static IEnumerable<ChangeLabel> Alternating(ChangeLabel first, int count)
        {
            for (var i = 0; i < count; i++)
            {
                yield return i % 2 == 0
                    ? first
                    : first == ChangeLabel.Add ? ChangeLabel.Remove : ChangeLabel.Add;
            }
        }
    }

    [Fact]
    public void FoldAll_rejects_a_null_sequence()
    {
        Assert.Throws<ArgumentNullException>(() => BindingFold.FoldAll(BindingState.Unknown, null!));
    }
}
