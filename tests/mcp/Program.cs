using Bim.Core;
using Bim.Mcp;
using System.Text.Json;
using System.Text.Json.Nodes;
// MCP oracle: every tool text must equal the CLI stdout line (JsonSerializer defaults) byte for byte.
var storeDir=Path.Combine(Path.GetTempPath(),"mcp-oracle-"+Guid.NewGuid().ToString("N"));
var cliDir=Path.Combine(Path.GetTempPath(),"mcp-cli-inputs-"+Guid.NewGuid().ToString("N"));
try {
int count=0;
void Check(bool condition,string name) { if(!condition) throw new Exception(name); Console.WriteLine("PASS "+name); count++; }
string FindRoot() {
    var dir=new DirectoryInfo(AppContext.BaseDirectory);
    while(dir!=null && !File.Exists(Path.Combine(dir.FullName,"global.json"))) dir=dir.Parent;
    if(dir==null) throw new Exception("repository root with global.json not found");
    return dir.FullName;
}
var root=FindRoot();
string Example(string name)=>File.ReadAllText(Path.Combine(root,"examples",name));
JsonNode Node(string json)=>JsonNode.Parse(json) ?? throw new Exception("null json");
string Failed(Action action) {
    try { action(); } catch(Exception e) { return JsonSerializer.Serialize(new {status="failed",code=e.Message}); }
    throw new Exception("expected direct call to fail");
}
var server=new McpServer();
JsonObject Call(string line)=>Node(server.Handle(line) ?? throw new Exception("expected response for: "+line)).AsObject();
string Request(JsonNode? id,string method,JsonNode? parameters) {
    var request=new JsonObject{["jsonrpc"]="2.0",["id"]=id,["method"]=method};
    if(parameters!=null) request["params"]=parameters;
    return request.ToJsonString();
}
JsonObject ToolCall(string name,JsonObject arguments)=>Call(Request(7,"tools/call",new JsonObject{["name"]=name,["arguments"]=arguments}));
string Text(JsonObject response)=>response["result"]!["content"]![0]!["text"]!.GetValue<string>();
bool IsError(JsonObject response)=>response["result"]!["isError"]!.GetValue<bool>();
bool HasType(JsonObject response)=>response["result"]!["content"]![0]!["type"]!.GetValue<string>()=="text";
int ErrorCode(JsonObject response)=>response["error"]!["code"]!.GetValue<int>();
bool NullId(JsonObject response)=>response.ContainsKey("id") && response["id"]==null;

var beforeText=Example("before.json"); var afterText=Example("after.json"); var intentText=Example("room-intent.json");
Project P(string json)=>JsonSerializer.Deserialize<Project>(json)!;

// Real CLI oracle: verify.ps1 builds interfaces/cli in Release before mcp-contract runs.
string dotnet=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: >0 } host ? host : "dotnet";
var cliDll=Path.Combine(root,"interfaces","cli","bin","Release","net8.0","Bim.Cli.dll");
if(!File.Exists(cliDll)) throw new Exception("CLI binary missing: "+cliDll+" (build interfaces/cli --configuration Release first, as scripts/verify.ps1 does)");
Directory.CreateDirectory(cliDir);
string Input(string name,string content) { var path=Path.Combine(cliDir,name); File.WriteAllText(path,content); return path; }
string Cli(params string[] arguments) {
    var start=new System.Diagnostics.ProcessStartInfo(dotnet){RedirectStandardOutput=true,UseShellExecute=false,StandardOutputEncoding=new System.Text.UTF8Encoding(false)};
    start.ArgumentList.Add(cliDll);
    foreach(var argument in arguments) start.ArgumentList.Add(argument);
    using var process=System.Diagnostics.Process.Start(start) ?? throw new Exception("CLI process did not start: "+dotnet);
    var stdout=process.StandardOutput.ReadToEnd();
    if(!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("CLI timed out: "+string.Join(" ",arguments)); }
    if(stdout.Length==0) throw new Exception("CLI wrote no stdout: "+string.Join(" ",arguments));
    if(stdout.EndsWith(Environment.NewLine,StringComparison.Ordinal)) return stdout.Substring(0,stdout.Length-Environment.NewLine.Length);
    if(stdout.EndsWith("\r\n",StringComparison.Ordinal)) return stdout.Substring(0,stdout.Length-2);
    if(stdout.EndsWith("\n",StringComparison.Ordinal)) return stdout.Substring(0,stdout.Length-1);
    return stdout;
}
var beforePath=Input("before.json",beforeText); var afterPath=Input("after.json",afterText); var intentPath=Input("room-intent.json",intentText);

