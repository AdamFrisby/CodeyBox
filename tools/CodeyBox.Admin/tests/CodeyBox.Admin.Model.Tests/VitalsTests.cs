using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>Vital bands: history-relative, Neutral by default, throughput never Bad.</summary>
public sealed class VitalsTests
{
    private static FleetVital ByName(IReadOnlyList<FleetVital> vitals, string name) =>
        Assert.Single(vitals, v => v.Name == name);

    [Fact]
    public void Throughput_NeverReportsBad()
    {
        var options = new AdminModelOptions();
        var histories = new VitalHistories
        {
            ThroughputPerHour = [50, 60, 55, 70, 65],
            QueueDepth = [1, 1, 1, 1, 1],
            InFlight = [1, 1, 1, 1, 1],
            FailureRate = [0, 0, 0, 0, 0],
            Parked = [0, 0, 0, 0, 0],
        };
        // A stone-quiet fleet: nothing completed in the window.
        var snapshot = Fixtures.Snapshot([], history: histories);

        var throughput = ByName(VitalsEvaluator.Evaluate(snapshot, options), "Throughput");

        Assert.Equal(0, throughput.Value);
        Assert.NotEqual(VitalBand.Bad, throughput.Band);
        Assert.Equal(VitalBand.Neutral, throughput.Band);
    }

    [Fact]
    public void Throughput_GoodWhenAboveOwnHistory()
    {
        var completedAt = Fixtures.Now.AddMinutes(-10);
        var items = Enumerable.Range(0, 10)
            .Select(i => Fixtures.Item($"done-{i}", state: "Done", updatedAt: completedAt))
            .ToList();
        var snapshot = Fixtures.Snapshot(items, history: Fixtures.FlatHistory(throughput: 2));

        var throughput = ByName(VitalsEvaluator.Evaluate(snapshot), "Throughput");

        Assert.Equal(VitalBand.Good, throughput.Band);
    }

    [Fact]
    public void QueueDepth_BadDerivesFromSuppliedHistory_NotConstants()
    {
        // Same current depth reads differently against different histories.
        var items = Enumerable.Range(0, 30).Select(i => Fixtures.Item($"q-{i}")).ToList();

        var calm = Fixtures.Snapshot(items, history: Fixtures.FlatHistory(queue: 4));
        var busy = Fixtures.Snapshot(items, history: Fixtures.FlatHistory(queue: 30));

        Assert.Equal(VitalBand.Bad, ByName(VitalsEvaluator.Evaluate(calm), "Queue depth").Band);
        Assert.Equal(VitalBand.Neutral, ByName(VitalsEvaluator.Evaluate(busy), "Queue depth").Band);
    }

    [Fact]
    public void QueueDepth_NeutralWithoutEnoughHistory()
    {
        var items = Enumerable.Range(0, 100).Select(i => Fixtures.Item($"q-{i}")).ToList();
        var snapshot = Fixtures.Snapshot(items, history: new VitalHistories());

        Assert.Equal(
            VitalBand.Neutral,
            ByName(VitalsEvaluator.Evaluate(snapshot), "Queue depth").Band);
    }

    [Fact]
    public void FailureRate_BadWhenExceedingOwnWorst()
    {
        var failedAt = Fixtures.Now.AddHours(-1);
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("bad", state: "Failed", updatedAt: failedAt),
            Fixtures.Item("good", state: "Done", updatedAt: failedAt),
        };
        var snapshot = Fixtures.Snapshot(items, history: Fixtures.FlatHistory(failure: 0));

        Assert.Equal(
            VitalBand.Bad,
            ByName(VitalsEvaluator.Evaluate(snapshot), "Failure rate").Band);
    }

    [Fact]
    public void FailureRate_NeutralWhenWithinOwnRange()
    {
        var failedAt = Fixtures.Now.AddHours(-1);
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("bad", state: "Failed", updatedAt: failedAt),
            Fixtures.Item("good", state: "Done", updatedAt: failedAt),
        };
        var snapshot = Fixtures.Snapshot(items, history: Fixtures.FlatHistory(failure: 0.5));

        Assert.Equal(
            VitalBand.Neutral,
            ByName(VitalsEvaluator.Evaluate(snapshot), "Failure rate").Band);
    }

    [Fact]
    public void StalledFleet_InFlightReadsBad()
    {
        var snapshot = Fixtures.Snapshot(
            [Fixtures.Item("waiting")],
            history: Fixtures.FlatHistory(queue: 1, inFlight: 4));

        Assert.Equal(
            VitalBand.Bad,
            ByName(VitalsEvaluator.Evaluate(snapshot), "In flight").Band);
    }

    [Fact]
    public void Parked_NoneIsGood_SpikeIsBad()
    {
        var empty = Fixtures.Snapshot([], history: Fixtures.FlatHistory());
        Assert.Equal(VitalBand.Good, ByName(VitalsEvaluator.Evaluate(empty), "Parked").Band);

        var parked = Fixtures.Snapshot(
            [Fixtures.Item("p", state: "WaitingForQuotaReset")],
            history: Fixtures.FlatHistory());
        Assert.Equal(VitalBand.Bad, ByName(VitalsEvaluator.Evaluate(parked), "Parked").Band);
    }

    [Fact]
    public void EveryNonNeutralVital_ExplainsItself()
    {
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("bad", state: "Failed", updatedAt: Fixtures.Now.AddHours(-1)),
        };
        items.AddRange(Enumerable.Range(0, 30).Select(i => Fixtures.Item($"q-{i}")));
        var snapshot = Fixtures.Snapshot(items, history: Fixtures.FlatHistory());

        foreach (var vital in VitalsEvaluator.Evaluate(snapshot))
        {
            Assert.False(string.IsNullOrWhiteSpace(vital.Reason));
            if (vital.Band != VitalBand.Neutral)
            {
                Assert.NotEmpty(vital.History);
            }
        }
    }
}
