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
// P5-37: a reply stdout rejects ends serving (McpServer.Run returns the write's exception): exit 1 and one stderr line
// "mcp stdout failed: <type>: <message>", never an unhandled exception; a stderr that throws too is ignored.
// P5-39: that line stays exactly one line: the message is folded with ReplaceLineEndings(" ") (CR, LF, CRLF and the other
// Unicode line breaks each become one space) and a Message that is null prints as empty instead of losing the line to a
// NullReferenceException. P5-41: a Message getter that throws no longer drops the line: the message part becomes
// "<Message threw <the getter's exception type>>" (only types are read), exit 1.
if(McpServer.Run(input,output,new McpServer{ReadTimeout=readTimeout}) is {} stdoutFailure) {
    Environment.ExitCode=1;
    try { string failureText; try { failureText=stdoutFailure.Message?.ReplaceLineEndings(" ")??""; } catch(Exception getterFailure) { failureText="<Message threw "+getterFailure.GetType().FullName+">"; } Console.Error.WriteLine("mcp stdout failed: "+stdoutFailure.GetType().FullName+": "+failureText); } catch(Exception) { }
}
