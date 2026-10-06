// Writes (or, with --check, verifies) the synthetic fixture document set, and each document's manifest,
// under tests/fixtures/documents.
//
//   dotnet run --project tools/GenerateFixtureDocuments            # regenerate in place
//   dotnet run --project tools/GenerateFixtureDocuments -- --check # exit 1 if a committed file differs
//
// A regenerated PDF or PNG has a new sha256, so any pin of its hash must be updated in the same change;
// the JSON manifests are plain text.
using AgentForge.GenerateFixtureDocuments;

var check = args.Contains("--check");
var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
if (root is null)
{
    Console.Error.WriteLine("Run from inside the repository (no AgentForge.slnx found above the current directory).");
    return 2;
}

var directory = Path.Combine(root, FixtureDocuments.RelativeDirectory);
Directory.CreateDirectory(directory);
var stale = 0;
foreach (var document in FixtureDocuments.Files)
{
    var path = Path.Combine(directory, document.FileName);
    var bytes = document.Render();
    if (check)
    {
        var same = File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        Console.WriteLine($"{(same ? "OK   " : "STALE")} {FixtureDocuments.RelativeDirectory}/{document.FileName}");
        stale += same ? 0 : 1;
    }
    else
    {
        File.WriteAllBytes(path, bytes);
        Console.WriteLine($"wrote {FixtureDocuments.RelativeDirectory}/{document.FileName} ({bytes.Length} bytes)");
    }
}

return stale == 0 ? 0 : 1;

static string? FindRepositoryRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AgentForge.slnx")))
        {
            return dir.FullName;
        }
    }

    return null;
}
