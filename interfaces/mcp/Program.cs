using System.Text;
using Bim.Mcp;
// P1-20: --read-timeout-seconds n (the only server option) is checked before stdin or any pipe is touched; a bad one
// is one typed RevitCommandResponse line on stderr (stdout stays free of anything but JSON-RPC) and exit 1.
if(McpServer.ParseOptions(args,out var readTimeout) is {} refusal) { Console.Error.WriteLine(refusal); Environment.ExitCode=1; return; }
// stdout carries only JSON-RPC responses: acquire the raw stream, then route any stray Console.Out writes to stderr.
var utf8=new UTF8Encoding(false);
using var input=new StreamReader(Console.OpenStandardInput(),utf8);
using var output=new StreamWriter(Console.OpenStandardOutput(),utf8){AutoFlush=true};
Console.SetOut(Console.Error);
McpServer.Run(input,output,new McpServer{ReadTimeout=readTimeout});
