using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using SharedFileJournal;

var command = args.Length > 0 ? args[0] : "stress";
var basePath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "sfj-demo");

switch (command)
{
    case "init":
        Init();
        break;
    case "write":
        Write(args.Length > 2 ? args[2] : "hello from demo");
        break;
    case "read":
        Read();
        break;
    case "compact":
        CompactJournal();
        break;
    case "stress":
        Stress();
        break;
    default:
        Console.WriteLine("Usage: SharedFileJournal.Demo <init|write|read|compact|stress> [basePath] [message]");
        break;
}

void Init()
{
    using var journal = new SharedJournal(basePath);
    Console.WriteLine($"Journal initialized at: {basePath}");
}

void Write(string message)
{
    using var journal = new SharedJournal(basePath);
    var result = journal.Append(Encoding.UTF8.GetBytes(message));
    Console.WriteLine($"Appended record at offset {result.Offset}, length {result.TotalRecordLength}");
}

void Read()
{
    using var journal = new SharedJournal(basePath);
    var count = 0;
    foreach (var record in journal.ReadAll())
    {
        var text = Encoding.UTF8.GetString(record.Payload.Span);
        Console.WriteLine($"  [{count}] offset={record.Offset} len={record.Payload.Length}: {text}");
        count++;
    }
    Console.WriteLine($"Total records: {count}");
}

void CompactJournal()
{
    using var journal = new SharedJournal(basePath);
    var result = journal.Compact();
    Console.WriteLine($"Compaction complete:");
    Console.WriteLine($"  Valid records: {result.ValidRecordCount}");
    Console.WriteLine($"  Valid end offset: {result.ValidEndOffset}");
}

void Stress()
{
    var threadCount = 4;
    var recordsPerThread = 10000;

    Console.WriteLine($"Stress test: {threadCount} threads x {recordsPerThread} records");
    Console.WriteLine($"Journal path: {basePath}");

    // Clean start
    if (File.Exists(basePath)) File.Delete(basePath);

    using var journal = new SharedJournal(basePath);

    var sw = Stopwatch.StartNew();
    var barrier = new Barrier(threadCount);

    var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
    {
        barrier.SignalAndWait();
        for (var i = 0; i < recordsPerThread; i++)
        {
            var payload = Encoding.UTF8.GetBytes($"t{t}-r{i}");
            journal.Append(payload);
        }
    })).ToArray();

    Task.WaitAll(tasks);
    var writeElapsed = sw.Elapsed;

    Console.WriteLine($"Write phase: {writeElapsed.TotalMilliseconds:F1}ms " +
                      $"({threadCount * recordsPerThread / writeElapsed.TotalSeconds:F0} records/sec)");

    sw.Restart();
    var records = journal.ReadAll().ToList();
    var readElapsed = sw.Elapsed;

    Console.WriteLine($"Read phase: {readElapsed.TotalMilliseconds:F1}ms " +
                      $"({records.Count / readElapsed.TotalSeconds:F0} records/sec)");

    var expected = threadCount * recordsPerThread;
    Console.WriteLine($"Records written: {expected}, read back: {records.Count}");

    if (records.Count != expected)
    {
        Console.WriteLine("ERROR: Record count mismatch!");
        Environment.Exit(1);
    }

    // Verify all payloads are valid
    foreach (var record in records)
    {
        var text = Encoding.UTF8.GetString(record.Payload.Span);
        if (!text.StartsWith('t'))
        {
            Console.WriteLine($"ERROR: Invalid payload at offset {record.Offset}: {text}");
            Environment.Exit(1);
        }
    }

    // Compaction check
    var recovery = journal.Compact();
    Console.WriteLine($"Compaction: {recovery.ValidRecordCount} valid records");
    Console.WriteLine("All checks passed!");
}