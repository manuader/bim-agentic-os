using Bim.Core;
using Bim.Mcp;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
// Bounded CLI inputs (S01): every file argument goes through one bounded reader with typed failure codes.
// Runs the real Release CLI dll exactly as tests/mcp does; scripts/verify.ps1 builds interfaces/cli first.
var work=Path.Combine(Path.GetTempPath(),"cli-inputs-"+Guid.NewGuid().ToString("N"));
int passed=0, failed=0, skipped=0;
void Check(bool condition,string name,string detail="") {
    if(condition) { Console.WriteLine("PASS "+name); passed++; return; }
    Console.WriteLine("::error::cli-inputs FAIL "+name+(detail.Length>0?" | "+detail:""));
    failed++;
}
void Skip(string name,string why) { Console.WriteLine("SKIP "+name+": "+why); skipped++; }
string FindRoot() {
    var dir=new DirectoryInfo(AppContext.BaseDirectory);
    while(dir!=null && !File.Exists(Path.Combine(dir.FullName,"global.json"))) dir=dir.Parent;
    if(dir==null) throw new InvalidOperationException("repository root with global.json not found");
    return dir.FullName;
}
try {
    var root=FindRoot();
    string dotnet=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: >0 } host ? host : "dotnet";
    var cliDll=Path.Combine(root,"interfaces","cli","bin","Release","net8.0","Bim.Cli.dll");
    if(!File.Exists(cliDll)) throw new InvalidOperationException("CLI binary missing: "+cliDll+" (build interfaces/cli --configuration Release first, as scripts/verify.ps1 does)");
    Directory.CreateDirectory(work);
    string Example(string name)=>File.ReadAllText(Path.Combine(root,"examples",name));
    string Input(string name,string content) { var path=Path.Combine(work,name); File.WriteAllText(path,content); return path; }
    string Bytes(string name,byte[] content) { var path=Path.Combine(work,name); File.WriteAllBytes(path,content); return path; }
    (int Exit,string Out) Cli(params string[] arguments) {
        var start=new ProcessStartInfo(dotnet){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,StandardOutputEncoding=new UTF8Encoding(false)};
        start.ArgumentList.Add(cliDll);
        foreach(var argument in arguments) start.ArgumentList.Add(argument);
        using var process=Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: "+dotnet);
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        // A FIFO opened for reading blocks until a writer appears; a bounded wait turns that hang into a failure.
        if(!process.WaitForExit(60000)) { process.Kill(true); return (-1,"<timed out>"); }
        process.WaitForExit();
        var text=stdout.Result;
        if(text.EndsWith('\n')) text=text[..^1];
        if(text.EndsWith('\r')) text=text[..^1];
        _=stderr.Result;
        return (process.ExitCode,text);
    }
    string FailedCode(string code)=>JsonSerializer.Serialize(new {status="failed",code});
    void ExpectFailure(string name,string code,params string[] arguments) {
        var (exit,output)=Cli(arguments);
        Check(exit==1 && output==FailedCode(code),name,"exit "+exit+" stdout "+output);
    }

    var beforeText=Example("before.json"); var afterText=Example("after.json"); var intentText=Example("room-intent.json");
    Project P(string json)=>JsonSerializer.Deserialize<Project>(json)!;
    var before=Input("before.json",beforeText); var after=Input("after.json",afterText); var intent=Input("room-intent.json",intentText);
    var missing=Path.Combine(work,"does-not-exist.json");
    const string projectGuid="00000000-0000-0000-0000-000000000001";

    // Valid inputs: stdout is unchanged (byte-equal to the direct Core serialization the CLI has always printed).
    var expectedDiff=JsonSerializer.Serialize(Compiler.Diff(P(beforeText),P(afterText)));
    var diff=Cli("diff",before,after);
    Check(diff.Exit==0 && diff.Out==expectedDiff,"valid diff output unchanged","exit "+diff.Exit);
    var validate=Cli("validate",before);
    Check(validate.Exit==0 && validate.Out=="[]","valid validate output unchanged",validate.Out);
    var expectedRoom=JsonSerializer.Serialize(RoomCompiler.Compile(Guid.Parse(projectGuid),0,JsonSerializer.Deserialize<RoomIntent>(intentText)!));
    var room=Cli("room",projectGuid,"0",intent);
    Check(room.Exit==0 && room.Out==expectedRoom,"valid room output unchanged","exit "+room.Exit);
    // Decoding matches File.ReadAllText: UTF-8 with or without BOM (and BOM-detected UTF-16), same output.
    var bomBefore=Bytes("before-bom.json",[..Encoding.UTF8.GetPreamble(),..Encoding.UTF8.GetBytes(beforeText)]);
    var bomDiff=Cli("diff",bomBefore,after);
    Check(bomDiff.Exit==0 && bomDiff.Out==expectedDiff,"UTF-8 BOM input decodes like File.ReadAllText","exit "+bomDiff.Exit);
    var utf16Before=Bytes("before-utf16.json",[..Encoding.Unicode.GetPreamble(),..Encoding.Unicode.GetBytes(beforeText)]);
    var utf16Diff=Cli("diff",utf16Before,after);
    Check(utf16Diff.Exit==0 && utf16Diff.Out==expectedDiff,"UTF-16 BOM input decodes like File.ReadAllText","exit "+utf16Diff.Exit);
    const string unicodeSource="café <wall> + façade";
    var unicodeIntentText=intentText.Replace("synthetic Test A intent fixture",unicodeSource,StringComparison.Ordinal);
    var unicodeRoom=Cli("room",projectGuid,"0",Input("unicode-intent.json",unicodeIntentText));
    Check(unicodeRoom.Exit==0 && unicodeIntentText!=intentText && unicodeRoom.Out==JsonSerializer.Serialize(RoomCompiler.Compile(Guid.Parse(projectGuid),0,JsonSerializer.Deserialize<RoomIntent>(unicodeIntentText)!)),"non-ASCII UTF-8 input output unchanged","exit "+unicodeRoom.Exit);

    // Size cap: equal to the MCP line cap (8 MiB). Exactly at the cap is accepted; one byte more is rejected.
    Check(McpServer.MaxLineChars==8*1024*1024,"MCP MaxLineChars is 8 MiB");
    var cap=McpServer.MaxLineChars;
    var beforeBytes=Encoding.UTF8.GetBytes(beforeText);
    var atCap=new byte[cap]; beforeBytes.CopyTo(atCap,0); Array.Fill(atCap,(byte)' ',beforeBytes.Length,cap-beforeBytes.Length);
    var atCapPath=Bytes("at-cap.json",atCap);
    var atCapValidate=Cli("validate",atCapPath);
    Check(atCapValidate.Exit==0 && atCapValidate.Out=="[]","input of exactly 8 MiB accepted",atCapValidate.Out);
    var overCap=new byte[cap+1]; atCap.CopyTo(overCap,0); overCap[cap]=(byte)' ';
    var overCapPath=Bytes("over-cap.json",overCap);
    ExpectFailure("8 MiB + 1 input rejected as input_too_large (validate)","input_too_large","validate",overCapPath);
    ExpectFailure("8 MiB + 1 input rejected as input_too_large (diff after)","input_too_large","diff",before,overCapPath);

    // Not a regular file.
    var directory=Path.Combine(work,"a-directory.json"); Directory.CreateDirectory(directory);
    ExpectFailure("directory passed as file rejected (validate)","input_not_regular_file","validate",directory);
    ExpectFailure("directory passed as file rejected (room)","input_not_regular_file","room",projectGuid,"0",directory);
    if(!OperatingSystem.IsWindows()) {
        ExpectFailure("character device /dev/null rejected","input_not_regular_file","validate","/dev/null");
        var fifo=Path.Combine(work,"pipe.json");
        bool made;
        try {
            using var mk=Process.Start(new ProcessStartInfo("mkfifo"){ArgumentList={fifo},UseShellExecute=false});
            made=mk!=null && mk.WaitForExit(10000) && mk.ExitCode==0;
        } catch(System.ComponentModel.Win32Exception) { made=false; }
        if(made) ExpectFailure("FIFO rejected without blocking","input_not_regular_file","validate",fifo);
        else Skip("FIFO rejected without blocking","mkfifo unavailable");
    } else {
        Skip("device and FIFO inputs","Unix-only fixtures; Windows non-disk handles are covered by the same GetFileType check");
    }

    // Links are rejected (symlinks and junctions carry a LinkTarget); creation needs privilege on Windows.
    var link=Path.Combine(work,"link.json");
    bool linked;
    try { File.CreateSymbolicLink(link,before); linked=true; }
    catch(Exception e) when(e is IOException or UnauthorizedAccessException) { linked=false; Skip("symlink inputs","symlink creation failed without privilege: "+e.GetType().Name); }
    if(linked) {
        ExpectFailure("symlink to valid file rejected","input_link_rejected","validate",link);
        var dangling=Path.Combine(work,"dangling.json"); File.CreateSymbolicLink(dangling,Path.Combine(work,"nowhere.json"));
        ExpectFailure("dangling symlink rejected","input_link_rejected","validate",dangling);
        var dirLink=Path.Combine(work,"dir-link.json"); Directory.CreateSymbolicLink(dirLink,directory);
        ExpectFailure("symlink to directory rejected","input_link_rejected","validate",dirLink);
    }

    // Missing inputs, on every verb that reads a file.
    var storeDir=Path.Combine(work,"store");
    var init=Cli("init",storeDir,before);
    Check(init.Exit==0 && init.Out=="{\"status\":\"succeeded\",\"scope\":\"intended_state_only\"}","valid init output unchanged",init.Out);
    ExpectFailure("missing file rejected (validate)","input_not_found","validate",missing);
    ExpectFailure("missing file rejected (diff before)","input_not_found","diff",missing,after);
    ExpectFailure("missing file rejected (diff after)","input_not_found","diff",before,missing);
    ExpectFailure("missing file rejected (room)","input_not_found","room",projectGuid,"0",missing);
    ExpectFailure("missing file rejected (init)","input_not_found","init",Path.Combine(work,"store2"),missing);
    ExpectFailure("missing file rejected (plan)","input_not_found","plan",storeDir,missing);
    ExpectFailure("missing file rejected (commit-intent)","input_not_found","commit-intent",storeDir,missing,missing,"op");
    ExpectFailure("missing file rejected (reconcile)","input_not_found","reconcile",storeDir,missing,missing);
    ExpectFailure("missing file rejected (ifc-export)","input_not_found","ifc-export",missing,after,missing,projectGuid,"op");
    ExpectFailure("missing file rejected (native, before pipe connect)","input_not_found","native",missing,"bim-agentic-os-2025-999999");
    ExpectFailure("missing directory component rejected","input_not_found","validate",Path.Combine(work,"no-such-dir","x.json"));
    // evaluate keeps its evaluation-report envelope; the typed code is carried in the input failure reason.
    var offline=Cli("evaluate","offline",missing);
    Check(offline.Exit==1 && offline.Out.Contains("evaluation_input_error: input_not_found",StringComparison.Ordinal),"missing file rejected (evaluate offline) with typed reason",offline.Out);
    var offlineBig=Cli("evaluate","offline",overCapPath);
    Check(offlineBig.Exit==1 && offlineBig.Out.Contains("evaluation_input_error: input_too_large",StringComparison.Ordinal),"oversized file rejected (evaluate offline) with typed reason","exit "+offlineBig.Exit);
    var plan=Cli("plan",storeDir,after);
    Check(plan.Exit==0 && plan.Out==JsonSerializer.Serialize(Compiler.Diff(new ProjectStore(storeDir).Read(),P(afterText))),"valid plan output unchanged","exit "+plan.Exit);

    Console.WriteLine(JsonSerializer.Serialize(new {passed,failed,skipped,cli_inputs=failed==0}));
    if(failed>0) Environment.ExitCode=1;
} catch(Exception exception) {
    Console.WriteLine("::error::cli-inputs harness error: "+exception.Message.ReplaceLineEndings(" "));
    Console.Error.WriteLine(exception);
    Environment.ExitCode=1;
} finally {
    try { if(Directory.Exists(work)) Directory.Delete(work,true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
}
