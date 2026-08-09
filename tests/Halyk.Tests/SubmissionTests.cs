using System.Text.Json;
using Halyk.Core.Ledger;
using Halyk.Core.Submissions;
using Halyk.Ingest;

namespace Halyk.Tests;

public class SubmissionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("halyk-tests").FullName;

    private const string Template = """
        {
          "team": "",
          "contact_email": "",
          "model": "",
          "answers": {
            "P1": { "6.1": { "status": null, "actual": null, "evidence_txn_id": null } },
            "B4": { "6.1": { "status": null, "actual": null, "evidence_txn_id": null },
                    "6.2": { "status": null, "actual": null, "evidence_txn_id": null } }
          }
        }
        """;

    private string WriteTemplate()
    {
        var path = Path.Combine(_dir, "template.json");
        File.WriteAllText(path, Template);
        return path;
    }

    [Fact]
    public void Template_drives_the_scenario_list()
    {
        var document = SubmissionIo.FromTemplate(WriteTemplate(), "t", "e", "m");

        Assert.Equal(new[] { "P1", "B4" }, document.Answers.Keys);
        Assert.Equal(3, document.Cells().Count());
    }

    [Fact]
    public void Gaps_are_filled_because_empty_and_wrong_cost_the_same()
    {
        var document = SubmissionIo.FromTemplate(WriteTemplate(), "t", "e", "m");

        SubmissionIo.FillGaps(document);

        Assert.All(document.Cells(), cell =>
        {
            Assert.True(CovenantStatus.IsValid(cell.Answer.Status));
            Assert.NotNull(cell.Answer.Actual);
        });
    }

    [Fact]
    public void Validator_reports_missing_cells_invalid_status_and_negative_actual()
    {
        var templatePath = WriteTemplate();
        var template = SubmissionIo.Load(templatePath);
        var document = SubmissionIo.FromTemplate(templatePath, "t", "e", "m");

        document.Answers["B4"].Remove("6.2");
        document.Answers["P1"]["6.1"].Status = "breach";
        document.Answers["P1"]["6.1"].Actual = -5m;

        var problems = SubmissionValidator.Validate(document, template);

        Assert.Contains(problems, p => p.Location == "answers.B4.6.2" && p.Message == "cell missing");
        Assert.Contains(problems, p => p.Location == "answers.P1.6.1" && p.Message.Contains("COMPLIANT"));
        Assert.Contains(problems, p => p.Location == "answers.P1.6.1" && p.Message.Contains("positive"));
    }

    [Fact]
    public void Actual_is_written_as_a_number_with_two_decimals()
    {
        var document = SubmissionIo.FromTemplate(WriteTemplate(), "t", "e", "m");
        document.Answers["P1"]["6.1"].Status = CovenantStatus.Breach;
        document.Answers["P1"]["6.1"].Actual = 1850000m;

        var json = JsonSerializer.Serialize(document, SubmissionIo.Options);

        Assert.Contains("\"actual\": 1850000.00", json);
        Assert.DoesNotContain("\"1850000", json);
    }

    [Fact]
    public void A_quoted_actual_is_rejected_on_load_rather_than_silently_accepted()
    {
        var path = Path.Combine(_dir, "quoted.json");
        File.WriteAllText(path, """
            { "team": "t", "contact_email": "e", "model": "m",
              "answers": { "P1": { "6.1": { "status": "BREACH", "actual": "1234.50", "evidence_txn_id": null } } } }
            """);

        Assert.Throws<JsonException>(() => SubmissionIo.Load(path));
    }

    [Fact]
    public void A_sign_slip_keeps_its_magnitude_because_actual_is_defined_as_the_modulus()
    {
        var document = SubmissionIo.FromTemplate(WriteTemplate(), "t", "e", "m");
        document.Answers["P1"]["6.1"].Status = CovenantStatus.Breach;
        document.Answers["P1"]["6.1"].Actual = -300000m;

        SubmissionIo.FillGaps(document);

        Assert.Equal(300000m, document.Answers["P1"]["6.1"].Actual);
    }

    [Fact]
    public void Filled_gaps_pass_the_validator()
    {
        var templatePath = WriteTemplate();
        var document = SubmissionIo.FromTemplate(templatePath, "t", "e", "m");

        SubmissionIo.FillGaps(document);

        Assert.Empty(SubmissionValidator.Validate(document, SubmissionIo.Load(templatePath)));
    }

    [Fact]
    public void Scenario_id_comes_from_the_transaction_id_not_the_account()
    {
        Assert.Equal("P1", LedgerTransaction.ParseScenarioId("TXN-P1-0039"));
        Assert.Equal("B4", LedgerTransaction.ParseScenarioId("TXN-B4-0001"));
        Assert.Null(LedgerTransaction.ParseScenarioId("garbage"));
    }

    [Fact]
    public void Ledger_reader_handles_quoted_commas_and_mixed_currencies()
    {
        var path = Path.Combine(_dir, "ledger.csv");
        File.WriteAllText(path, """
            txn_id,date,account_id,counterparty,description,amount,currency
            TXN-P1-0001,2025-01-05,ACC-7801,Acme,"Rent, February",-1200.50,USD
            TXN-P1-0002,2025-02-05,ACC-7801,Acme,Inflow,900.00,EUR
            TXN-B4-0001,2025-03-05,ACC-7204,Beta,Other,-10.00,USD
            TXN-B4-0002,2025-03-06,ACC-7204,Beta,Amount missing in the export,,USD
            """);

        var rows = LedgerReader.Read(path);
        var map = ScenarioMap.Build(rows);

        Assert.Equal(4, rows.Count);
        Assert.Equal("Rent, February", rows[0].Description);
        Assert.Equal(1200.50m, rows[0].AbsAmount);
        Assert.True(rows[0].IsOutflow);
        Assert.Equal("EUR", rows[1].Currency);
        Assert.Equal("P1", map.ScenarioForAccount("ACC-7801"));
        Assert.Equal("B4", map.ScenarioForAccount("ACC-7204"));
        Assert.Empty(map.Anomalies());

        var blank = rows.Single(r => !r.HasAmount);
        Assert.Equal("TXN-B4-0002", blank.TxnId);
        Assert.Null(blank.AbsAmount);
        Assert.False(blank.IsOutflow);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
