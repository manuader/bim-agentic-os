using System.Text;

// P5-31/P5-33: a console writer whose every write throws the given exception (attempts counted); nothing is ever stored.
// Shared by tests/cli (compiled by the default glob) and tests/mcp (linked by Bim.Mcp.Tests.csproj), so the CLI and MCP
// sections reject a Console.Error or Console.Out through one class.
internal sealed class RejectingWriter(Exception failure) : TextWriter
{
    public int Attempts { get; private set; }
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) { Attempts++; throw failure; }
    public override void Write(char[] buffer, int index, int count) { Attempts++; throw failure; }
    public override void Write(string? value) { Attempts++; throw failure; }
    public override void WriteLine(string? value) { Attempts++; throw failure; }
    public override void Flush() { }
}