var init=Call(Request(1,"initialize",new JsonObject{["protocolVersion"]="2024-11-05",["capabilities"]=new JsonObject(),["clientInfo"]=new JsonObject{["name"]="oracle",["version"]="0"}}));
Check(init["jsonrpc"]!.GetValue<string>()=="2.0" && init["id"]!.GetValue<int>()==1 && !init.ContainsKey("error"),"initialize is a JSON-RPC 2.0 response echoing the id");
var info=init["result"]!;
Check(info["protocolVersion"]!.GetValue<string>()=="2024-11-05","initialize protocolVersion 2024-11-05");
Check(info["serverInfo"]!["name"]!.GetValue<string>()=="bim-agentic-os" && !string.IsNullOrWhiteSpace(info["serverInfo"]!["version"]!.GetValue<string>()),"initialize serverInfo name and version");
Check(info["capabilities"]!["tools"] is JsonObject,"initialize advertises tools capability");
Check(Call(Request("abc","initialize",null))["id"]!.GetValue<string>()=="abc","string request id echoed");

Check(server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")==null,"initialized notification has no response");
Check(server.Handle("""{"jsonrpc":"2.0","method":"notifications/unknown","params":{}}""")==null,"unknown notification has no response");

var list=Call(Request(2,"tools/list",null));
var tools=list["result"]!["tools"]!.AsArray();
Check(string.Join(",",tools.Select(t=>t!["name"]!.GetValue<string>()))=="diff,validate,room,plan","tools/list names exactly diff, validate, room, plan");
Check(tools.All(t=>t!["inputSchema"]!["type"]!.GetValue<string>()=="object" && t!["inputSchema"]!["properties"] is JsonObject && t!["inputSchema"]!["required"] is JsonArray && !string.IsNullOrWhiteSpace(t!["description"]!.GetValue<string>())),"every tool has a JSON Schema object inputSchema and description");
string Required(string tool)=>string.Join(",",tools.Single(t=>t!["name"]!.GetValue<string>()==tool)!["inputSchema"]!["required"]!.AsArray().Select(x=>x!.GetValue<string>()));
Check(Required("diff")=="before,after" && Required("validate")=="project" && Required("room")=="project_guid,revision,intent" && Required("plan")=="store_path,desired","tool schemas require the CLI inputs");

var diff=ToolCall("diff",new JsonObject{["before"]=Node(beforeText),["after"]=Node(afterText)});
Check(!IsError(diff) && HasType(diff) && Text(diff)==JsonSerializer.Serialize(Compiler.Diff(P(beforeText),P(afterText))),"diff text byte-equal to CLI serialization of Compiler.Diff");
var conflictAfter=Node(afterText); conflictAfter["Revision"]=1;
var conflict=ToolCall("diff",new JsonObject{["before"]=Node(beforeText),["after"]=conflictAfter});
Check(IsError(conflict) && Text(conflict)=="""{"status":"failed","code":"revision_conflict"}""","diff core failure returns CLI failure text with isError");
Check(Text(diff)==Cli("diff",beforePath,afterPath),"diff text byte-equal to real CLI stdout");
Check(Text(conflict)==Cli("diff",beforePath,Input("conflict.json",conflictAfter.ToJsonString())),"diff failure text byte-equal to real CLI stdout");

var valid=ToolCall("validate",new JsonObject{["project"]=Node(beforeText)});
Check(!IsError(valid) && Text(valid)==JsonSerializer.Serialize(Compiler.Validate(P(beforeText))) && Text(valid)=="[]","validate text byte-equal to CLI serialization for valid project");
var dangling=Node(beforeText); dangling["Elements"]!.AsArray().RemoveAt(0);
var invalid=ToolCall("validate",new JsonObject{["project"]=dangling.DeepClone()});
Check(IsError(invalid) && Text(invalid)==JsonSerializer.Serialize(Compiler.Validate(P(dangling.ToJsonString()))) && Text(invalid).Contains("missing_dependency"),"validate issues set isError with byte-equal issue text");
Check(Text(valid)==Cli("validate",beforePath),"valid validate text byte-equal to real CLI stdout");
Check(Text(invalid)==Cli("validate",Input("dangling.json",dangling.ToJsonString())),"invalid validate text byte-equal to real CLI stdout");

var projectGuid="00000000-0000-0000-0000-000000000001";
var room=ToolCall("room",new JsonObject{["project_guid"]=projectGuid,["revision"]=0,["intent"]=Node(intentText)});
Check(!IsError(room) && Text(room)==JsonSerializer.Serialize(RoomCompiler.Compile(Guid.Parse(projectGuid),0,JsonSerializer.Deserialize<RoomIntent>(intentText)!)),"room text byte-equal to CLI serialization of RoomCompiler.Compile");
var feet=Node(intentText); feet["Units"]="ft";
var badRoom=ToolCall("room",new JsonObject{["project_guid"]=projectGuid,["revision"]=0,["intent"]=feet.DeepClone()});
Check(IsError(badRoom) && Text(badRoom)==Failed(()=>RoomCompiler.Compile(Guid.Parse(projectGuid),0,JsonSerializer.Deserialize<RoomIntent>(feet.ToJsonString())!)),"room constraint failure returns CLI failure text");
var badGuid=ToolCall("room",new JsonObject{["project_guid"]="not-a-guid",["revision"]=0,["intent"]=Node(intentText)});
Check(IsError(badGuid) && Text(badGuid)==Failed(()=>Guid.Parse("not-a-guid")),"room guid parse failure returns CLI failure text");
Check(Text(room)==Cli("room",projectGuid,"0",intentPath),"room text byte-equal to real CLI stdout");
Check(Text(badRoom)==Cli("room",projectGuid,"0",Input("feet.json",feet.ToJsonString())),"room constraint failure byte-equal to real CLI stdout");
Check(Text(badGuid)==Cli("room","not-a-guid","0",intentPath),"room guid failure byte-equal to real CLI stdout");
// Non-ASCII and HTML-sensitive characters must survive the envelope and match the CLI escaping.
const string unicodeSource="caf\u00e9 <wall> + fa\u00e7ade";
var unicodeIntentText=intentText.Replace("synthetic Test A intent fixture",unicodeSource);
if(unicodeIntentText==intentText) throw new Exception("room intent provenance fixture changed");
var unicodeRoom=ToolCall("room",new JsonObject{["project_guid"]=projectGuid,["revision"]=0,["intent"]=Node(unicodeIntentText)});
Check(!IsError(unicodeRoom) && Text(unicodeRoom)==JsonSerializer.Serialize(RoomCompiler.Compile(Guid.Parse(projectGuid),0,JsonSerializer.Deserialize<RoomIntent>(unicodeIntentText)!)),"room with non-ASCII/HTML provenance byte-equal to direct serialization");
Check(Text(unicodeRoom)==Cli("room",projectGuid,"0",Input("unicode-intent.json",unicodeIntentText)),"room with non-ASCII/HTML provenance byte-equal to real CLI stdout");
Check(JsonSerializer.Deserialize<Project>(Text(unicodeRoom))!.Elements.All(e=>e.Provenance.Source==unicodeSource),"non-ASCII/HTML provenance round-trips");
var unicodeAfterText=afterText.Replace("synthetic core fixture",unicodeSource);
var unicodeDiff=ToolCall("diff",new JsonObject{["before"]=Node(beforeText),["after"]=Node(unicodeAfterText)});
Check(!IsError(unicodeDiff) && Text(unicodeDiff)==JsonSerializer.Serialize(Compiler.Diff(P(beforeText),P(unicodeAfterText))) && Text(unicodeDiff)==Cli("diff",beforePath,Input("unicode-after.json",unicodeAfterText)),"diff with non-ASCII/HTML provenance byte-equal to direct and real CLI");

new ProjectStore(storeDir).Initialize(P(beforeText));
var plan=ToolCall("plan",new JsonObject{["store_path"]=storeDir,["desired"]=Node(afterText)});
Check(!IsError(plan) && Text(plan)==JsonSerializer.Serialize(Compiler.Diff(new ProjectStore(storeDir).Read(),P(afterText))),"plan text byte-equal to CLI serialization against a temp ProjectStore");
Check(Text(plan)==Cli("plan",storeDir,afterPath),"plan text byte-equal to real CLI stdout");
var missingStore=Path.Combine(storeDir,"missing");
var noStore=ToolCall("plan",new JsonObject{["store_path"]=missingStore,["desired"]=Node(afterText)});
Check(IsError(noStore) && Text(noStore)==Failed(()=>new ProjectStore(missingStore).Read()),"plan on missing store returns CLI failure text");
Check(!Directory.Exists(missingStore),"plan does not create a store");
Check(Text(noStore)==Cli("plan",missingStore,afterPath),"plan missing-store failure byte-equal to real CLI stdout");
var silentStore=Path.Combine(storeDir,"never-created");
var silentCall=new JsonObject{["jsonrpc"]="2.0",["method"]="tools/call",["params"]=new JsonObject{["name"]="plan",["arguments"]=new JsonObject{["store_path"]=silentStore,["desired"]=Node(afterText)}}};
Check(server.Handle(silentCall.ToJsonString())==null && !Directory.Exists(silentStore),"id-less tools/call gives no response and does not execute");

foreach(var hidden in new[]{"native","evaluate","init","commit-intent","ifc-export","unknown"}) {
    var unknown=ToolCall(hidden,new JsonObject());
    Check(IsError(unknown) && Text(unknown)=="""{"status":"failed","code":"unknown_tool"}""","tool "+hidden+" is not exposed and returns isError");
}

Check(ErrorCode(Call(Request(3,"resources/list",null)))==-32601,"unknown method returns -32601");
var malformed=Call("{\"jsonrpc\":\"2.0\",\"id\":4,");
Check(ErrorCode(malformed)==-32700 && NullId(malformed),"malformed JSON returns -32700 with null id");
Check(ErrorCode(Call("""{"jsonrpc":"2.0","id":5}"""))==-32600,"request without method returns -32600");
Check(ErrorCode(Call("[]"))==-32600,"non-object request returns -32600");
Check(ErrorCode(Call(Request(6,"tools/call",new JsonObject{["arguments"]=new JsonObject()})))==-32602,"tools/call without name returns -32602");
Check(ErrorCode(Call(Request(6,"tools/call",new JsonArray())))==-32602,"tools/call with non-object params returns -32602");
Check(ErrorCode(ToolCall("diff",new JsonObject{["before"]=Node(beforeText)}))==-32602,"missing tool argument returns -32602");
Check(ErrorCode(ToolCall("room",new JsonObject{["project_guid"]=projectGuid,["revision"]="0",["intent"]=Node(intentText)}))==-32602,"wrongly typed tool argument returns -32602");
Check(ErrorCode(ToolCall("validate",new JsonObject{["project"]=Node(beforeText),["extra"]=1}))==-32602,"unexpected tool argument returns -32602");

var oversizedLine="{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/list\",\"params\":{\"pad\":\""+new string('a',McpServer.MaxLineChars)+"\"}}";
var oversized=Call(oversizedLine);
Check(ErrorCode(oversized)==-32600 && NullId(oversized),"oversized line rejected with an error");

var stream=string.Join("\n",new[]{oversizedLine,Request(9,"initialize",null),"""{"jsonrpc":"2.0","method":"notifications/initialized"}""","","not json",Request(10,"tools/list",null)})+"\n";
var output=new StringWriter();
McpServer.Run(new StringReader(stream),output);
var lines=output.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries);
Check(lines.Length==4,"stdio loop answers requests, skips notifications and blank lines");
var parsed=lines.Select(l=>Node(l).AsObject()).ToArray();
Check(parsed.All(r=>r["jsonrpc"]!.GetValue<string>()=="2.0"),"stdio loop writes only JSON-RPC lines");
Check(ErrorCode(parsed[0])==-32600 && parsed[1]["id"]!.GetValue<int>()==9 && ErrorCode(parsed[2])==-32700 && parsed[3]["id"]!.GetValue<int>()==10,"stdio loop recovers after oversized and malformed lines");
Check(!output.ToString().Contains('\r'),"stdio responses are newline-delimited");

Console.WriteLine(JsonSerializer.Serialize(new {passed=count, mcp_contract=true, native_verified=false}));
} catch(Exception exception) {
    Console.Error.WriteLine(exception);
    Environment.ExitCode=1;
} finally {
    if(Directory.Exists(storeDir)) Directory.Delete(storeDir,true);
    if(Directory.Exists(cliDir)) Directory.Delete(cliDir,true);
}
