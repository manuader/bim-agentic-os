using Bim.Core;
using Bim.Cli;
using Bim.Mcp;
using Bim.Gltf;
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
    (int Exit,string Out,string Err) Cli(params string[] arguments) {
        var start=new ProcessStartInfo(dotnet){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,StandardOutputEncoding=new UTF8Encoding(false)};
        start.ArgumentList.Add(cliDll);
        foreach(var argument in arguments) start.ArgumentList.Add(argument);
        using var process=Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: "+dotnet);
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        // A FIFO opened for reading blocks until a writer appears; a bounded wait turns that hang into a failure.
        if(!process.WaitForExit(60000)) { process.Kill(true); return (-1,"<timed out>",""); }
        process.WaitForExit();
        var text=stdout.Result;
        if(text.EndsWith('\n')) text=text[..^1];
        if(text.EndsWith('\r')) text=text[..^1];
        return (process.ExitCode,text,stderr.Result.ReplaceLineEndings(" ").Trim());
    }
    string FailedCode(string code)=>JsonSerializer.Serialize(new {status="failed",code});
    void ExpectFailure(string name,string code,params string[] arguments) {
        var (exit,output,error)=Cli(arguments);
        Check(exit==1 && output==FailedCode(code),name,"exit "+exit+" stdout "+output+(error.Length>0?" stderr "+error:""));
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
    Check(BoundedInput.MaxBytes==McpServer.MaxLineChars,"CLI BoundedInput.MaxBytes equals McpServer.MaxLineChars");
    // Static: no CLI verb may bypass the bounded reader.
    var cliSource=File.ReadAllText(Path.Combine(root,"interfaces","cli","Program.cs"));
    Check(!cliSource.Contains("File.ReadAllText",StringComparison.Ordinal) && !cliSource.Contains("File.ReadAllBytes",StringComparison.Ordinal) && !cliSource.Contains("File.OpenRead",StringComparison.Ordinal),"interfaces/cli/Program.cs reads no file except through BoundedInput");
    // Invalid UTF-8 (0xFF) decodes to U+FFFD exactly as File.ReadAllText does, so output is unchanged.
    var marker=Encoding.UTF8.GetBytes("synthetic Test A intent fixture");
    var intentBytes=Encoding.UTF8.GetBytes(intentText);
    var at=intentBytes.AsSpan().IndexOf(marker);
    Check(at>=0,"room intent provenance fixture present");
    if(at>=0) {
        var invalid=(byte[])intentBytes.Clone(); invalid[at]=0xFF;
        var invalidPath=Bytes("invalid-utf8-intent.json",invalid);
        var expectedInvalid=JsonSerializer.Serialize(RoomCompiler.Compile(Guid.Parse(projectGuid),0,JsonSerializer.Deserialize<RoomIntent>(File.ReadAllText(invalidPath))!));
        var invalidRoom=Cli("room",projectGuid,"0",invalidPath);
        var mismatch=Enumerable.Range(0,Math.Min(invalidRoom.Out.Length,expectedInvalid.Length)).FirstOrDefault(i=>invalidRoom.Out[i]!=expectedInvalid[i],-1);
        // The decoded text holds U+FFFD; the serialized output carries it escaped, so the fixture is checked before serialization.
        Check(File.ReadAllText(invalidPath).Contains('\uFFFD',StringComparison.Ordinal),"invalid UTF-8 fixture decodes to U+FFFD via File.ReadAllText");
        Check(invalidRoom.Exit==0 && invalidRoom.Out==expectedInvalid,"invalid UTF-8 byte decodes like File.ReadAllText","exit "+invalidRoom.Exit+" lengths "+invalidRoom.Out.Length+"/"+expectedInvalid.Length+" first mismatch "+mismatch+" stderr "+invalidRoom.Err);
    }
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
        // Permission denied is typed (skipped when the runner can still open a mode-000 file, e.g. as root).
        var locked=Input("locked.json",beforeText);
        File.SetUnixFileMode(locked,UnixFileMode.None);
        bool readable;
        try { using(File.OpenRead(locked)) { } readable=true; } catch(UnauthorizedAccessException) { readable=false; }
        if(readable) Skip("chmod 000 input rejected as input_access_denied","mode-000 file still readable (privileged runner)");
        else ExpectFailure("chmod 000 input rejected as input_access_denied","input_access_denied","validate",locked);
        File.SetUnixFileMode(locked,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    } else {
        Skip("FIFO and chmod inputs","Unix-only fixtures");
        // Device and pipe namespaces are refused before open; NUL is the legacy device name.
        ExpectFailure("validate NUL rejected as input_not_regular_file","input_not_regular_file","validate","NUL");
        ExpectFailure(@"\\.\ device path rejected","input_not_regular_file","validate",@"\\.\NUL");
        ExpectFailure(@"\\?\ path to a regular file rejected","input_not_regular_file","validate",@"\\?\"+Path.GetFullPath(before));
        ExpectFailure("//./ device path rejected","input_not_regular_file","validate","//./NUL");
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


    // P8-07 export-gltf project.json output.glb|output.gltf: bounded input, never-overwrite bounded output, JSON envelope.
    var gProv=new Provenance("test","approved");
    Guid gLevel=Guid.Parse("0a000000-0000-4000-8000-000000000001"), gWall=Guid.Parse("0b000000-0000-4000-8000-000000000001"), gDoor=Guid.Parse("0e000000-0000-4000-8000-000000000001"), gWindow=Guid.Parse("0e000000-0000-4000-8000-000000000002");
    var gProject=new Project(Guid.Parse("3f0c1d2e-0000-4000-8000-0000000000c1"),4,[
        new Element(gLevel,"level",[0],[],gProv),
        new Element(gWall,"wall",[0,0,6,0,3,0.2],[gLevel],gProv),
        new Element(gDoor,"door",[1,0,2,0,0,2.1],[gLevel,gWall],gProv),
        new Element(gWindow,"window",[3.5,0,5,0,0.9,1.2],[gLevel,gWall],gProv)]);
    var gInput=Input("gltf-project.json",JsonSerializer.Serialize(gProject));
    var gExpectedGlb=GltfExporter.ExportGlb(gProject);
    var gExpectedGltf=new UTF8Encoding(false).GetBytes(GltfExporter.ExportGltf(gProject));
    string GHex(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    JsonElement? GEnvelope(string output) { try { return JsonDocument.Parse(output).RootElement; } catch(JsonException) { return null; } }
    bool GSucceeded((int Exit,string Out,string Err) r,string path,string format,byte[] expected) =>
        r.Exit==0 && GEnvelope(r.Out) is {} env && env.GetProperty("status").GetString()=="succeeded" && env.GetProperty("format").GetString()==format
        && env.GetProperty("output").GetString()==path && env.GetProperty("bytes").GetInt64()==expected.Length && env.GetProperty("sha256").GetString()==GHex(expected)
        && env.GetProperty("elements").GetInt32()==4 && env.GetProperty("openings").GetInt32()==2;
    var gGlb=Path.Combine(work,"model.glb");
    var gFirst=Cli("export-gltf",gInput,gGlb);
    Check(GSucceeded(gFirst,gGlb,"glb",gExpectedGlb) && File.Exists(gGlb) && File.ReadAllBytes(gGlb).SequenceEqual(gExpectedGlb),"export-gltf writes the library GLB bytes and a succeeded envelope","exit "+gFirst.Exit+" "+gFirst.Out+" "+gFirst.Err);
    var gGlb2=Path.Combine(work,"model-again.glb");
    var gSecond=Cli("export-gltf",gInput,gGlb2);
    Check(gSecond.Exit==0 && File.Exists(gGlb2) && File.Exists(gGlb) && File.ReadAllBytes(gGlb2).SequenceEqual(File.ReadAllBytes(gGlb)),"export-gltf rerun is byte-identical","exit "+gSecond.Exit+" "+gSecond.Out);
    var gText=Path.Combine(work,"model.gltf");
    var gThird=Cli("export-gltf",gInput,gText);
    Check(GSucceeded(gThird,gText,"gltf",gExpectedGltf) && File.Exists(gText) && File.ReadAllBytes(gText).SequenceEqual(gExpectedGltf),"export-gltf .gltf is the library text as UTF-8 without BOM","exit "+gThird.Exit+" "+gThird.Out+" "+gThird.Err);
    var gUpper=Path.Combine(work,"MODEL-UPPER.GLB");
    var gFourth=Cli("export-gltf",gInput,gUpper);
    Check(GSucceeded(gFourth,gUpper,"glb",gExpectedGlb),"export-gltf accepts an upper-case .GLB extension","exit "+gFourth.Exit+" "+gFourth.Out);
    // Never overwrite: an existing file keeps its bytes, an existing directory stays.
    if(File.Exists(gGlb)) File.WriteAllBytes(gGlb,[1,2,3]);
    ExpectFailure("export-gltf refuses an existing output file","output_exists","export-gltf",gInput,gGlb);
    Check(File.Exists(gGlb) && File.ReadAllBytes(gGlb).SequenceEqual(new byte[]{1,2,3}),"export-gltf leaves an existing output file untouched");
    var gDirOut=Path.Combine(work,"exists-dir.glb"); Directory.CreateDirectory(gDirOut);
    ExpectFailure("export-gltf refuses an existing directory as output","output_exists","export-gltf",gInput,gDirOut);
    // Bounded output path.
    var gObj=Path.Combine(work,"model.obj");
    ExpectFailure("export-gltf refuses an unsupported output extension","output_extension_unsupported","export-gltf",gInput,gObj);
    Check(!File.Exists(gObj),"refused extension creates no file");
    ExpectFailure("export-gltf refuses an output without extension","output_extension_unsupported","export-gltf",gInput,Path.Combine(work,"model"));
    ExpectFailure("export-gltf refuses an output in a missing directory","output_directory_not_found","export-gltf",gInput,Path.Combine(work,"no-such-dir","model.glb"));
    ExpectFailure("export-gltf refuses an empty output path","output_path_invalid","export-gltf",gInput,"");
    ExpectFailure("export-gltf refuses a trailing-separator output path","output_extension_unsupported","export-gltf",gInput,Path.Combine(work,"trail.glb")+Path.DirectorySeparatorChar);
    // Cross-OS goldens (tests/cli also runs on macOS): the tests/gltf opening fixture exported through the CLI hashes to the
    // golden SHA-256 values computed by the independent reimplementation (tests/gltf GoldenOpeningsGlb/Gltf).
    Guid gOpeningWall=Guid.Parse("0b000000-0000-4000-8000-0000000000a1");
    var gOpeningFixture=new Project(Guid.Parse("3f0c1d2e-0000-4000-8000-000000000001"),2,[
        new Element(gLevel,"level",[0],[],gProv),
        new Element(gOpeningWall,"wall",[0,0,6,0,3,0.2],[gLevel],gProv),
        new Element(gDoor,"door",[1,0,2,0,0,2.1],[gLevel,gOpeningWall],gProv),
        new Element(gWindow,"window",[3.5,0,5,0,0.9,1.2],[gLevel,gOpeningWall],gProv)]);
    var gOpeningInput=Input("gltf-opening-fixture.json",JsonSerializer.Serialize(gOpeningFixture));
    foreach(var (gExt,gGolden) in new[]{("glb","47A14D5CE34105E4145CBBD9207B7D7EEBFC472BAB8B23EBF9686AB677966D3F"),("gltf","039FC0A70FB5BEB58EDE15A3BC59816C0EE2A31CDA4BFBCC5B399F7C1FA79DBF")}) {
        var gGoldenOut=Path.Combine(work,"golden-openings."+gExt);
        var gGoldenRun=Cli("export-gltf",gOpeningInput,gGoldenOut);
        Check(gGoldenRun.Exit==0 && File.Exists(gGoldenOut) && GHex(File.ReadAllBytes(gGoldenOut))==gGolden,"export-gltf opening fixture ."+gExt+" equals the cross-OS golden SHA-256","exit "+gGoldenRun.Exit+" "+gGoldenRun.Out);
    }
    // NTFS alternate data stream syntax: any ':' after the drive designator is refused on every OS; nothing is written.
    var gHost=Path.Combine(work,"host.txt");
    if(OperatingSystem.IsWindows()) File.WriteAllBytes(gHost,[7,7,7]);
    var gAds=Path.Combine(work,"host.txt:model.glb");
    ExpectFailure("export-gltf refuses an alternate-data-stream output (host.txt:model.glb)","output_path_invalid","export-gltf",gInput,gAds);
    if(OperatingSystem.IsWindows()) Check(File.ReadAllBytes(gHost).SequenceEqual(new byte[]{7,7,7}) && new FileInfo(gHost).Length==3,"ADS refusal leaves host.txt untouched");
    else Check(!File.Exists(gHost) && !File.Exists(gAds),"ADS refusal creates neither host.txt nor host.txt:model.glb");
    ExpectFailure("export-gltf refuses a relative ':' output","output_path_invalid","export-gltf",gInput,"ab:c.glb");
    var gUnknown=Cli("no-such-verb");
    Check(gUnknown.Exit==1 && gUnknown.Out.Contains("export-gltf project.json output.glb|output.gltf",StringComparison.Ordinal),"default usage lists export-gltf",gUnknown.Out);
    if(OperatingSystem.IsWindows()) {
        ExpectFailure(@"export-gltf refuses a \\.\ device output","output_not_regular_file","export-gltf",gInput,@"\\.\pipe\bim-gltf-test.glb");
    } else Skip("export-gltf device outputs","Windows-only device namespace fixtures");
    if(linked) {
        var gLinkOut=Path.Combine(work,"dangling-out.glb"); File.CreateSymbolicLink(gLinkOut,Path.Combine(work,"nowhere-out.glb"));
        ExpectFailure("export-gltf refuses a dangling symlink output","output_link_rejected","export-gltf",gInput,gLinkOut);
        Check(!File.Exists(Path.Combine(work,"nowhere-out.glb")),"dangling symlink output target is not created");
        var gLinkedDir=Path.Combine(work,"linked-dir"); Directory.CreateSymbolicLink(gLinkedDir,directory);
        ExpectFailure("export-gltf refuses an output inside a symlinked directory","output_link_rejected","export-gltf",gInput,Path.Combine(gLinkedDir,"model.glb"));
    }
    // Bounded input: every refusal leaves no output.
    var gNoOut=Path.Combine(work,"never.glb");
    ExpectFailure("export-gltf refuses an oversized input","input_too_large","export-gltf",overCapPath,gNoOut);
    ExpectFailure("export-gltf refuses a missing input","input_not_found","export-gltf",missing,gNoOut);
    ExpectFailure("export-gltf refuses a directory input","input_not_regular_file","export-gltf",directory,gNoOut);
    ExpectFailure("export-gltf refuses malformed JSON","bim_ir_json_invalid","export-gltf",Input("gltf-bad.json","{\"Id\":"),gNoOut);
    var gNull=Cli("export-gltf",Input("gltf-null.json","null"),gNoOut);
    Check(gNull.Exit==1 && GEnvelope(gNull.Out) is {} gNullEnv && gNullEnv.GetProperty("status").GetString()=="failed" && gNullEnv.GetProperty("code").GetString()=="bim_ir_invalid","export-gltf refuses a null project",gNull.Out);
    var gOverlap=gProject with {Elements=[..gProject.Elements,new Element(Guid.Parse("0e000000-0000-4000-8000-000000000003"),"window",[1.5,0,3,0,1,1],[gLevel,gWall],gProv)]};
    var gRefused=Cli("export-gltf",Input("gltf-overlap.json",JsonSerializer.Serialize(gOverlap)),gNoOut);
    Check(gRefused.Exit==1 && GEnvelope(gRefused.Out) is {} gEnv && gEnv.GetProperty("status").GetString()=="failed" && gEnv.GetProperty("code").GetString()=="gltf_opening_overlap" && gEnv.GetProperty("detail").GetString()!.Length>0,
        "export-gltf reports exporter refusals as {status, code, detail}","exit "+gRefused.Exit+" "+gRefused.Out);
    Check(!File.Exists(gNoOut),"refused exports create no output file");
    var gUsage=Cli("export-gltf",gInput);
    Check(gUsage.Exit==1 && GEnvelope(gUsage.Out) is {} gUsageEnv && gUsageEnv.GetProperty("status").GetString()=="failed" && gUsageEnv.GetProperty("code").GetString()!.StartsWith("usage: export-gltf",StringComparison.Ordinal),"export-gltf with a missing argument prints its usage as a failed envelope",gUsage.Out);
    Check(File.ReadAllText(Path.Combine(root,"interfaces","cli","GltfExportCommand.cs")).Contains("BoundedInput.ReadText",StringComparison.Ordinal),"export-gltf reads its input through BoundedInput");

    // P6-07 cad-draft / cad-brief input.dxf|input.pdf output.json [options]: bounded CAD input, never-overwrite .json output,
    // JSON envelope. Oracle: the Bim.Cad library called in-process on the same bytes (tests/cad fixtures).
    var fixtures=Path.Combine(root,"tests","cad","fixtures");
    string Fixture(string name)=>Path.Combine(fixtures,name);
    var cadOut=Path.Combine(work,"cad"); Directory.CreateDirectory(cadOut);
    int cadN=0;
    string Out(string ext=".json")=>Path.Combine(cadOut,"out-"+(++cadN).ToString(System.Globalization.CultureInfo.InvariantCulture)+ext);
    JsonElement? Doc(string path) { try { return JsonDocument.Parse(File.ReadAllBytes(path)).RootElement; } catch(Exception e) when(e is JsonException or IOException) { return null; } }
    string Sha(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    Guid DefaultId(byte[] bytes)=>new Guid(System.Security.Cryptography.SHA256.HashData(bytes).AsSpan(0,16));
    bool Ok((int Exit,string Out,string Err) r,string kind,string output)=>r.Exit==0 && GEnvelope(r.Out) is {} e && e.GetProperty("status").GetString()=="succeeded" && e.GetProperty("kind").GetString()==kind && e.GetProperty("output").GetString()==output && File.Exists(output)
        && e.GetProperty("bytes").GetInt64()==new FileInfo(output).Length && e.GetProperty("sha256").GetString()==Sha(File.ReadAllBytes(output)) && !e.GetProperty("persisted").GetBoolean();
    string R((int Exit,string Out,string Err) r)=>"exit "+r.Exit+" "+r.Out+(r.Err.Length>0?" stderr "+r.Err:"");
    void ExpectCad(string name,string code,params string[] arguments) {
        var output=arguments.Length>2 ? arguments[2] : null;
        var existed=output is not null && (File.Exists(output) || Directory.Exists(output));
        var r=Cli(arguments);
        Check(r.Exit==1 && GEnvelope(r.Out) is {} e && e.GetProperty("status").GetString()=="failed" && e.GetProperty("code").GetString()==code && (existed || output is null || !File.Exists(output)),name,R(r));
    }
    var dxfDraftPath=Fixture("draft-plan.dxf"); var dxfDraftBytes=File.ReadAllBytes(dxfDraftPath);
    var pdfDraftPath=Fixture("draft-plan.pdf"); var pdfDraftBytes=File.ReadAllBytes(pdfDraftPath);
    var dxfBriefPath=Fixture("brief-plan.dxf"); var dxfBriefBytes=File.ReadAllBytes(dxfBriefPath);
    var pdfBriefPath=Fixture("simple-plan.pdf"); var pdfBriefBytes=File.ReadAllBytes(pdfBriefPath);
    var cadId=Guid.Parse("6c0d0000-0000-4000-8000-000000000607");

    // cad-draft DXF with an explicit project id: project/walls/openings/uncertainties equal the library's serialization.
    {
        var lib=Bim.Cad.CadToBimIr.FromDxf(Bim.Cad.DxfReader.Parse(dxfDraftBytes,"draft-plan.dxf"),new Bim.Cad.CadToBimIrOptions(cadId));
        var o=Out(); var r=Cli("cad-draft",dxfDraftPath,o,"--project-id",cadId.ToString());
        Check(Ok(r,"cad-draft",o) && GEnvelope(r.Out) is {} e && e.GetProperty("format").GetString()=="dxf" && e.GetProperty("walls").GetInt32()==lib.Walls.Count && e.GetProperty("openings").GetInt32()==lib.Openings.Count && e.GetProperty("uncertainties").GetInt32()==lib.Uncertainties.Count,
            "cad-draft dxf writes a draft and a succeeded envelope with counts",R(r));
        Check(lib.Walls.Count==8 && lib.Openings.Count==2,"cad-draft dxf library oracle has 8 walls and 2 openings");
        if(Doc(o) is {} d) {
            Check(d.GetProperty("kind").GetString()=="cad-draft" && d.GetProperty("version").GetInt32()==1 && !d.GetProperty("persisted").GetBoolean(),"cad-draft document header");
            var s=d.GetProperty("source");
            Check(s.GetProperty("file").GetString()=="draft-plan.dxf" && s.GetProperty("format").GetString()=="dxf" && s.GetProperty("bytes").GetInt64()==dxfDraftBytes.Length && s.GetProperty("sha256").GetString()==Sha(dxfDraftBytes)
                && s.GetProperty("page").ValueKind==JsonValueKind.Null && s.GetProperty("millimetres_per_unit").GetDouble()==1 && s.GetProperty("scale").GetString()=="insunits","cad-draft dxf source block",s.GetRawText());
            Check(d.GetProperty("project").GetRawText()==JsonSerializer.Serialize(lib.ToProject()),"cad-draft project equals the library BIM-IR draft");
            Check(JsonSerializer.Deserialize<Project>(d.GetProperty("project").GetRawText()) is {} p && p.Id==cadId && p.Elements.Length==1+8+2,"cad-draft project deserializes as BIM-IR with level, walls and openings");
            Check(d.GetProperty("walls").GetRawText()==JsonSerializer.Serialize(lib.Walls) && d.GetProperty("openings").GetRawText()==JsonSerializer.Serialize(lib.Openings),"cad-draft walls and openings equal the library proposals");
            Check(d.GetProperty("uncertainties").GetRawText()==JsonSerializer.Serialize(lib.Uncertainties),"cad-draft uncertainties equal the library report");
        } else Check(false,"cad-draft dxf output is JSON");
        // Default project id: the first 16 bytes of the input SHA-256; reruns are byte-identical.
        var o1=Out(); var o2=Out(); var r1=Cli("cad-draft",dxfDraftPath,o1); var r2=Cli("cad-draft",dxfDraftPath,o2);
        Check(Ok(r1,"cad-draft",o1) && Ok(r2,"cad-draft",o2) && File.ReadAllBytes(o1).SequenceEqual(File.ReadAllBytes(o2)),"cad-draft rerun is byte-identical",R(r1)+" | "+R(r2));
        var libDefault=Bim.Cad.CadToBimIr.FromDxf(Bim.Cad.DxfReader.Parse(dxfDraftBytes,"draft-plan.dxf"),new Bim.Cad.CadToBimIrOptions(DefaultId(dxfDraftBytes)));
        Check(Doc(o1) is {} d1 && d1.GetProperty("project").GetRawText()==JsonSerializer.Serialize(libDefault.ToProject()),"cad-draft default project id is derived from the input SHA-256");
        // --metres-per-unit overrides $INSUNITS.
        var libScaled=Bim.Cad.CadToBimIr.FromDxf(Bim.Cad.DxfReader.Parse(dxfDraftBytes,"draft-plan.dxf"),new Bim.Cad.CadToBimIrOptions(cadId){MillimetresPerUnit=10});
        var o3=Out(); var r3=Cli("cad-draft",dxfDraftPath,o3,"--project-id",cadId.ToString(),"--metres-per-unit","0.01");
        Check(Ok(r3,"cad-draft",o3) && Doc(o3) is {} d3 && d3.GetProperty("source").GetProperty("millimetres_per_unit").GetDouble()==10 && d3.GetProperty("source").GetProperty("scale").GetString()=="metres-per-unit"
            && d3.GetProperty("project").GetRawText()==JsonSerializer.Serialize(libScaled.ToProject()) && d3.GetProperty("uncertainties").GetRawText()==JsonSerializer.Serialize(libScaled.Uncertainties),"cad-draft --metres-per-unit overrides the DXF units",R(r3));
    }
    // cad-draft PDF: calibration from a known dimension, page, door forms.
    {
        var cal=Bim.Cad.PdfCalibration.FromKnownDimension(new Bim.Cad.Point2(100,100),new Bim.Cad.Point2(400,100),6000);
        var lib=Bim.Cad.CadToBimIr.FromPdf(Bim.Cad.PdfReader.Parse(pdfDraftBytes,"draft-plan.pdf"),1,cal,new Bim.Cad.CadToBimIrOptions(cadId){PdfDoorForms=new[]{5}});
        var o=Out(); var r=Cli("cad-draft",pdfDraftPath,o,"--project-id",cadId.ToString(),"--page","1","--calibrate","100,100,400,100,6000","--pdf-door-forms","5");
        Check(Ok(r,"cad-draft",o) && lib.Walls.Count==4 && Doc(o) is {} d && d.GetProperty("source").GetProperty("format").GetString()=="pdf" && d.GetProperty("source").GetProperty("page").GetInt32()==1
            && d.GetProperty("source").GetProperty("scale").GetString()=="known-dimension" && d.GetProperty("source").GetProperty("millimetres_per_unit").GetDouble()==20
            && d.GetProperty("project").GetRawText()==JsonSerializer.Serialize(lib.ToProject()) && d.GetProperty("openings").GetRawText()==JsonSerializer.Serialize(lib.Openings) && d.GetProperty("uncertainties").GetRawText()==JsonSerializer.Serialize(lib.Uncertainties),
            "cad-draft pdf with --calibrate and --pdf-door-forms equals the library draft",R(r));
        // --metres-per-unit on a PDF is a uniform calibration (0.02 m per point = 20 mm).
        var libMpu=Bim.Cad.CadToBimIr.FromPdf(Bim.Cad.PdfReader.Parse(pdfDraftBytes,"draft-plan.pdf"),1,new Bim.Cad.PdfCalibration(20,Bim.Cad.PdfMatrix.Scaling(20)),new Bim.Cad.CadToBimIrOptions(cadId));
        var o2=Out(); var r2=Cli("cad-draft",pdfDraftPath,o2,"--project-id",cadId.ToString(),"--metres-per-unit","0.02");
        Check(Ok(r2,"cad-draft",o2) && Doc(o2) is {} d2 && d2.GetProperty("source").GetProperty("page").GetInt32()==1 && d2.GetProperty("project").GetRawText()==JsonSerializer.Serialize(libMpu.ToProject()),"cad-draft pdf --metres-per-unit, default page 1",R(r2));
        ExpectCad("cad-draft pdf without a scale is cad_uncalibrated","cad_uncalibrated","cad-draft",pdfDraftPath,Out());
        ExpectCad("cad-draft pdf missing page is cad_page_missing","cad_page_missing","cad-draft",pdfDraftPath,Out(),"--page","2","--metres-per-unit","0.02");
        ExpectCad("cad-draft pdf degenerate calibration refused","cad_option_value_invalid","cad-draft",pdfDraftPath,Out(),"--calibrate","100,100,100,100,6000");
    }
    // cad-brief DXF and PDF.
    {
        var lib=Bim.Cad.BriefExtract.FromDxf(Bim.Cad.DxfReader.Parse(dxfBriefBytes,"brief-plan.dxf"));
        var o=Out(); var r=Cli("cad-brief",dxfBriefPath,o);
        Check(Ok(r,"cad-brief",o) && GEnvelope(r.Out) is {} e && e.GetProperty("format").GetString()=="dxf" && e.GetProperty("rooms").GetInt32()==lib.Rooms.Count && e.GetProperty("uncertainties").GetInt32()==lib.Uncertainties.Count && e.GetProperty("issues").GetInt32()==lib.Issues.Count,
            "cad-brief dxf writes a brief draft and a succeeded envelope with counts",R(r));
        Check(lib.Rooms.Count==7,"cad-brief dxf library oracle has 7 rooms");
        if(Doc(o) is {} d) {
            Check(d.GetProperty("kind").GetString()=="cad-brief" && d.GetProperty("version").GetInt32()==1 && !d.GetProperty("persisted").GetBoolean() && d.GetProperty("source").GetProperty("metres_per_unit").GetDouble()==0.001 && d.GetProperty("source").GetProperty("sha256").GetString()==Sha(dxfBriefBytes),"cad-brief document header and source");
            var briefRaw=d.GetProperty("brief").GetRawText();
            Check(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(briefRaw),System.Text.Json.Nodes.JsonNode.Parse(BriefSerializer.Serialize(lib.Brief))),"cad-brief brief equals the P6-03 serialization of the library brief");
            Check(BriefParser.Parse(briefRaw).Rooms.Count==7,"cad-brief brief parses with the P6-03 parser");
            Check(d.GetProperty("rooms").GetRawText()==JsonSerializer.Serialize(lib.Rooms) && d.GetProperty("uncertainties").GetRawText()==JsonSerializer.Serialize(lib.Uncertainties),"cad-brief rooms and uncertainties equal the library");
            Check(d.GetProperty("issues").GetArrayLength()==lib.Issues.Count && (lib.Issues.Count==0 || d.GetProperty("issues")[0].GetProperty("Severity").ValueKind==JsonValueKind.String),"cad-brief issues are listed with string severities");
        } else Check(false,"cad-brief dxf output is JSON");
        var libPdf=Bim.Cad.BriefExtract.FromPdf(Bim.Cad.PdfReader.Parse(pdfBriefBytes,"simple-plan.pdf"),1);
        var o2=Out(); var r2=Cli("cad-brief",pdfBriefPath,o2);
        Check(Ok(r2,"cad-brief",o2) && Doc(o2) is {} d2 && d2.GetProperty("source").GetProperty("metres_per_unit").ValueKind==JsonValueKind.Null && d2.GetProperty("source").GetProperty("scale").GetString()=="none"
            && d2.GetProperty("rooms").GetRawText()==JsonSerializer.Serialize(libPdf.Rooms) && d2.GetProperty("uncertainties").GetRawText()==JsonSerializer.Serialize(libPdf.Uncertainties),"cad-brief uncalibrated pdf is allowed and reports its uncertainty",R(r2));
        var calB=Bim.Cad.PdfCalibration.FromKnownDimension(new Bim.Cad.Point2(0,0),new Bim.Cad.Point2(10,0),1000);
        var libCal=Bim.Cad.BriefExtract.FromPdf(Bim.Cad.PdfReader.Parse(pdfBriefBytes,"simple-plan.pdf"),1,calB);
        var o3=Out(); var r3=Cli("cad-brief",pdfBriefPath,o3,"--calibrate","0,0,10,0,1000","--page","1");
        Check(Ok(r3,"cad-brief",o3) && Doc(o3) is {} d3 && d3.GetProperty("source").GetProperty("metres_per_unit").GetDouble()==0.001 && d3.GetProperty("rooms").GetRawText()==JsonSerializer.Serialize(libCal.Rooms) && d3.GetProperty("uncertainties").GetRawText()==JsonSerializer.Serialize(libCal.Uncertainties),"cad-brief calibrated pdf equals the library",R(r3));
        var libMpu=Bim.Cad.BriefExtract.FromPdf(Bim.Cad.PdfReader.Parse(pdfBriefBytes,"simple-plan.pdf"),1,null,new Bim.Cad.BriefExtractOptions{MetresPerUnit=0.05});
        var o4=Out(); var r4=Cli("cad-brief",pdfBriefPath,o4,"--metres-per-unit","0.05");
        Check(Ok(r4,"cad-brief",o4) && Doc(o4) is {} d4 && d4.GetProperty("source").GetProperty("metres_per_unit").GetDouble()==0.05 && d4.GetProperty("rooms").GetRawText()==JsonSerializer.Serialize(libMpu.Rooms),"cad-brief pdf --metres-per-unit equals the library",R(r4));
        ExpectCad("cad-brief pdf missing page is cad_page_missing","cad_page_missing","cad-brief",pdfBriefPath,Out(),"--page","3");
    }
    // Option, input and output refusals: one failed envelope, exit 1, no output file.
    ExpectCad("cad-draft unknown option","cad_option_unknown","cad-draft",dxfDraftPath,Out(),"--bogus","1");
    ExpectCad("cad-draft option without value","cad_option_value_missing","cad-draft",dxfDraftPath,Out(),"--metres-per-unit");
    ExpectCad("cad-draft duplicate option","cad_option_duplicate","cad-draft",dxfDraftPath,Out(),"--metres-per-unit","1","--metres-per-unit","1");
    foreach(var bad in new[]{"0","-1","NaN","Infinity","abc","1e400","1,5"}) ExpectCad("cad-draft --metres-per-unit "+bad+" refused","cad_option_value_invalid","cad-draft",dxfDraftPath,Out(),"--metres-per-unit",bad);
    foreach(var bad in new[]{"0","-1","x","1.5","99999999999"}) ExpectCad("cad-draft --page "+bad+" refused","cad_option_value_invalid","cad-draft",pdfDraftPath,Out(),"--page",bad,"--metres-per-unit","0.02");
    ExpectCad("cad-draft bad --project-id refused","cad_option_value_invalid","cad-draft",dxfDraftPath,Out(),"--project-id","not-a-guid");
    // The empty GUID is refused while parsing options, before any output check or input read (missing input, fresh output).
    ExpectCad("cad-draft empty --project-id refused as an option value","cad_option_value_invalid","cad-draft",Path.Combine(work,"missing.dxf"),Out(),"--project-id",Guid.Empty.ToString());
    ExpectCad("cad-draft --pdf-window-forms on a DXF refused","cad_option_not_applicable","cad-draft",dxfDraftPath,Out(),"--pdf-window-forms","5");
    foreach(var bad in new[]{"0,0,10,0,Infinity","NaN,0,10,0,1000","0,0,1e400,0,1000"}) ExpectCad("cad-draft --calibrate "+bad+" refused","cad_option_value_invalid","cad-draft",pdfDraftPath,Out(),"--calibrate",bad);
    ExpectCad("cad-brief --metres-per-unit 1001 refused","cad_option_value_invalid","cad-brief",pdfBriefPath,Out(),"--metres-per-unit","1001");
    {
        var o=Out(); var r=Cli("cad-brief",pdfBriefPath,o,"--metres-per-unit","1000");
        Check(Ok(r,"cad-brief",o) && Doc(o) is {} d && d.GetProperty("source").GetProperty("metres_per_unit").GetDouble()==1000,"cad-brief --metres-per-unit 1000 (the upper bound) accepted",R(r));
        // Byte-identical reruns for a PDF draft and for briefs (DXF and PDF).
        foreach(var run in new[]{new[]{"cad-draft",pdfDraftPath,"--calibrate","100,100,400,100,6000","--pdf-door-forms","5"},new[]{"cad-brief",dxfBriefPath},new[]{"cad-brief",pdfBriefPath,"--calibrate","0,0,10,0,1000"}}) {
            var first=Out(); var second=Out();
            var rFirst=Cli([run[0],run[1],first,..run[2..]]); var rSecond=Cli([run[0],run[1],second,..run[2..]]);
            Check(Ok(rFirst,run[0],first) && Ok(rSecond,run[0],second) && File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),run[0]+" "+Path.GetFileName(run[1])+" rerun is byte-identical",R(rFirst)+" | "+R(rSecond));
        }
    }
    ExpectCad("cad-draft --calibrate with four numbers refused","cad_option_value_invalid","cad-draft",pdfDraftPath,Out(),"--calibrate","1,2,3,4");
    ExpectCad("cad-draft --calibrate and --metres-per-unit together refused","cad_scale_conflict","cad-draft",pdfDraftPath,Out(),"--calibrate","100,100,400,100,6000","--metres-per-unit","0.02");
    ExpectCad("cad-draft --page on a DXF refused","cad_option_not_applicable","cad-draft",dxfDraftPath,Out(),"--page","1");
    ExpectCad("cad-draft --calibrate on a DXF refused","cad_option_not_applicable","cad-draft",dxfDraftPath,Out(),"--calibrate","0,0,1,0,1");
    ExpectCad("cad-brief --pdf-door-forms refused","cad_option_unknown","cad-brief",pdfBriefPath,Out(),"--pdf-door-forms","5");
    ExpectCad("cad-brief --project-id refused","cad_option_unknown","cad-brief",dxfBriefPath,Out(),"--project-id",cadId.ToString());
    ExpectCad("cad-draft unsupported input extension","cad_input_format_unsupported","cad-draft",Input("plan.txt","0\nEOF\n"),Out());
    ExpectCad("cad-draft missing input","input_not_found","cad-draft",Path.Combine(work,"missing.dxf"),Out());
    ExpectCad("cad-brief directory input","input_not_regular_file","cad-brief",Directory.CreateDirectory(Path.Combine(work,"dir.dxf")).FullName,Out());
    var cadBig=Path.Combine(work,"big.dxf");
    using(var big=new FileStream(cadBig,FileMode.CreateNew)) { big.SetLength(32L*1024*1024+1); }
    ExpectCad("cad-draft input over 32 MiB is input_too_large","input_too_large","cad-draft",cadBig,Out());
    File.Delete(cadBig);
    var noUnits=Input("no-units.dxf",File.ReadAllText(dxfDraftPath).Replace("$INSUNITS","$NOTUNITS",StringComparison.Ordinal));
    Check(File.ReadAllText(noUnits)!=File.ReadAllText(dxfDraftPath),"no-units DXF fixture differs from draft-plan.dxf");
    ExpectCad("cad-draft DXF without units is cad_uncalibrated","cad_uncalibrated","cad-draft",noUnits,Out());
    {
        var r=Cli("cad-draft",Input("garbage.dxf","not a dxf\n"),Out());
        Check(r.Exit==1 && GEnvelope(r.Out) is {} e && e.GetProperty("status").GetString()=="failed" && e.GetProperty("code").GetString()!.StartsWith("dxf_",StringComparison.Ordinal) && e.GetProperty("detail").GetString()!.Length>0,"cad-draft malformed DXF is a dxf_* refusal with detail",R(r));
        var r2=Cli("cad-brief",Input("garbage.pdf","not a pdf"),Out());
        Check(r2.Exit==1 && GEnvelope(r2.Out) is {} e2 && e2.GetProperty("code").GetString()=="pdf_not_pdf" && e2.GetProperty("detail").GetString()!.Length>0,"cad-brief non-PDF bytes are pdf_not_pdf with detail",R(r2));
    }
    {
        var existing=Out(); File.WriteAllBytes(existing,[9,9]);
        ExpectCad("cad-draft refuses an existing output","output_exists","cad-draft",dxfDraftPath,existing);
        ExpectCad("cad-brief refuses an existing output","output_exists","cad-brief",dxfBriefPath,existing);
        Check(File.ReadAllBytes(existing).SequenceEqual(new byte[]{9,9}),"cad verbs leave an existing output untouched");
        ExpectCad("cad-draft refuses a non-.json output","output_extension_unsupported","cad-draft",dxfDraftPath,Out(".glb"));
        ExpectCad("cad-brief refuses an output in a missing directory","output_directory_not_found","cad-brief",dxfBriefPath,Path.Combine(work,"no-dir","b.json"));
        ExpectCad("cad-draft refuses a ':' output","output_path_invalid","cad-draft",dxfDraftPath,"ab:c.json");
        if(linked) {
            var cadLink=Path.Combine(cadOut,"dangling.json"); File.CreateSymbolicLink(cadLink,Path.Combine(cadOut,"nowhere.json"));
            ExpectCad("cad-draft refuses a dangling symlink output","output_link_rejected","cad-draft",dxfDraftPath,cadLink);
            Check(!File.Exists(Path.Combine(cadOut,"nowhere.json")),"cad dangling symlink output target is not created");
        }
        // Output is checked before the input is read: a bad output wins over a missing input.
        ExpectCad("cad-draft checks the output before reading the input","output_exists","cad-draft",Path.Combine(work,"missing.dxf"),existing);
    }
    foreach(var verb in new[]{"cad-draft","cad-brief"}) {
        var r=Cli(verb,dxfDraftPath);
        Check(r.Exit==1 && GEnvelope(r.Out) is {} e && e.GetProperty("status").GetString()=="failed" && e.GetProperty("code").GetString()!.StartsWith("usage: "+verb,StringComparison.Ordinal),verb+" with a missing argument prints its usage",R(r));
    }
    var cadUsage=Cli("no-such-verb");
    Check(cadUsage.Out.Contains("cad-draft input.dxf|input.pdf output.json",StringComparison.Ordinal) && cadUsage.Out.Contains("cad-brief input.dxf|input.pdf output.json",StringComparison.Ordinal),"default usage lists cad-draft and cad-brief",cadUsage.Out);
    var cadSource=File.ReadAllText(Path.Combine(root,"interfaces","cli","CadCommand.cs"));
    Check(cadSource.Contains("BoundedInput.ReadBytes",StringComparison.Ordinal) && !cadSource.Contains("File.ReadAll",StringComparison.Ordinal) && !cadSource.Contains("ReadFile(",StringComparison.Ordinal) && !cadSource.Contains("File.OpenRead",StringComparison.Ordinal),"cad verbs read their input only through BoundedInput");

    // ---- P2-08 mirror-drift: a query.snapshot file and the Core store to the P2-04 drift report plus the P2-07 resolution ----
    // Oracle: the report and resolution subtrees are byte-equal to MirrorDrift.Compare / MirrorDriftResolve.Plan called
    // directly on the same inputs; nothing is applied (the store is byte-identical afterwards) and the output is never
    // overwritten. Fixture: tests/mirror-journal/fixtures/drift (committed model at revision 3 plus its query.snapshot).
    {
        var driftDir=Path.Combine(root,"tests","mirror-journal","fixtures","drift");
        var committedText=File.ReadAllText(Path.Combine(driftDir,"committed.json"));
        var snapshotText=File.ReadAllText(Path.Combine(driftDir,"snapshot.json"));
        var committed=MirrorCommittedModel.Parse(committedText);
        const string driftDoc="60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
        const string otherDoc="11111111-2222-3333-4444-555555555555-00000001";
        const string movedWall="db98eeb5-3a66-7972-29a5-494ec813984a";
        const string deletedDoor="5f83f664-4e98-113a-3e1b-38f4a4fe7143";
        var driftStore=Path.Combine(work,"drift-store");
        new ProjectStore(driftStore).Initialize(committed.Project);
        var storeFile=Path.Combine(driftStore,"project.json");
        var storeBytes=File.ReadAllBytes(storeFile);
        string[] StoreListing()=>Directory.GetFileSystemEntries(driftStore).Select(entry=>Path.GetFileName(entry)).Order(StringComparer.Ordinal).ToArray();
        var storeListing=StoreListing();
        // Drifted snapshot: the second wall moved 50 mm in x, the door deleted in Revit.
        var drifted=System.Text.Json.Nodes.JsonNode.Parse(snapshotText)!.AsObject();
        foreach(var element in drifted["elements"]!.AsArray().ToArray())
            if((string?)element!["Id"]==movedWall) { element!["GeometryMetres"]![0]=5.05; element!["GeometryMetres"]![2]=5.05; }
        foreach(var element in drifted["elements"]!.AsArray().ToArray())
            if((string?)element!["Id"]==deletedDoor) drifted["elements"]!.AsArray().Remove(element);
        foreach(var source in drifted["sources"]!.AsArray().ToArray())
            if((string?)source!["id"]==deletedDoor) drifted["sources"]!.AsArray().Remove(source);
        var driftedText=drifted.ToJsonString();
        var snapshotPath=Input("drift-snapshot.json",driftedText);
        System.Text.Json.Nodes.JsonObject Mirror(string document=driftDoc,int revision=3,string? projectId=null) {
            var map=System.Text.Json.Nodes.JsonNode.Parse(committedText)!["map"]!.DeepClone();
            return new System.Text.Json.Nodes.JsonObject {
                ["document"]=new System.Text.Json.Nodes.JsonObject{["path"]=@"C:\Repo\.bim\native-runs\p2-08\copy.rvt",["unique_id"]=document},
                ["core_project_id"]=projectId ?? committed.Project.Id.ToString("D"),
                ["core_revision"]=revision,
                ["map"]=map,
                ["levels"]=new System.Text.Json.Nodes.JsonObject(),
                ["wall_types"]=new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject{["thickness_metres"]=0.2,["type"]="Generic - 200mm"}),
                ["door_symbol"]="Single-Flush: 0915 x 2134mm",
                ["window_symbol"]="Fixed: 1000 x 1200mm",
            };
        }
        var mirrorPath=Input("drift-mirror.json",Mirror().ToJsonString());
        var driftOut=Path.Combine(work,"drift-out"); Directory.CreateDirectory(driftOut);
        var driftN=0;
        string DriftOut(string ext=".json")=>Path.Combine(driftOut,"drift-"+(++driftN).ToString(System.Globalization.CultureInfo.InvariantCulture)+ext);
        string DR((int Exit,string Out,string Err) r)=>"exit "+r.Exit+" "+r.Out+(r.Err.Length>0?" stderr "+r.Err:"");
        JsonElement? DEnvelope(string output) { try { return JsonDocument.Parse(output).RootElement; } catch(JsonException) { return null; } }
        bool Refused((int Exit,string Out,string Err) r,string code)=>r.Exit==1 && DEnvelope(r.Out) is {} e && e.GetProperty("status").GetString()=="failed" && e.GetProperty("code").GetString()==code;
        void ExpectDrift(string name,string code,params string[] arguments) {
            var r=Cli(arguments);
            Check(Refused(r,code),name,DR(r));
        }

        // Expected, straight from Core on the same inputs.
        var bindings=new RevitBindings(new RevitDocumentRef(@"C:\Repo\.bim\native-runs\p2-08\copy.rvt",driftDoc),new Dictionary<Guid,string>(),new[]{new RevitWallTypeBinding(0.2,"Generic - 200mm")},"Single-Flush: 0915 x 2134mm","Fixed: 1000 x 1200mm");
        var parsedSnapshot=MirrorDrift.ParseSnapshot(driftedText);
        var expectedReport=MirrorDrift.Compare(parsedSnapshot,committed,(IReadOnlyList<MirrorJournalEntry>?)null,new MirrorDriftOptions());
        var expectedResolution=MirrorDriftResolve.Plan(expectedReport,parsedSnapshot,committed,bindings);
        Check(expectedReport.Summary.Drift==2 && expectedResolution.Proposals.Count==2 && expectedResolution.RestorePlan.Steps.Count>0,"mirror-drift fixture has a changed wall and a missing door",expectedReport.ToJson());

        var first=DriftOut();
        var ok=Cli("mirror-drift",driftStore,snapshotPath,mirrorPath,first);
        var okEnv=DEnvelope(ok.Out);
        Check(ok.Exit==0 && okEnv is {} s && s.GetProperty("status").GetString()=="succeeded" && s.GetProperty("output").GetString()==first && s.GetProperty("applied").GetBoolean()==false
            && s.GetProperty("drift").GetInt32()==2 && s.GetProperty("proposals").GetInt32()==2 && File.Exists(first)
            && s.GetProperty("bytes").GetInt64()==new FileInfo(first).Length && s.GetProperty("sha256").GetString()==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(first))),
            "mirror-drift writes the report and resolution to a new file and prints a succeeded summary",DR(ok));
        if(File.Exists(first)) {
            var written=File.ReadAllBytes(first);
            Check(written.Length>0 && written[0]==(byte)'{' && !(written.Length>=3 && written[0]==0xEF && written[1]==0xBB && written[2]==0xBF),"mirror-drift output is UTF-8 JSON without a BOM");
            using var doc=JsonDocument.Parse(written);
            var top=doc.RootElement;
            Check(top.GetProperty("mirror_drift_version").GetInt32()==1 && top.GetProperty("applied").GetBoolean()==false && top.GetProperty("requires_approval").GetBoolean()==true,
                "mirror-drift document is version 1, never applied, requires approval",Encoding.UTF8.GetString(written));
            Check(top.GetProperty("core_store").GetProperty("project_id").GetString()==committed.Project.Id.ToString("D") && top.GetProperty("core_store").GetProperty("revision").GetInt32()==3
                && top.GetProperty("core_store").GetProperty("hash").GetString()==Compiler.Hash(committed.Project),"mirror-drift document names the Core store state it was computed against");
            Check(top.GetProperty("report").GetRawText()==expectedReport.ToJson(),"mirror-drift report is byte-equal to MirrorDrift.Compare",top.GetProperty("report").GetRawText());
            Check(top.GetProperty("resolution").GetRawText()==expectedResolution.ToJson(),"mirror-drift resolution is byte-equal to MirrorDriftResolve.Plan",top.GetProperty("resolution").GetRawText());
            Check(top.GetProperty("resolution").GetProperty("applied").GetBoolean()==false && top.GetProperty("resolution").GetProperty("restore_plan").GetProperty("steps").GetArrayLength()==expectedResolution.RestorePlan.Steps.Count,
                "mirror-drift resolution carries the restore plan, not applied");
            var again=DriftOut();
            var rerun=Cli("mirror-drift",driftStore,snapshotPath,mirrorPath,again);
            Check(rerun.Exit==0 && File.Exists(again) && File.ReadAllBytes(again).SequenceEqual(written),"mirror-drift rerun is byte-identical",DR(rerun));
        }
        Check(File.ReadAllBytes(storeFile).SequenceEqual(storeBytes) && StoreListing().SequenceEqual(storeListing),"mirror-drift applies nothing: the Core store is byte-identical and gains no file");

        // A catalogue query.snapshot response ({"status":"succeeded",...,"readback":<snapshot>}) is unwrapped.
        var wrapped=new RevitCommandResponse(RevitCommandResponse.Succeeded,RevitCommandCodes.Ok,null,null,[],[],[],driftedText,new RevitCommandReceipt("op-snap-1","query.snapshot",driftDoc,new string('a',64),false),[]).ToJson();
        var wrappedOut=DriftOut();
        var wrappedRun=Cli("mirror-drift",driftStore,Input("drift-snapshot-response.json",wrapped),mirrorPath,wrappedOut);
        Check(wrappedRun.Exit==0 && File.Exists(first) && File.Exists(wrappedOut) && File.ReadAllBytes(wrappedOut).SequenceEqual(File.ReadAllBytes(first)),"mirror-drift accepts a query.snapshot catalogue response and gives the same document",DR(wrappedRun));
        var failedResponse=RevitCommandResponse.Failure("document_not_open").ToJson();
        ExpectDrift("mirror-drift refuses a failed query.snapshot response","drift_snapshot_invalid","mirror-drift",driftStore,Input("drift-snapshot-failed.json",failedResponse),mirrorPath,DriftOut());

        // A clean snapshot: no drift, no proposals, still a document.
        var cleanOut=DriftOut();
        var clean=Cli("mirror-drift",driftStore,Input("drift-clean.json",snapshotText),mirrorPath,cleanOut);
        Check(clean.Exit==0 && DEnvelope(clean.Out) is {} c && c.GetProperty("drift").GetInt32()==0 && c.GetProperty("proposals").GetInt32()==0 && File.Exists(cleanOut),"mirror-drift on an unchanged snapshot reports no drift",DR(clean));

        // Never overwrite; output checked before any input is read.
        var existing=DriftOut(); File.WriteAllBytes(existing,[7,7]);
        ExpectDrift("mirror-drift refuses an existing output","output_exists","mirror-drift",driftStore,snapshotPath,mirrorPath,existing);
        Check(File.ReadAllBytes(existing).SequenceEqual(new byte[]{7,7}),"mirror-drift leaves an existing output untouched");
        ExpectDrift("mirror-drift checks the output before reading the inputs","output_exists","mirror-drift",driftStore,Path.Combine(work,"missing-snapshot.json"),mirrorPath,existing);
        ExpectDrift("mirror-drift refuses a non-.json output","output_extension_unsupported","mirror-drift",driftStore,snapshotPath,mirrorPath,DriftOut(".glb"));
        ExpectDrift("mirror-drift refuses an output in a missing directory","output_directory_not_found","mirror-drift",driftStore,snapshotPath,mirrorPath,Path.Combine(work,"no-such-dir","d.json"));
        var dirOutput=Path.Combine(driftOut,"dir.json"); Directory.CreateDirectory(dirOutput);
        ExpectDrift("mirror-drift refuses an existing directory as output","output_exists","mirror-drift",driftStore,snapshotPath,mirrorPath,dirOutput);
        if(linked) {
            var driftLink=Path.Combine(driftOut,"dangling.json"); File.CreateSymbolicLink(driftLink,Path.Combine(driftOut,"nowhere.json"));
            ExpectDrift("mirror-drift refuses a dangling symlink output","output_link_rejected","mirror-drift",driftStore,snapshotPath,mirrorPath,driftLink);
            Check(!File.Exists(Path.Combine(driftOut,"nowhere.json")),"mirror-drift dangling symlink output target is not created");
        }

        // Typed refusals write nothing.
        var staleOut=DriftOut();
        ExpectDrift("mirror-drift refuses a mirror recorded at another Core revision (stale)","drift_resolve_report_stale","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-stale.json",Mirror(revision:2).ToJsonString()),staleOut);
        Check(!File.Exists(staleOut),"a stale refusal writes no output");
        ExpectDrift("mirror-drift refuses a mirror for another Core project (stale)","drift_resolve_report_stale","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-project.json",Mirror(projectId:Guid.Empty.ToString("D")).ToJsonString()),DriftOut());
        var mismatchOut=DriftOut();
        ExpectDrift("mirror-drift refuses a mirror for another document","drift_document_mismatch","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-other.json",Mirror(document:otherDoc).ToJsonString()),mismatchOut);
        Check(!File.Exists(mismatchOut),"a document mismatch writes no output");
        var badPath=Mirror(); badPath["document"]!["path"]="relative.rvt";
        ExpectDrift("mirror-drift refuses a relative document path","document_path_invalid","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-path.json",badPath.ToJsonString()),DriftOut());
        var unknownMember=Mirror(); unknownMember["apply"]=true;
        ExpectDrift("mirror-drift refuses an unknown mirror member","mirror_drift_request_invalid","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-unknown.json",unknownMember.ToJsonString()),DriftOut());
        var noRevision=Mirror(); noRevision.Remove("core_revision");
        ExpectDrift("mirror-drift refuses a mirror without core_revision","mirror_drift_request_invalid","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-norev.json",noRevision.ToJsonString()),DriftOut());
        ExpectDrift("mirror-drift refuses a mirror that is not JSON","mirror_drift_request_invalid","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-garbage.json","{not json"),DriftOut());
        var badOptions=Mirror(); badOptions["options"]=new System.Text.Json.Nodes.JsonObject{["max_findings"]=0};
        ExpectDrift("mirror-drift refuses invalid options","drift_options_invalid","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-options.json",badOptions.ToJsonString()),DriftOut());
        var truncatedMirror=Mirror(); truncatedMirror["options"]=new System.Text.Json.Nodes.JsonObject{["max_findings"]=1};
        ExpectDrift("mirror-drift refuses a truncated report","drift_resolve_report_truncated","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-truncated.json",truncatedMirror.ToJsonString()),DriftOut());
        ExpectDrift("mirror-drift refuses a snapshot that is not a query.snapshot","drift_snapshot_invalid","mirror-drift",driftStore,Input("drift-snapshot-bad.json","{\"snapshot_version\":2}"),mirrorPath,DriftOut());

        // Bounded inputs: every file goes through BoundedInput, the store included.
        var bigSnapshot=new byte[BoundedInput.MaxBytes+1]; Array.Fill(bigSnapshot,(byte)' ');
        ExpectDrift("mirror-drift snapshot over 8 MiB is input_too_large","input_too_large","mirror-drift",driftStore,Bytes("drift-big.json",bigSnapshot),mirrorPath,DriftOut());
        ExpectDrift("mirror-drift directory as mirror is input_not_regular_file","input_not_regular_file","mirror-drift",driftStore,snapshotPath,driftOut,DriftOut());
        ExpectDrift("mirror-drift missing snapshot is input_not_found","input_not_found","mirror-drift",driftStore,Path.Combine(work,"missing-snapshot.json"),mirrorPath,DriftOut());
        var emptyStore=Path.Combine(work,"drift-empty-store"); Directory.CreateDirectory(emptyStore);
        ExpectDrift("mirror-drift on an uninitialized store is input_not_found","input_not_found","mirror-drift",emptyStore,snapshotPath,mirrorPath,DriftOut());
        Check(Directory.GetFileSystemEntries(emptyStore).Length==0,"mirror-drift never initializes a store");
        var garbageStore=Path.Combine(work,"drift-garbage-store"); Directory.CreateDirectory(garbageStore); File.WriteAllText(Path.Combine(garbageStore,"project.json"),"{\"Receipts\":{}}");
        ExpectDrift("mirror-drift on a store without a project is drift_store_invalid","drift_store_invalid","mirror-drift",garbageStore,snapshotPath,mirrorPath,DriftOut());
        var manyUnmapped=new System.Text.Json.Nodes.JsonArray();
        for(var i=0;i<=MirrorSnapshotMapper.MaxElements;i++) manyUnmapped.Add(new System.Text.Json.Nodes.JsonObject{["category"]="floor",["unique_id"]="u"+i.ToString(System.Globalization.CultureInfo.InvariantCulture),["reason"]="no_bim_ir_kind",["detail"]=""});
        var tooMany=System.Text.Json.Nodes.JsonNode.Parse(driftedText)!.AsObject(); tooMany["unmapped"]=manyUnmapped;
        ExpectDrift("mirror-drift snapshot over the element bound is drift_input_too_large","drift_input_too_large","mirror-drift",driftStore,Input("drift-too-many.json",tooMany.ToJsonString()),mirrorPath,DriftOut());

        // ---- Review fixes ----
        // 1. Never write inside the store (a store/*-drift.json would later look like a cloud conflict copy of project.json).
        ExpectDrift("mirror-drift refuses an output inside the store directory","output_in_store","mirror-drift",driftStore,snapshotPath,mirrorPath,Path.Combine(driftStore,"project-drift.json"));
        ExpectDrift("mirror-drift refuses an output inside the store given with a trailing separator","output_in_store","mirror-drift",driftStore+Path.DirectorySeparatorChar,snapshotPath,mirrorPath,Path.Combine(driftStore,"drift.json"));
        Check(StoreListing().SequenceEqual(storeListing) && File.ReadAllBytes(storeFile).SequenceEqual(storeBytes),"an output_in_store refusal leaves the store listing and bytes unchanged",string.Join(",",StoreListing()));
        // 2. A malformed store is drift_store_invalid, never a runtime exception message.
        string MalformedStore(string name,string projectJson) { var dir=Path.Combine(work,name); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir,"project.json"),"{\"Project\":"+projectJson+",\"Receipts\":{}}"); return dir; }
        var projectNode=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(committed.Project))!.AsObject();
        var nullGeometry=projectNode.DeepClone().AsObject(); nullGeometry["Elements"]![1]!["GeometryMetres"]=null;
        var nullDependencies=projectNode.DeepClone().AsObject(); nullDependencies["Elements"]![1]!["Dependencies"]=null;
        var nullElement=projectNode.DeepClone().AsObject(); nullElement["Elements"]![1]=null;
        var noId=projectNode.DeepClone().AsObject(); noId.Remove("Id");
        var noRevisionStore=projectNode.DeepClone().AsObject(); noRevisionStore.Remove("Revision");
        var noElementId=projectNode.DeepClone().AsObject(); noElementId["Elements"]![1]!.AsObject().Remove("Id");
        foreach(var (label,node) in new[]{("null GeometryMetres",nullGeometry),("null Dependencies",nullDependencies),("null element",nullElement),("missing project Id",noId),("missing Revision",noRevisionStore),("missing element Id",noElementId)}) {
            var r=Cli("mirror-drift",MalformedStore("drift-malformed-"+label.Replace(' ','-'),node.ToJsonString()),snapshotPath,mirrorPath,DriftOut());
            Check(Refused(r,"drift_store_invalid"),"mirror-drift refuses a store with "+label+" as drift_store_invalid",DR(r));
        }
        // 3. Duplicate members in the mirror request are refused (root, document, options).
        var mirrorText=Mirror().ToJsonString();
        ExpectDrift("mirror-drift refuses a duplicate core_revision","mirror_drift_request_invalid","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-dup.json","{\"core_revision\":2,"+mirrorText[1..]),DriftOut());
        ExpectDrift("mirror-drift refuses a duplicate document.unique_id","mirror_drift_request_invalid","mirror-drift",driftStore,snapshotPath,
            Input("drift-mirror-dup-doc.json",mirrorText.Replace("\"unique_id\":\""+driftDoc+"\"","\"unique_id\":\""+driftDoc+"\",\"unique_id\":\""+driftDoc+"\"",StringComparison.Ordinal)),DriftOut());
        var dupOptions=Mirror(); dupOptions["options"]=new System.Text.Json.Nodes.JsonObject{["max_findings"]=10};
        ExpectDrift("mirror-drift refuses a duplicate options member","mirror_drift_request_invalid","mirror-drift",driftStore,snapshotPath,
            Input("drift-mirror-dup-options.json",dupOptions.ToJsonString().Replace("\"max_findings\":10","\"max_findings\":10,\"max_findings\":20000",StringComparison.Ordinal)),DriftOut());
        // 4. A truncated report names the option to raise.
        {
            var r=Cli("mirror-drift",driftStore,snapshotPath,Input("drift-mirror-truncated-detail.json",truncatedMirror.ToJsonString()),DriftOut());
            Check(r.Exit==1 && r.Out==JsonSerializer.Serialize(new {status="failed",code="drift_resolve_report_truncated",detail="the report holds more than options.max_findings = 1 findings; raise options.max_findings (at most 20000) to resolve it"}),
                "mirror-drift truncation refusal tells the caller to raise options.max_findings",DR(r));
        }
        // 5. Journal evidence, levels keys, store links.
        var journalText=File.ReadAllText(Path.Combine(driftDir,"journal.json"));
        var withJournal=Mirror(); withJournal["journal"]=System.Text.Json.Nodes.JsonNode.Parse(journalText); withJournal["options"]=new System.Text.Json.Nodes.JsonObject{["journal_after_sequence"]=4};
        var journalOut=DriftOut();
        var journalRun=Cli("mirror-drift",driftStore,snapshotPath,Input("drift-mirror-journal.json",withJournal.ToJsonString()),journalOut);
        var journalReport=MirrorDrift.Compare(parsedSnapshot,committed,MirrorDrift.ParseJournal(journalText),new MirrorDriftOptions(JournalAfterSequence:4));
        var journalResolution=MirrorDriftResolve.Plan(journalReport,parsedSnapshot,committed,bindings);
        Check(journalReport.Summary.ExternalEdit>0,"the journal fixture classifies a drifted element external_edit",journalReport.ToJson());
        Check(journalRun.Exit==0 && File.Exists(journalOut),"mirror-drift accepts mirror.journal",DR(journalRun));
        if(File.Exists(journalOut)) {
            using var journalDoc=JsonDocument.Parse(File.ReadAllBytes(journalOut));
            Check(journalDoc.RootElement.GetProperty("report").GetRawText()==journalReport.ToJson() && journalDoc.RootElement.GetProperty("resolution").GetRawText()==journalResolution.ToJson(),
                "mirror-drift with a journal is byte-equal to MirrorDrift.Compare(ParseJournal) and Plan",journalDoc.RootElement.GetProperty("report").GetRawText());
        }
        var badLevels=Mirror(); badLevels["levels"]=new System.Text.Json.Nodes.JsonObject{["Level 1"]="Level 1"};
        ExpectDrift("mirror-drift refuses a levels key that is not a BIM-IR id","mirror_drift_request_invalid","mirror-drift",driftStore,snapshotPath,Input("drift-mirror-levels.json",badLevels.ToJsonString()),DriftOut());
        if(linked) {
            var linkStore=Path.Combine(work,"drift-link-store"); Directory.CreateDirectory(linkStore);
            File.CreateSymbolicLink(Path.Combine(linkStore,"project.json"),storeFile);
            var r=Cli("mirror-drift",linkStore,snapshotPath,mirrorPath,DriftOut());
            Check(r.Exit==1 && r.Out==JsonSerializer.Serialize(new {status="failed",code="input_link_rejected",detail="store"}),"mirror-drift refuses a store project.json symlink (input_link_rejected, detail store)",DR(r));
        }
        // 6. The store file is read with FileShare.ReadWrite|Delete: a writer holding project.json open (as a commit
        // replacing it would) does not make the read fail with a sharing violation.
        {
            var heldOut=DriftOut();
            (int Exit,string Out,string Err) held;
            using(var holder=new FileStream(storeFile,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete)) held=Cli("mirror-drift",driftStore,snapshotPath,mirrorPath,heldOut);
            Check(held.Exit==0 && File.Exists(heldOut),"mirror-drift reads a store whose project.json is held open for writing",DR(held));
        }
        // Nit: an empty input path is a typed input refusal.
        {
            var r=Cli("mirror-drift",driftStore,"",mirrorPath,DriftOut());
            Check(r.Exit==1 && r.Out==JsonSerializer.Serialize(new {status="failed",code="input_path_invalid",detail="snapshot"}),"mirror-drift empty snapshot path is input_path_invalid",DR(r));
            var r2=Cli("mirror-drift",driftStore,snapshotPath,"",DriftOut());
            Check(r2.Exit==1 && r2.Out==JsonSerializer.Serialize(new {status="failed",code="input_path_invalid",detail="mirror"}),"mirror-drift empty mirror path is input_path_invalid",DR(r2));
        }

        // Usage and static guards.
        var driftUsage=Cli("mirror-drift",driftStore,snapshotPath);
        Check(driftUsage.Exit==1 && DEnvelope(driftUsage.Out) is {} u && u.GetProperty("status").GetString()=="failed" && u.GetProperty("code").GetString()!.StartsWith("usage: mirror-drift",StringComparison.Ordinal),"mirror-drift with missing arguments prints its usage",DR(driftUsage));
        var anyUsage=Cli("no-such-verb");
        Check(anyUsage.Out.Contains("mirror-drift store-directory snapshot.json mirror.json output.json",StringComparison.Ordinal),"default usage lists mirror-drift",anyUsage.Out);
        var commandPath=Path.Combine(root,"interfaces","cli","MirrorDriftCommand.cs");
        var toolPath=Path.Combine(root,"interfaces","mcp","MirrorDriftTool.cs");
        Check(File.Exists(commandPath) && File.Exists(toolPath),"mirror-drift sources exist");
        if(File.Exists(commandPath) && File.Exists(toolPath)) {
            var driftSources=File.ReadAllText(commandPath)+File.ReadAllText(toolPath);
            Check(driftSources.Contains("BoundedInput.ReadText",StringComparison.Ordinal) && !driftSources.Contains("File.ReadAll",StringComparison.Ordinal) && !driftSources.Contains("File.OpenRead",StringComparison.Ordinal) && !driftSources.Contains("ProjectStore(",StringComparison.Ordinal),
                "mirror-drift reads every input, the store included, only through BoundedInput");
            Check(!driftSources.Contains(".Commit(",StringComparison.Ordinal) && !driftSources.Contains("PlanRunner",StringComparison.Ordinal) && !driftSources.Contains("RevitCommandService",StringComparison.Ordinal) && !driftSources.Contains("Initialize(",StringComparison.Ordinal),
                "mirror-drift never commits, runs a plan or talks to Revit");
        }
    }

    // ---- P5-09 skills-lock: pin, verify and update the P5-08 project skill lock (skills.lock.json) from the CLI ----
    // Oracle: pin writes exactly SkillLockFile.Serialize(SkillLocking.Pin(..)) to a new file and never overwrites one;
    // verify writes nothing and reports skill_lock_drift / skill_lock_missing per pack; update is the only rewrite and
    // its bytes are SkillLocking.Update's. Fixture packs are minimal manifests written below (LF bytes).
    {
        var lockRoot=Path.Combine(work,"skills-lock"); Directory.CreateDirectory(lockRoot);
        var skillsDir=Path.Combine(lockRoot,"skills");
        const string alpha="bim.test.alpha", beta="bim.test.beta";
        string Manifest(string id,string version)=>"{\"manifest_version\":1,\"id\":\""+id+"\",\"version\":\""+version+"\",\"kind\":\"qa\",\"description\":\"CLI lock pack\",\"priority\":10}\n";
        void Pack(string id,string version="1.0.0") { Directory.CreateDirectory(Path.Combine(skillsDir,id)); File.WriteAllBytes(Path.Combine(skillsDir,id,"skill.json"),new UTF8Encoding(false).GetBytes(Manifest(id,version))); }
        Pack(alpha); Pack(beta);
        string LR((int Exit,string Out,string Err) r)=>"exit "+r.Exit+" "+r.Out+(r.Err.Length>0?" stderr "+r.Err:"");
        JsonElement? LEnvelope(string output) { try { return JsonDocument.Parse(output).RootElement.Clone(); } catch(JsonException) { return null; } }
        string? Str(JsonElement? e,string name)=>e is {} v && v.ValueKind==JsonValueKind.Object && v.TryGetProperty(name,out var p) && p.ValueKind==JsonValueKind.String ? p.GetString() : null;
        bool LRefused((int Exit,string Out,string Err) r,string code)=>r.Exit==1 && Str(LEnvelope(r.Out),"status")=="failed" && Str(LEnvelope(r.Out),"code")==code;
        string Hex(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        JsonElement PackState(JsonElement envelope,string id)=>envelope.GetProperty("packs").EnumerateArray().Single(p=>p.GetProperty("id").GetString()==id);
        var lockFile=Path.Combine(lockRoot,"skills.lock.json");

        // 1. pin writes the canonical lock to a new file.
        var expectedPin=SkillLockFile.Serialize(SkillLocking.Pin(skillsDir,new[]{alpha,beta}));
        var pin=Cli("skills-lock","pin",skillsDir,lockFile,beta,alpha);
        var pinEnv=LEnvelope(pin.Out);
        Check(pin.Exit==0 && Str(pinEnv,"status")=="succeeded" && Str(pinEnv,"mode")=="pin" && Str(pinEnv,"lock")==lockFile && File.Exists(lockFile),"skills-lock pin writes a new lock and prints a succeeded summary",LR(pin));
        var pinned=File.Exists(lockFile) ? File.ReadAllBytes(lockFile) : Array.Empty<byte>();
        Check(pinned.AsSpan().SequenceEqual(expectedPin),"skills-lock pin bytes equal SkillLockFile.Serialize(SkillLocking.Pin)",Encoding.UTF8.GetString(pinned));
        Check(pinEnv is {} pe && pe.TryGetProperty("bytes",out var pb) && pb.GetInt64()==expectedPin.LongLength && Str(pe,"sha256")==Hex(expectedPin)
            && pe.GetProperty("packs").GetArrayLength()==2 && pe.GetProperty("packs")[0].GetProperty("id").GetString()==alpha && pe.GetProperty("packs")[0].GetProperty("version").GetString()=="1.0.0"
            && pe.GetProperty("packs")[0].GetProperty("content_sha256").GetString()==SkillPackHash.Compute(skillsDir,alpha),"skills-lock pin summary lists bytes, sha256 and the pinned packs in id order",LR(pin));

        // 2. pin never overwrites.
        var pinAgain=Cli("skills-lock","pin",skillsDir,lockFile,alpha);
        Check(LRefused(pinAgain,"lock_exists") && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(expectedPin),"skills-lock pin refuses an existing lock (lock_exists) and leaves it byte-identical",LR(pinAgain));

        // 3. verify on an unchanged directory.
        var verify=Cli("skills-lock","verify",skillsDir,lockFile);
        var verifyEnv=LEnvelope(verify.Out);
        Check(verify.Exit==0 && Str(verifyEnv,"status")=="succeeded" && Str(verifyEnv,"mode")=="verify" && verifyEnv!.Value.GetProperty("verified").GetBoolean()
            && verifyEnv.Value.GetProperty("lock_version").GetInt32()==1 && Str(verifyEnv,"content_hash")=="bim-skill-pack-sha256-v1"
            && verifyEnv.Value.GetProperty("packs").GetArrayLength()==2 && verifyEnv.Value.GetProperty("packs").EnumerateArray().All(p=>p.GetProperty("state").GetString()=="ok")
            && verifyEnv.Value.GetProperty("issues").GetArrayLength()==0,"skills-lock verify reports every pinned pack ok",LR(verify));

        // 4. drift: beta gains a file; verify reports it per pack and writes nothing.
        File.WriteAllText(Path.Combine(skillsDir,beta,"notes.txt"),"edited");
        var drift=Cli("skills-lock","verify",skillsDir,lockFile);
        var driftEnv=LEnvelope(drift.Out);
        Check(LRefused(drift,"skill_lock_drift") && driftEnv is {} de && !de.GetProperty("verified").GetBoolean()
            && PackState(de,beta).GetProperty("state").GetString()=="skill_lock_drift" && PackState(de,beta).GetProperty("detail").GetString()!.Contains(SkillPackHash.Compute(skillsDir,beta),StringComparison.Ordinal)
            && PackState(de,alpha).GetProperty("state").GetString()=="ok"
            && de.GetProperty("issues").EnumerateArray().Any(i=>i.GetProperty("code").GetString()=="skill_lock_drift" && i.GetProperty("subject").GetString()==beta),
            "skills-lock verify reports skill_lock_drift for the edited pack only",LR(drift));
        Check(File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(expectedPin) && Directory.GetFiles(lockRoot).Length==1,"skills-lock verify never writes the lock or a temporary file");

        // 5. missing: alpha's directory is gone.
        var alphaAway=Path.Combine(lockRoot,"alpha-away");
        Directory.Move(Path.Combine(skillsDir,alpha),alphaAway);
        var gone=Cli("skills-lock","verify",skillsDir,lockFile);
        var goneEnv=LEnvelope(gone.Out);
        Check(LRefused(gone,"skill_lock_drift") && goneEnv is {} ge && PackState(ge,alpha).GetProperty("state").GetString()=="skill_lock_missing" && PackState(ge,beta).GetProperty("state").GetString()=="skill_lock_drift",
            "skills-lock verify reports skill_lock_missing and skill_lock_drift per pack (code: drift first)",LR(gone));
        var onlyMissingLock=Path.Combine(lockRoot,"only-alpha.lock.json");
        File.WriteAllBytes(onlyMissingLock,SkillLockFile.Serialize(new SkillLock(new[]{new SkillLockEntry(alpha,new SkillVersion(1,0,0),new string('0',64))})));
        var onlyMissing=Cli("skills-lock","verify",skillsDir,onlyMissingLock);
        Check(LRefused(onlyMissing,"skill_lock_missing") && LEnvelope(onlyMissing.Out) is {} om && PackState(om,alpha).GetProperty("state").GetString()=="skill_lock_missing","skills-lock verify with only a missing pack is skill_lock_missing",LR(onlyMissing));
        var updateMissing=Cli("skills-lock","update",skillsDir,lockFile);
        Check(LRefused(updateMissing,"skill_lock_missing") && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(expectedPin),"skills-lock update refuses a vanished pinned pack and leaves the lock unchanged",LR(updateMissing));
        Directory.Move(alphaAway,Path.Combine(skillsDir,alpha));

        // 6. update is the explicit rewrite.
        var update=Cli("skills-lock","update",skillsDir,lockFile);
        var updateEnv=LEnvelope(update.Out);
        var expectedUpdate=SkillLockFile.Serialize(SkillLocking.Pin(skillsDir,new[]{alpha,beta}));
        Check(update.Exit==0 && Str(updateEnv,"status")=="succeeded" && Str(updateEnv,"mode")=="update" && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(expectedUpdate)
            && updateEnv!.Value.GetProperty("changes").GetArrayLength()==1 && updateEnv.Value.GetProperty("changes")[0].GetProperty("id").GetString()==beta
            && updateEnv.Value.GetProperty("changes")[0].GetProperty("before").GetProperty("content_sha256").GetString()!=updateEnv.Value.GetProperty("changes")[0].GetProperty("after").GetProperty("content_sha256").GetString()
            && Str(updateEnv,"sha256")==Hex(expectedUpdate),"skills-lock update re-pins the drifted pack, lists the change and rewrites the lock",LR(update));
        var verifyAfter=Cli("skills-lock","verify",skillsDir,lockFile);
        Check(verifyAfter.Exit==0 && Str(LEnvelope(verifyAfter.Out),"status")=="succeeded","skills-lock verify succeeds after the update",LR(verifyAfter));
        var updateNoop=Cli("skills-lock","update",skillsDir,lockFile);
        Check(updateNoop.Exit==0 && LEnvelope(updateNoop.Out) is {} un && un.GetProperty("changes").GetArrayLength()==0 && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(expectedUpdate),"skills-lock update with nothing changed lists no change",LR(updateNoop));
        // Review finding 1: update with ids re-pins those ids and keeps every other pin (beta stays pinned).
        File.WriteAllText(Path.Combine(skillsDir,alpha,"notes.txt"),"alpha edited");
        File.WriteAllText(Path.Combine(skillsDir,beta,"notes.txt"),"beta edited again");
        var betaPinned=SkillLockFile.Read(lockFile).Find(beta);
        var narrow=Cli("skills-lock","update",skillsDir,lockFile,alpha);
        var narrowLock=File.Exists(lockFile) ? SkillLockFile.Read(lockFile) : null;
        Check(narrow.Exit==0 && LEnvelope(narrow.Out) is {} nw && nw.GetProperty("changes").GetArrayLength()==1 && nw.GetProperty("changes")[0].GetProperty("id").GetString()==alpha
            && narrowLock is not null && narrowLock.Packs.Count==2 && narrowLock.Find(beta)==betaPinned && narrowLock.Find(alpha)?.ContentSha256==SkillPackHash.Compute(skillsDir,alpha),
            "skills-lock update with pack ids re-pins those ids and keeps the other pins unchanged (beta still pinned, not re-pinned)",LR(narrow));
        var stillDrift=Cli("skills-lock","verify",skillsDir,lockFile);
        Check(LRefused(stillDrift,"skill_lock_drift") && LEnvelope(stillDrift.Out) is {} sd && PackState(sd,beta).GetProperty("state").GetString()=="skill_lock_drift" && PackState(sd,alpha).GetProperty("state").GetString()=="ok",
            "an update naming alpha does not silently accept beta's drift",LR(stillDrift));
        // Dropping a pin is explicit: skills-lock unpin.
        var unpin=Cli("skills-lock","unpin",skillsDir,lockFile,beta);
        Check(unpin.Exit==0 && Str(LEnvelope(unpin.Out),"mode")=="unpin" && LEnvelope(unpin.Out) is {} up && up.GetProperty("changes").GetArrayLength()==1 && up.GetProperty("changes")[0].GetProperty("id").GetString()==beta
            && up.GetProperty("changes")[0].GetProperty("after").ValueKind==JsonValueKind.Null && SkillLockFile.Read(lockFile).Packs.Select(e=>e.Id).SequenceEqual(new[]{alpha}),"skills-lock unpin drops exactly the named pin",LR(unpin));
        var unpinBytes=File.ReadAllBytes(lockFile);
        var unpinAgain=Cli("skills-lock","unpin",skillsDir,lockFile,beta);
        Check(LRefused(unpinAgain,"value_invalid") && Str(LEnvelope(unpinAgain.Out),"subject")==beta && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(unpinBytes),"skills-lock unpin of a pack that is not pinned is value_invalid and writes nothing",LR(unpinAgain));
        Check(LRefused(Cli("skills-lock","unpin",skillsDir,Path.Combine(lockRoot,"absent.lock.json"),alpha),"lock_missing") && !File.Exists(Path.Combine(lockRoot,"absent.lock.json")),"skills-lock unpin needs an existing lock");

        // 7. refusals: typed envelopes, nothing written.
        var noLock=Path.Combine(lockRoot,"none.lock.json");
        var missingLock=Cli("skills-lock","verify",skillsDir,noLock);
        Check(missingLock.Exit==1 && missingLock.Out=="""{"status":"failed","code":"lock_missing","subject":"none.lock.json","detail":"lock file not found","mode":"verify","verified":false,"packs":[],"issues":[{"code":"lock_missing","subject":"none.lock.json","detail":"lock file not found"}]}""","skills-lock verify of a missing lock is lock_missing with the uniform verify shape",LR(missingLock));
        Check(LRefused(Cli("skills-lock","update",skillsDir,noLock),"lock_missing") && !File.Exists(noLock),"skills-lock update without ids needs an existing lock");
        var unknown=Cli("skills-lock","pin",skillsDir,noLock,"bim.test.nope");
        Check(LRefused(unknown,"skill_missing") && Str(LEnvelope(unknown.Out),"subject")=="bim.test.nope" && !File.Exists(noLock),"skills-lock pin of an unknown pack is skill_missing and writes nothing",LR(unknown));
        var badId=Cli("skills-lock","pin",skillsDir,noLock,"Not An Id");
        Check(LRefused(badId,"skills_lock_request_invalid") && !File.Exists(noLock),"skills-lock pin refuses a malformed pack id before reading anything",LR(badId));
        var tooMany=Cli(new[]{"skills-lock","pin",skillsDir,noLock}.Concat(Enumerable.Range(0,SkillLock.MaxPacks+1).Select(i=>"bim.test.p"+i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        Check(LRefused(tooMany,"skills_lock_request_invalid") && !File.Exists(noLock),"skills-lock pin refuses more than SkillLock.MaxPacks pack ids",LR(tooMany));
        var emptyLockPath=Cli("skills-lock","verify",skillsDir,"");
        Check(emptyLockPath.Exit==1 && emptyLockPath.Out==JsonSerializer.Serialize(new {status="failed",code="skills_lock_path_invalid",detail="lock"}),"skills-lock empty lock path is skills_lock_path_invalid (detail lock)",LR(emptyLockPath));
        var emptySkills=Cli("skills-lock","verify","",lockFile);
        Check(emptySkills.Exit==1 && emptySkills.Out==JsonSerializer.Serialize(new {status="failed",code="skills_lock_path_invalid",detail="skills"}),"skills-lock empty skills path is skills_lock_path_invalid (detail skills)",LR(emptySkills));
        var longPath=Cli("skills-lock","verify",skillsDir,Path.Combine(lockRoot,new string('x',5000)));
        Check(longPath.Exit==1 && Str(LEnvelope(longPath.Out),"code")=="skills_lock_path_invalid","skills-lock refuses a lock path over the length bound",LR(longPath));
        var dirLock=Path.Combine(lockRoot,"dir.lock.json"); Directory.CreateDirectory(dirLock);
        Check(LRefused(Cli("skills-lock","pin",skillsDir,dirLock,alpha),"not_regular_file") && Directory.Exists(dirLock),"skills-lock pin refuses a directory as the lock path");
        Check(LRefused(Cli("skills-lock","verify",skillsDir,dirLock),"not_regular_file"),"skills-lock verify refuses a directory as the lock path");
        var noSkills=Cli("skills-lock","verify",Path.Combine(lockRoot,"no-skills"),lockFile);
        Check(LRefused(noSkills,"skills_invalid"),"skills-lock verify on a missing skills directory is skills_invalid",LR(noSkills));
        var garbage=Path.Combine(lockRoot,"garbage.lock.json"); File.WriteAllText(garbage,"{not json");
        Check(LRefused(Cli("skills-lock","verify",skillsDir,garbage),"json_invalid"),"skills-lock verify of a malformed lock is json_invalid");
        var linkLock=Path.Combine(lockRoot,"link.lock.json");
        var linkOk=false;
        try { File.CreateSymbolicLink(linkLock,lockFile); linkOk=true; } catch(Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if(linkOk) {
            Check(LRefused(Cli("skills-lock","verify",skillsDir,linkLock),"path_unsafe"),"skills-lock verify refuses a symlinked lock (path_unsafe)");
            var danglingLock=Path.Combine(lockRoot,"dangling.lock.json");
            File.CreateSymbolicLink(danglingLock,Path.Combine(lockRoot,"nowhere.json"));
            Check(LRefused(Cli("skills-lock","pin",skillsDir,danglingLock,alpha),"path_unsafe") && !File.Exists(Path.Combine(lockRoot,"nowhere.json")),"skills-lock pin refuses a dangling symlink at the lock path and writes nothing through it");
            var targetBytes=File.ReadAllBytes(lockFile);
            Check(LRefused(Cli("skills-lock","update",skillsDir,linkLock),"path_unsafe") && LRefused(Cli("skills-lock","update",skillsDir,linkLock,alpha),"path_unsafe")
                && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(targetBytes) && new FileInfo(linkLock).LinkTarget is not null,"skills-lock update over a symlinked lock is path_unsafe and leaves the link and its target unchanged");
        } else Skip("skills-lock symlink refusals","symbolic links unavailable");

        // FIFO at the lock path: update with ids must not repair (replace) it.
        if(!OperatingSystem.IsWindows()) {
            var fifoLock=Path.Combine(lockRoot,"fifo.lock.json");
            bool fifoMade;
            try { using var mk=Process.Start(new ProcessStartInfo("mkfifo"){ArgumentList={fifoLock},UseShellExecute=false}); fifoMade=mk!=null && mk.WaitForExit(10000) && mk.ExitCode==0; }
            catch(System.ComponentModel.Win32Exception) { fifoMade=false; }
            if(fifoMade) {
                var fifoUpdate=Cli("skills-lock","update",skillsDir,fifoLock,alpha);
                Check(LRefused(fifoUpdate,"not_regular_file") && File.Exists(fifoLock) && new FileInfo(fifoLock).Length==0 && !Directory.GetFiles(lockRoot,"*.tmp").Any(),"skills-lock update with ids over a FIFO is not_regular_file and leaves the FIFO in place",LR(fifoUpdate));
                Check(LRefused(Cli("skills-lock","unpin",skillsDir,fifoLock,alpha),"not_regular_file"),"skills-lock unpin over a FIFO is not_regular_file");
            } else Skip("skills-lock update over a FIFO","mkfifo unavailable");
        } else Skip("skills-lock update over a FIFO","Unix-only fixture");

        // Review finding 2: a lock created by another writer between the pre-check and the move is lock_exists; the other
        // writer's bytes stay and no temporary file is left (in-process seam SkillLockCommand.PinNew beforeWrite).
        {
            var raced=Path.Combine(lockRoot,"raced","skills.lock.json"); Directory.CreateDirectory(Path.GetDirectoryName(raced)!);
            var otherBytes=Encoding.UTF8.GetBytes("{\"other\":\"writer\"}\n");
            string? racedCode=null;
            try { SkillLockCommand.PinNew(skillsDir,raced,new[]{alpha},path=>File.WriteAllBytes(path,otherBytes)); }
            catch(SkillLockException e) { racedCode=e.Code; }
            Check(racedCode=="lock_exists" && File.ReadAllBytes(raced).AsSpan().SequenceEqual(otherBytes) && Directory.GetFiles(Path.GetDirectoryName(raced)!).Length==1,
                "a lock created concurrently before the move is lock_exists, the other writer's bytes are kept and no *.tmp is left","code "+racedCode);
            var calm=Path.Combine(lockRoot,"calm","skills.lock.json");
            var calmLock=SkillLockCommand.PinNew(skillsDir,calm,new[]{alpha},null);
            Check(File.ReadAllBytes(calm).AsSpan().SequenceEqual(SkillLockFile.Serialize(calmLock)) && Directory.GetFiles(Path.GetDirectoryName(calm)!).Length==1,"PinNew without a race writes the canonical lock and leaves no *.tmp");
        }

        // 8. usage.
        foreach(var bad in new[]{new[]{"skills-lock"},new[]{"skills-lock","verify",skillsDir},new[]{"skills-lock","verify",skillsDir,lockFile,alpha},new[]{"skills-lock","pin",skillsDir,noLock},new[]{"skills-lock","repin",skillsDir,lockFile},new[]{"skills-lock","unpin",skillsDir,lockFile}}) {
            var r=Cli(bad);
            Check(r.Exit==1 && Str(LEnvelope(r.Out),"code")=="skills_lock_usage" && (Str(LEnvelope(r.Out),"detail")??"").StartsWith("usage: skills-lock",StringComparison.Ordinal),"skills-lock usage for: "+string.Join(" ",bad.Skip(1).Select(a=>a==skillsDir?"<skills>":a==lockFile?"<lock>":a)),LR(r));
        }
        Check(Cli("no-such-verb").Out.Contains("skills-lock pin|verify|update",StringComparison.Ordinal),"default usage lists skills-lock");

        // 9. static: the MCP-shared engine is read-only.
        var lockToolPath=Path.Combine(root,"interfaces","mcp","SkillLockTool.cs");
        var lockCommandPath=Path.Combine(root,"interfaces","cli","SkillLockCommand.cs");
        Check(File.Exists(lockToolPath) && File.Exists(lockCommandPath),"skills-lock sources exist");
        if(File.Exists(lockToolPath)) {
            var toolSource=File.ReadAllText(lockToolPath);
            Check(toolSource.Contains("SkillLocking.Load(",StringComparison.Ordinal) && !toolSource.Contains("SkillLocking.Update",StringComparison.Ordinal) && !toolSource.Contains("SkillLocking.Pin",StringComparison.Ordinal)
                && new[]{"SkillLockFile.Write","File.Write","File.Move","File.Create","File.Copy","File.Delete","File.Open","FileMode.","FileStream","Directory.Create","Directory.Move","Directory.Delete"}.All(api=>!toolSource.Contains(api,StringComparison.Ordinal)),
                "the verify engine shared with MCP only loads (never pins, updates or writes)");
        }
        if(File.Exists(lockCommandPath)) {
            var commandSource=File.ReadAllText(lockCommandPath);
            Check(!commandSource.Contains("SkillLockFile.Write",StringComparison.Ordinal) && commandSource.Contains("SkillLocking.Update(",StringComparison.Ordinal) && commandSource.Contains("FileMode.CreateNew",StringComparison.Ordinal),
                "pin writes only through an exclusive create; SkillLocking.Update is the only rewrite");
        }
    }

    // ---- P3-07 diff-edits --project p --edits e: the P3-05 incremental diff (IncrementalDiffIndex) on the CLI ----
    // Oracle: stdout is byte-identical to JsonSerializer.Serialize(IncrementalDiff.Diff(project, edits).Plan) called
    // in-process, and to the `diff` verb's stdout on project vs the project with the edits applied (applied here by an
    // independent reimplementation of the P3-05 apply rule: updates in place, deletes removed, creates appended in
    // edit-set order). Refusals print exactly {"status":"failed","code":<code>} and exit 1, like every CLI refusal.
    // The project and edits files are never written: bytes and mtime (pinned to a past instant first) are unchanged
    // and no file appears beside them, after every run, success or refusal.
    {
        var deRoot=Path.Combine(work,"diff-edits"); Directory.CreateDirectory(deRoot);
        string DE((int Exit,string Out,string Err) r)=>"exit "+r.Exit+" "+(r.Out.Length>400?r.Out[..400]+"...":r.Out)+(r.Err.Length>0?" stderr "+r.Err:"");
        string DInput(string name,string content) { var path=Path.Combine(deRoot,name); File.WriteAllText(path,content); File.SetLastWriteTimeUtc(path,new DateTime(2001,2,3,4,5,6,DateTimeKind.Utc)); return path; }
        Project Applied(Project project,IReadOnlyList<ElementEdit> edits) {
            var updates=edits.Where(e=>e.Action=="update").ToDictionary(e=>e.Id,e=>e.Element!);
            var deletes=edits.Where(e=>e.Action=="delete").Select(e=>e.Id).ToHashSet();
            var elements=project.Elements.Where(e=>!deletes.Contains(e.Id)).Select(e=>updates.TryGetValue(e.Id,out var u)?u:e).ToList();
            elements.AddRange(edits.Where(e=>e.Action=="create").Select(e=>e.Element!));
            return project with { Elements=elements.ToArray() };
        }
        var deRuns=0;
        // Every run: the project and edits files keep their bytes and mtime, and the directory gains no file.
        (int Exit,string Out,string Err) Run(string name,string projectPath,string editsPath,params string[] arguments) {
            var watched=new[]{projectPath,editsPath}.Where(File.Exists).Distinct().ToArray();
            var bytes=watched.Select(File.ReadAllBytes).ToArray(); var times=watched.Select(File.GetLastWriteTimeUtc).ToArray();
            var listing=Directory.GetFileSystemEntries(deRoot).Order(StringComparer.Ordinal).ToArray();
            var r=Cli(arguments.Length>0?arguments:new[]{"diff-edits","--project",projectPath,"--edits",editsPath});
            var untouched=watched.Select((p,i)=>File.ReadAllBytes(p).AsSpan().SequenceEqual(bytes[i]) && File.GetLastWriteTimeUtc(p)==times[i]).All(x=>x)
                && Directory.GetFileSystemEntries(deRoot).Order(StringComparer.Ordinal).SequenceEqual(listing);
            Check(untouched,"diff-edits never writes the project or edits file ("+name+")",DE(r));
            deRuns++;
            return r;
        }
        void Succeeds(string name,string projectText,IReadOnlyList<ElementEdit> edits,string? editsText=null) {
            var project=P(projectText);
            var projectPath=DInput(name+".project.json",projectText);
            var editsPath=DInput(name+".edits.json",editsText ?? JsonSerializer.Serialize(edits));
            var library=JsonSerializer.Serialize(IncrementalDiff.Diff(project,edits).Plan);
            var appliedProject=Applied(project,edits);
            Check(library==JsonSerializer.Serialize(Compiler.Diff(project,appliedProject)),"diff-edits fixture: library plan equals Compiler.Diff of the applied project ("+name+")");
            var appliedPath=DInput(name+".applied.json",JsonSerializer.Serialize(appliedProject));
            var diffVerb=Cli("diff",projectPath,appliedPath);
            Check(diffVerb.Exit==0 && diffVerb.Out==library,"diff-edits fixture: diff verb on the applied project equals the library plan ("+name+")",DE(diffVerb));
            var r=Run(name,projectPath,editsPath);
            Check(r.Exit==0 && r.Out==library && r.Out==diffVerb.Out && r.Err.Length==0,"diff-edits stdout byte-identical to the library plan and to diff of the applied project ("+name+")",DE(r));
            // The flags may come in either order.
            var swapped=Run(name+" swapped",projectPath,editsPath,"diff-edits","--edits",editsPath,"--project",projectPath);
            Check(swapped.Exit==0 && swapped.Out==library,"diff-edits accepts --edits before --project ("+name+")",DE(swapped));
        }
        void Refused(string name,string code,string projectPath,string editsPath,params string[] arguments) {
            var r=Run(name,projectPath,editsPath,arguments);
            Check(r.Exit==1 && r.Out==FailedCode(code),"diff-edits refuses "+name+" as "+code,DE(r));
        }
        // The library refusal for the same in-process inputs (ArgumentException / InvalidOperationException message).
        string LibraryCode(string projectText,IReadOnlyList<ElementEdit> edits) {
            try { IncrementalDiff.Diff(P(projectText),edits); return "succeeded"; }
            catch(Exception e) when (e is ArgumentException or InvalidOperationException) { return e.Message; }
        }
        void RefusedLike(string name,string code,string projectText,IReadOnlyList<ElementEdit> edits) {
            Check(LibraryCode(projectText,edits)==code,"diff-edits fixture: the library refuses "+name+" as "+code,LibraryCode(projectText,edits));
            Refused(name,code,DInput(name+".project.json",projectText),DInput(name+".edits.json",JsonSerializer.Serialize(edits)));
        }

        var baseProject=P(beforeText); var wall=baseProject.Elements[0]; var plan=baseProject.Elements[1];
        Element Wall(Guid id,double x,Guid[]? dependencies=null)=>new(id,"wall",new[]{x,0,x+5,0,3,0.2},dependencies ?? Array.Empty<Guid>(),new("cli diff-edits fixture","specified"));
        Element Doc(Guid id,params Guid[] dependencies)=>new(id,"plan",Array.Empty<double>(),dependencies,new("cli diff-edits fixture","proposed"));
        Guid G(int n)=>Guid.Parse("00000000-0000-0000-0000-"+n.ToString("x12",System.Globalization.CultureInfo.InvariantCulture));

        // 1. The examples' 5 m -> 5.4 m wall edit: the same plan as `diff examples/before.json examples/after.json`.
        var widened=P(afterText).Elements[0];
        Succeeds("examples wall update",beforeText,new[]{ElementEdit.Update(widened)});
        Check(Run("examples vs diff",DInput("ex.project.json",beforeText),DInput("ex.edits.json",JsonSerializer.Serialize(new[]{ElementEdit.Update(widened)}))).Out==expectedDiff,"diff-edits on the examples prints exactly diff examples/before.json examples/after.json");
        // 2. The edits file format, pinned by a hand-written literal: an array of {"Action","Id","Element"}.
        var literal="[{\"Action\":\"update\",\"Id\":\"00000000-0000-0000-0000-000000000002\",\"Element\":{\"Id\":\"00000000-0000-0000-0000-000000000002\",\"Kind\":\"wall\",\"GeometryMetres\":[0,0,5.4,0,3,0.2],\"Dependencies\":[],\"Provenance\":{\"Source\":\"synthetic core fixture\",\"Approval\":\"specified\"}}}]";
        Succeeds("literal edits file",beforeText,new[]{ElementEdit.Update(widened)},literal);
        // 3. Mixed create/update/delete: delete the plan, update the wall, create a wall and a plan depending on it.
        Succeeds("mixed create update delete",beforeText,new[]{ElementEdit.Create(Wall(G(0x10),20)),ElementEdit.Delete(plan.Id),ElementEdit.Update(wall with {GeometryMetres=new[]{0,0,6,0,3,0.25}}),ElementEdit.Create(Doc(G(0x11),G(0x10),wall.Id))});
        // 4. A no-op update and an empty edit set: no operations, hashes equal.
        Succeeds("no-op update",beforeText,new[]{ElementEdit.Update(wall)});
        Succeeds("empty edit set",beforeText,Array.Empty<ElementEdit>());
        // 5. A larger project (300 walls, each with a plan) with edits far apart.
        var big=new List<Element>();
        for(var i=0;i<300;i++) { big.Add(Wall(G(0x1000+i),i*10)); big.Add(Doc(G(0x2000+i),G(0x1000+i))); }
        var bigText=JsonSerializer.Serialize(new Project(baseProject.Id,7,big.ToArray()));
        Succeeds("300-wall project",bigText,new[]{ElementEdit.Update(Wall(G(0x1000+5),51)),ElementEdit.Delete(G(0x2000+150)),ElementEdit.Delete(G(0x1000+150)),ElementEdit.Create(Doc(G(0x3000),G(0x1000+299),G(0x1000+0)))});
        // 6. Inputs decode like every CLI file input (UTF-8 BOM).
        Succeeds("UTF-8 BOM edits",beforeText,new[]{ElementEdit.Update(widened)},"﻿"+JsonSerializer.Serialize(new[]{ElementEdit.Update(widened)}));

        // 7. Library refusals keep the library's code.
        RefusedLike("an unknown action","edit_invalid",beforeText,new[]{new ElementEdit("move",wall.Id,wall)});
        RefusedLike("a create whose element id differs","edit_invalid",beforeText,new[]{new ElementEdit("create",G(0x20),Wall(G(0x21),30))});
        RefusedLike("a delete carrying an element","edit_invalid",beforeText,new[]{new ElementEdit("delete",plan.Id,plan)});
        RefusedLike("an update of an unknown id","edit_unknown_id",beforeText,new[]{ElementEdit.Update(Wall(G(0x30),40))});
        RefusedLike("a delete of an unknown id","edit_unknown_id",beforeText,new[]{ElementEdit.Delete(G(0x31))});
        RefusedLike("two edits of one id","edit_duplicate_id",beforeText,new[]{ElementEdit.Update(widened),ElementEdit.Delete(wall.Id)});
        RefusedLike("a create of an existing id","edit_create_exists",beforeText,new[]{ElementEdit.Create(wall)});
        RefusedLike("more than IncrementalDiff.MaxEdits edits","edit_bound_exceeded",beforeText,Enumerable.Range(0,IncrementalDiff.MaxEdits+1).Select(i=>ElementEdit.Delete(G(0x40000+i))).ToArray());
        RefusedLike("a delete leaving a dangling dependency","invalid_ir",beforeText,new[]{ElementEdit.Delete(wall.Id)});
        RefusedLike("an edit creating a dependency cycle","invalid_ir",beforeText,new[]{ElementEdit.Update(wall with {Dependencies=new[]{plan.Id}})});
        RefusedLike("an invalid wall geometry","invalid_ir",beforeText,new[]{ElementEdit.Update(wall with {GeometryMetres=new[]{0,0,0,0,3,0.2}})});
        RefusedLike("an invalid project","invalid_ir",JsonSerializer.Serialize(baseProject with {Elements=new[]{plan}}),Array.Empty<ElementEdit>());
        var editsOk=DInput("ok.edits.json",JsonSerializer.Serialize(new[]{ElementEdit.Update(widened)}));
        var projectOk=DInput("ok.project.json",beforeText);
        // A null entry in the array is the library's malformed edit.
        Refused("a null edit entry","edit_invalid",projectOk,DInput("null-entry.edits.json","[null]"));
        Refused("an update with null geometry","edit_invalid",projectOk,DInput("null-geometry.edits.json","[{\"Action\":\"update\",\"Id\":\"00000000-0000-0000-0000-000000000002\",\"Element\":{\"Id\":\"00000000-0000-0000-0000-000000000002\",\"Kind\":\"wall\",\"GeometryMetres\":null,\"Dependencies\":[],\"Provenance\":null}}]"));

        // 8. Malformed JSON: typed CLI refusals, never a raw exception message.
        foreach(var (label,text) in new[]{("not JSON","{"),("JSON null","null"),("an object","{\"Action\":\"delete\"}"),("a string","\"edits\""),("an unknown member","[{\"Action\":\"delete\",\"Id\":\"00000000-0000-0000-0000-000000000003\",\"Elemnt\":null}]"),("a bad guid","[{\"Action\":\"delete\",\"Id\":\"not-a-guid\"}]"),("trailing content","[] []"),("empty file","")})
            Refused("edits that are "+label,"diff_edits_edits_invalid",projectOk,DInput("bad-"+label.Replace(' ','-')+".edits.json",text));
        foreach(var (label,text) in new[]{("not JSON","{"),("JSON null","null"),("an array","[]"),("null elements","{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Revision\":0,\"Elements\":null}"),("a null element","{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Revision\":0,\"Elements\":[null]}"),("null geometry","{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Revision\":0,\"Elements\":[{\"Id\":\"00000000-0000-0000-0000-000000000002\",\"Kind\":\"plan\",\"GeometryMetres\":null,\"Dependencies\":[],\"Provenance\":null}]}"),("null dependencies","{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Revision\":0,\"Elements\":[{\"Id\":\"00000000-0000-0000-0000-000000000002\",\"Kind\":\"plan\",\"GeometryMetres\":[],\"Dependencies\":null,\"Provenance\":null}]}"),("empty file","")})
            Refused("a project that is "+label,"diff_edits_project_invalid",DInput("bad-"+label.Replace(' ','-')+".project.json",text),editsOk);

        // 9. Bounded inputs: the shared BoundedInput reader and its typed codes, for either file.
        var deMissing=Path.Combine(deRoot,"absent.json");
        Refused("a missing project","input_not_found",deMissing,editsOk,"diff-edits","--project",deMissing,"--edits",editsOk);
        Refused("a missing edits file","input_not_found",projectOk,deMissing,"diff-edits","--project",projectOk,"--edits",deMissing);
        var deDirectory=Path.Combine(deRoot,"a-directory.json"); Directory.CreateDirectory(deDirectory);
        Refused("a directory as project","input_not_regular_file",projectOk,editsOk,"diff-edits","--project",deDirectory,"--edits",editsOk);
        Refused("a directory as edits","input_not_regular_file",projectOk,editsOk,"diff-edits","--project",projectOk,"--edits",deDirectory);
        var deOver=Path.Combine(deRoot,"over-cap.json"); File.WriteAllBytes(deOver,overCap);
        Refused("a project over 8 MiB","input_too_large",projectOk,deOver,"diff-edits","--project",deOver,"--edits",editsOk);
        Refused("edits over 8 MiB","input_too_large",projectOk,deOver,"diff-edits","--project",projectOk,"--edits",deOver);
        var deAtCap=new byte[cap]; var editsBytes=File.ReadAllBytes(editsOk); editsBytes.CopyTo(deAtCap,0); Array.Fill(deAtCap,(byte)' ',editsBytes.Length,cap-editsBytes.Length);
        var deAtCapPath=Path.Combine(deRoot,"at-cap.edits.json"); File.WriteAllBytes(deAtCapPath,deAtCap);
        var atCapRun=Run("edits of exactly 8 MiB",projectOk,deAtCapPath);
        Check(atCapRun.Exit==0 && atCapRun.Out==expectedDiff,"diff-edits accepts an edits file of exactly 8 MiB",DE(atCapRun));

        // 10. Usage: exactly --project <p> --edits <e>, each once, nothing else; checked before any file is read.
        foreach(var bad in new[]{new[]{"diff-edits"},new[]{"diff-edits","--project",projectOk},new[]{"diff-edits","--edits",editsOk},new[]{"diff-edits","--project",projectOk,"--edits"},
            new[]{"diff-edits","--project",projectOk,"--project",projectOk},new[]{"diff-edits","--project",projectOk,"--edits",editsOk,"extra"},new[]{"diff-edits","--project",projectOk,"--edits",editsOk,"--max","3"},
            new[]{"diff-edits",projectOk,editsOk},new[]{"diff-edits","--project","","--edits",editsOk},new[]{"diff-edits","--project",deMissing,"--edits",deMissing,"--bogus"}})
            Refused("arguments: "+string.Join(" ",bad.Skip(1).Select(a=>a==projectOk?"<project>":a==editsOk?"<edits>":a==deMissing?"<missing>":a)),"diff_edits_usage",projectOk,editsOk,bad);

        // 11. Static: the verb reads only through BoundedInput and has no write path.
        var deSourcePath=Path.Combine(root,"interfaces","cli","DiffEditsCommand.cs");
        Check(File.Exists(deSourcePath),"interfaces/cli/DiffEditsCommand.cs exists");
        if(File.Exists(deSourcePath)) {
            var deSource=File.ReadAllText(deSourcePath);
            Check(deSource.Contains("BoundedInput.ReadText(",StringComparison.Ordinal) && deSource.Contains("IncrementalDiff",StringComparison.Ordinal) && !deSource.Contains("Compiler.Diff",StringComparison.Ordinal)
                && new[]{"File.","FileStream","FileMode.","Directory.","ProjectStore","Commit("}.All(api=>!deSource.Contains(api,StringComparison.Ordinal)),
                "diff-edits reads only through BoundedInput, diffs through IncrementalDiff (not Compiler.Diff) and has no write path");
        }
        Check(deRuns>=60,"diff-edits ran every case",deRuns.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    Console.WriteLine(JsonSerializer.Serialize(new {passed,failed,skipped,cli_inputs=failed==0}));
    if(failed>0) Environment.ExitCode=1;
} catch(Exception exception) {
    Console.WriteLine("::error::cli-inputs harness error: "+exception.Message.ReplaceLineEndings(" "));
    Console.Error.WriteLine(exception);
    Environment.ExitCode=1;
} finally {
    try { if(Directory.Exists(work)) Directory.Delete(work,true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
}
