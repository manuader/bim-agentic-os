using Bim.TypedGeometry.Tests;
using System.Text.Json;

var suite = new Suite();
try
{
    var root = Repository.Root();
    Goldens.Run(suite, root);
    TypedContracts.Run(suite, root);
}
catch (Exception exception)
{
    suite.Fail("unexpected exception", exception.GetType().Name + ": " + exception.Message);
    Console.Error.WriteLine(exception);
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = suite.Passed, failed = suite.Failed }));
if (suite.Failed > 0) Environment.ExitCode = 1;
