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

    Console.WriteLine(JsonSerializer.Serialize(new {passed,failed,skipped,cli_inputs=failed==0}));
    if(failed>0) Environment.ExitCode=1;
} catch(Exception exception) {
    Console.WriteLine("::error::cli-inputs harness error: "+exception.Message.ReplaceLineEndings(" "));
    Console.Error.WriteLine(exception);
    Environment.ExitCode=1;
} finally {
    try { if(Directory.Exists(work)) Directory.Delete(work,true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
}
