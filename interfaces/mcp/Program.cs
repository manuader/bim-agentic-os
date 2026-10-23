using System.Text;
using Bim.Mcp;
// stdout carries only JSON-RPC responses: acquire the raw stream, then route any stray Console.Out writes to stderr.
var utf8=new UTF8Encoding(false);
using var input=new StreamReader(Console.OpenStandardInput(),utf8);
using var output=new StreamWriter(Console.OpenStandardOutput(),utf8){AutoFlush=true};
Console.SetOut(Console.Error);
McpServer.Run(input,output);
