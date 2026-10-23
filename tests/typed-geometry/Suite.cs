using System.Security.Cryptography;
using System.Text;

namespace Bim.TypedGeometry.Tests;

// Collects every failure so one CI run reports all of them; failures are also emitted as
// GitHub annotations because they are the only CI output readable without authentication.
public sealed class Suite
{
    readonly List<string> failures = new();
    public int Passed { get; private set; }
    public int Failed => failures.Count;

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static string OneLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("%", "%25");

    public void Fail(string name, string detail)
    {
        failures.Add(name);
        Console.WriteLine("::error title=typed-geometry::FAIL " + OneLine(name) + " | " + OneLine(detail));
    }

    public void Check(bool condition, string name)
    {
        if (condition) { Passed++; Console.WriteLine("PASS " + name); }
        else Fail(name, "condition false");
    }

    public void Equal(string name, string expected, string actual)
    {
        if (expected == actual) { Passed++; Console.WriteLine("PASS " + name); }
        else Fail(name, "expected " + expected + " actual " + actual);
    }

    public void Sha(string name, string expectedSha256, string actual)
    {
        var sha = Sha256(actual);
        if (sha == expectedSha256) { Passed++; Console.WriteLine("PASS " + name); }
        else Fail(name, "expected sha " + expectedSha256 + " actual sha " + sha + " bytes " + actual);
    }

    public void Throws<T>(string name, Action action, string? message = null) where T : Exception
    {
        try { action(); }
        catch (T error) when (message is null || error.Message == message) { Passed++; Console.WriteLine("PASS " + name); return; }
        catch (Exception error) { Fail(name, "threw " + error.GetType().Name + ": " + error.Message); return; }
        Fail(name, "no exception");
    }
}

public static class Repository
{
    public static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "capabilities", "capabilities.json")) && File.Exists(Path.Combine(directory.FullName, "examples", "before.json"))) return directory.FullName;
        }
        throw new InvalidOperationException("repository_root_not_found");
    }
}
