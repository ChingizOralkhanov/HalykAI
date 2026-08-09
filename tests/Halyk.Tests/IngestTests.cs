using Halyk.Core.Ledger;
using Halyk.Ingest;

namespace Halyk.Tests;

public class IngestTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("halyk-ingest").FullName;
    private string Documents => Path.Combine(_root, "documents");
    private string Work => Path.Combine(_root, "work");

    private string WriteDoc(string relativePath, string content)
    {
        var path = Path.Combine(Documents, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Cache_hit_on_unchanged_file_and_re_extract_on_change()
    {
        var path = WriteDoc("a.txt", "first");
        var first = DocumentInventory.Build(Documents, Work).Single();
        Assert.Equal(5, first.Chars);

        File.WriteAllText(path, "second version");
        var second = DocumentInventory.Build(Documents, Work).Single();

        Assert.Equal(14, second.Chars);
        Assert.NotEqual(first.Sha256, second.Sha256);
    }

    [Fact]
    public void Losing_the_manifest_does_not_throw_away_extracted_text()
    {
        WriteDoc("a.txt", "content");
        DocumentInventory.Build(Documents, Work);

        File.Delete(Path.Combine(Work, "inventory.json"));
        var record = DocumentInventory.Build(Documents, Work).Single();

        Assert.Equal("content".Length, record.Chars);
        Assert.True(File.Exists(Path.Combine(Work, "text", "a.txt.txt")));
    }

    [Fact]
    public void Corrupt_metadata_degrades_to_a_re_extract_instead_of_throwing()
    {
        WriteDoc("a.txt", "content");
        DocumentInventory.Build(Documents, Work);
        File.WriteAllText(Path.Combine(Work, "meta", "a.txt.json"), "{ truncated");

        var record = DocumentInventory.Build(Documents, Work).Single();

        Assert.Null(record.Error);
        Assert.Equal("content".Length, record.Chars);
    }

    [Fact]
    public void Failures_are_reported_and_never_cached()
    {
        WriteDoc("weird.bin", "x");

        var first = DocumentInventory.Build(Documents, Work).Single();
        var second = DocumentInventory.Build(Documents, Work).Single();

        Assert.True(first.Failed);
        Assert.True(second.Failed);
        Assert.False(first.NeedsVision);
        Assert.False(File.Exists(Path.Combine(Work, "meta", "weird.bin.json")));
    }

    [Fact]
    public void Nested_folders_are_inventoried_without_name_collisions()
    {
        WriteDoc("a.txt", "top level");
        WriteDoc(Path.Combine("nested", "a.txt"), "nested one");

        var records = DocumentInventory.Build(Documents, Work);

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.File == "a.txt");
        Assert.Contains(records, r => r.File == "nested/a.txt");
        Assert.Equal(2, records.Select(r => r.TextFile).Distinct().Count());
    }

    [Fact]
    public void Text_files_are_written_without_a_byte_order_mark()
    {
        WriteDoc("a.txt", "content");
        DocumentInventory.Build(Documents, Work);

        var bytes = File.ReadAllBytes(Path.Combine(Work, "text", "a.txt.txt"));

        Assert.Equal((byte)'c', bytes[0]);
    }

    [Fact]
    public void An_account_bound_to_two_scenarios_is_an_anomaly_not_a_silent_overwrite()
    {
        var transactions = new[]
        {
            Txn("TXN-P1-0001", "ACC-1"),
            Txn("TXN-P2-0001", "ACC-1"),
        };

        var map = ScenarioMap.Build(transactions);

        Assert.Contains(map.Anomalies(), a => a.Contains("ACC-1") && a.Contains("P1") && a.Contains("P2"));
    }

    [Fact]
    public void The_answer_sheet_scenarios_come_from_the_template_not_from_the_ledger()
    {
        var transactions = new[]
        {
            Txn("TXN-P1-0001", "ACC-7801"),
            Txn("TXN-9001-0001", "ACC-9001"),
        };

        var map = ScenarioMap.Build(transactions);

        Assert.Equal(2, map.ScenariosInLedger.Count);
        Assert.Equal(new[] { "P1" }, map.Restrict(new[] { "P1", "B4" }));
        Assert.Equal(new[] { "B4" }, map.MissingFromLedger(new[] { "P1", "B4" }));
    }

    [Fact]
    public void A_blank_amount_is_null_but_an_unparseable_one_is_an_error()
    {
        var blank = Path.Combine(_root, "blank.csv");
        File.WriteAllText(blank, """
            txn_id,date,account_id,counterparty,description,amount,currency
            TXN-P1-0001,2025-01-05,ACC-7801,Acme,Missing,,USD
            """);

        Assert.Null(LedgerReader.Read(blank).Single().Amount);

        var broken = Path.Combine(_root, "broken.csv");
        File.WriteAllText(broken, """
            txn_id,date,account_id,counterparty,description,amount,currency
            TXN-P1-0001,2025-01-05,ACC-7801,Acme,Junk,N/A,USD
            """);

        var error = Assert.Throws<InvalidDataException>(() => LedgerReader.Read(broken));
        Assert.Contains("TXN-P1-0001", error.Message);
    }

    private static LedgerTransaction Txn(string txnId, string accountId) =>
        new(txnId, new DateOnly(2025, 1, 1), accountId, "cp", "desc", -1m, "USD");

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
