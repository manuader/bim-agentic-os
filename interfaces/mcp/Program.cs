using System.Text;
using Bim.Cli;
using Bim.Core;
using Bim.Mcp;
// Optional launch argument: --revit-allowlist <file> adds enabled write roots for the revit_* tools (see
// RevitCommandPolicy). It is read once here, never from a tool call.
RevitCommandPolicy? revitPolicy=null;
if(args.Length==2 && args[0]=="--revit-allowlist") {
    try { revitPolicy=RevitCommandPolicy.ForRepository(AppContext.BaseDirectory,BoundedInput.ReadText(args[1])); }
    catch(Exception e) when(e is RevitCommandException or IOException or ArgumentException) { Console.Error.WriteLine("revit allowlist rejected: "+e.Message); Environment.ExitCode=2; return; }
} else if(args.Length!=0) { Console.Error.WriteLine("usage: Bim.Mcp [--revit-allowlist allowlist.txt]"); Environment.ExitCode=2; return; }
// stdout carries only JSON-RPC responses: acquire the raw stream, then route any stray Console.Out writes to stderr.
var utf8=new UTF8Encoding(false);
using var input=new StreamReader(Console.OpenStandardInput(),utf8);
using var output=new StreamWriter(Console.OpenStandardOutput(),utf8){AutoFlush=true};
Console.SetOut(Console.Error);
McpServer.Run(input,output,new McpServer(revitPolicy));
