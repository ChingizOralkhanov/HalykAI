using Halyk.Core.Scoring;
using Halyk.Core.Submissions;

namespace Halyk.Tests;

public class ScorerTests
{
    private static CovenantAnswer Key(string status, decimal actual, string? evidence = null) =>
        new() { Status = status, Actual = actual, EvidenceTxnId = evidence };

    [Fact]
    public void Exact_answer_with_null_evidence_key_scores_full_cell()
    {
        var key = Key(CovenantStatus.Breach, 1284663.42m);
        var answer = Key(CovenantStatus.Breach, 1284663.42m);

        Scorer.ScoreCell("S1", "6.1", answer, key).Total.Should_Be(1.00m);
    }

    [Fact]
    public void Wrong_status_zeroes_the_whole_cell()
    {
        var key = Key(CovenantStatus.Breach, 100m, "TXN-S1-0001");
        var answer = Key(CovenantStatus.Compliant, 100m, "TXN-S1-0001");

        Scorer.ScoreCell("S1", "6.1", answer, key).Total.Should_Be(0m);
    }

    [Fact]
    public void Half_tolerance_miss_costs_half_of_actual_and_of_the_evidence_slot()
    {
        var key = Key(CovenantStatus.Compliant, 1000m);
        var answer = Key(CovenantStatus.Compliant, 1025m);

        var score = Scorer.ScoreCell("S1", "6.2", answer, key);

        score.ActualPoints.Should_Be(0.15m);
        score.EvidencePoints.Should_Be(0.10m);
        score.Total.Should_Be(0.75m);
    }

    [Fact]
    public void Miss_at_tolerance_keeps_only_the_status_points()
    {
        var key = Key(CovenantStatus.Compliant, 1000m);
        var answer = Key(CovenantStatus.Compliant, 1050m);

        Scorer.ScoreCell("S1", "6.2", answer, key).Total.Should_Be(0.50m);
    }

    [Fact]
    public void Evidence_slot_is_all_or_nothing_when_the_key_names_a_transaction()
    {
        var key = Key(CovenantStatus.Breach, 500m, "TXN-S1-0020");

        Scorer.ScoreCell("S1", "6.3", Key(CovenantStatus.Breach, 500m, "TXN-S1-0020"), key).Total.Should_Be(1.00m);
        Scorer.ScoreCell("S1", "6.3", Key(CovenantStatus.Breach, 500m, "TXN-S1-0021"), key).Total.Should_Be(0.80m);
        Scorer.ScoreCell("S1", "6.3", Key(CovenantStatus.Breach, 500m), key).Total.Should_Be(0.80m);
    }

    [Fact]
    public void Missing_actual_also_burns_the_evidence_slot_when_the_key_is_null()
    {
        var key = Key(CovenantStatus.Compliant, 1000m);
        var answer = new CovenantAnswer { Status = CovenantStatus.Compliant, Actual = null };

        Scorer.ScoreCell("S1", "6.1", answer, key).Total.Should_Be(0.50m);
    }

    [Fact]
    public void Missing_or_malformed_cell_scores_nothing()
    {
        var key = Key(CovenantStatus.Compliant, 1000m);

        Scorer.ScoreCell("S1", "6.1", null, key).Total.Should_Be(0m);
        Scorer.ScoreCell("S1", "6.1", new CovenantAnswer { Status = "compliant", Actual = 1000m }, key).Total.Should_Be(0m);
    }

    [Fact]
    public void Grading_uses_the_value_as_it_will_be_serialized()
    {
        var key = Key(CovenantStatus.Compliant, 105.00m);
        var answer = Key(CovenantStatus.Compliant, 104.999m);

        Scorer.ScoreCell("S1", "6.1", answer, key).Total.Should_Be(1.00m);
    }

    [Fact]
    public void A_key_of_zero_is_treated_as_malformed_rather_than_scored_on_a_ratio()
    {
        var key = Key(CovenantStatus.Compliant, 0m);

        Assert.Null(Scorer.RelativeError(10m, 0m));
        Assert.Equal(1m, Scorer.ActualFactor(0m, 0m));
        Assert.Equal(0m, Scorer.ActualFactor(10m, 0m));
        Assert.Contains("not comparable", Scorer.ScoreCell("S1", "6.1", Key(CovenantStatus.Compliant, 10m), key).Note);
    }

    [Fact]
    public void Report_aggregates_totals_over_all_key_cells()
    {
        var groundTruth = new GroundTruthDocument
        {
            Scenarios =
            {
                ["S1"] = new GroundTruthScenario
                {
                    Covenants =
                    {
                        ["6.1"] = Key(CovenantStatus.Breach, 100m),
                        ["6.2"] = Key(CovenantStatus.Compliant, 200m, "TXN-S1-0002"),
                    },
                },
            },
        };

        var submission = new SubmissionDocument
        {
            Answers =
            {
                ["S1"] = new Dictionary<string, CovenantAnswer>
                {
                    ["6.1"] = Key(CovenantStatus.Breach, 100m),
                    ["6.2"] = Key(CovenantStatus.Breach, 200m, "TXN-S1-0002"),
                },
            },
        };

        var report = Scorer.Score(submission, groundTruth);

        report.CellCount.Should_Be(2);
        report.Total.Should_Be(1.00m);
        report.StatusCorrect.Should_Be(1);
    }
}

internal static class DecimalAssert
{
    public static void Should_Be(this decimal actual, decimal expected) =>
        Assert.Equal(expected, Math.Round(actual, 4));

    public static void Should_Be(this int actual, int expected) => Assert.Equal(expected, actual);
}
