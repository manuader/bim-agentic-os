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
Check(string.Join(",",tools.Select(t=>t!["name"]!.GetValue<string>()))=="diff,validate,room,plan,mirror_drift,mirror_drift_select,mirror_drift_adopt,skills_lock_verify,audit_verify,diff_intent,"+string.Join(",",RevitCommandCatalogue.All.Select(c=>c.ToolName)),"tools/list names exactly diff, validate, room, plan, mirror_drift, mirror_drift_select, mirror_drift_adopt, skills_lock_verify, audit_verify, diff_intent, then one revit_* tool per catalogue command (RevitCommandCatalogue.All, in order)");
Check(RevitCommandCatalogue.V1.Select(c=>c.ToolName).SequenceEqual(new[]{"revit_query_levels","revit_query_wall_types","revit_query_floor_types","revit_query_family_symbols","revit_wall_create","revit_opening_place","revit_floor_create","revit_room_create"}),"catalogue v1 tool names are the eight ADR0009 commands");
Check(tools.All(t=>t!["inputSchema"]!["type"]!.GetValue<string>()=="object" && t!["inputSchema"]!["properties"] is JsonObject && t!["inputSchema"]!["required"] is JsonArray && !string.IsNullOrWhiteSpace(t!["description"]!.GetValue<string>())),"every tool has a JSON Schema object inputSchema and description");
string Required(string tool)=>string.Join(",",tools.Single(t=>t!["name"]!.GetValue<string>()==tool)!["inputSchema"]!["required"]!.AsArray().Select(x=>x!.GetValue<string>()));
Check(Required("diff")=="before,after" && Required("validate")=="project" && Required("room")=="project_guid,revision,intent" && Required("plan")=="store_path,desired" && Required("mirror_drift")=="store_path,snapshot,mirror" && Required("skills_lock_verify")=="skills_path,lock_path" && Required("audit_verify")=="log_path","tool schemas require the CLI inputs");

// P9-05: the Revit tools are derived from the whole catalogue (v1 + v2 + whatever is added later), never a fixed list.
// Each tool states the P10-03 permission level it needs on a test copy; a write to a real project needs more.
const string McpUid="60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
const string McpCopy=@"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
const string McpReal=@"C:\Projects\Real\Tower.rvt";
var copiesRoots=PermissionPolicy.DefaultCopiesRoots(@"C:\Repo");
string LevelName(PermissionLevel level)=>level switch { PermissionLevel.Read=>"read", PermissionLevel.WriteCopies=>"write-copies", PermissionLevel.WriteRealProject=>"write-real-project", _=>throw new Exception("level") };
var revitTools=tools.Where(t=>t!["name"]!.GetValue<string>().StartsWith(RevitCommandSpec.ToolPrefix,StringComparison.Ordinal)).ToArray();
Check(revitTools.Length==RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(c=>c.ToolName).Distinct().Count()==RevitCommandCatalogue.All.Count,"exactly one revit_* tool per catalogue command, no extra and no duplicate");
var mcpToolName=new System.Text.RegularExpressions.Regex(@"\A[a-zA-Z0-9_-]{1,64}\z");
Check(tools.All(t=>mcpToolName.IsMatch(t!["name"]!.GetValue<string>())) && tools.Select(t=>t!["name"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count()==tools.Count,"every listed tool name is unique and matches ^[a-zA-Z0-9_-]{1,64}$");
foreach(var v2 in new[]{"revit_query_roof_types","revit_roof_create_footprint","revit_stair_create","revit_curtain_wall_create","revit_floor_create_opening"})
    Check(tools.Count(t=>t!["name"]!.GetValue<string>()==v2)==1,"catalogue v2 tool "+v2+" is listed");
Check(RevitCommandCatalogue.V2.All(spec=>RevitCommandCatalogue.All.Contains(spec)) && RevitCommandCatalogue.V1.All(spec=>RevitCommandCatalogue.All.Contains(spec)),"the catalogue enumeration contains v1 and v2");
foreach(var spec in RevitCommandCatalogue.All) {
    var tool=tools.Single(t=>t!["name"]!.GetValue<string>()==spec.ToolName)!;
    var schema=tool["inputSchema"]!;
    Check(schema["properties"]!["args"]!.ToJsonString()==JsonNode.Parse(spec.ArgsSchemaJson)!.ToJsonString()
        && string.Join(",",schema["required"]!.AsArray().Select(x=>x!.GetValue<string>()))=="pipe_name,operation_id,document,args"
        && schema["additionalProperties"]!.GetValue<bool>()==false
        && tool["description"]!.GetValue<string>().StartsWith(spec.Description,StringComparison.Ordinal),spec.ToolName+" schema embeds the catalogue args schema and description");
    var required=PermissionPolicy.RequiredLevel(spec.Name,McpCopy,copiesRoots) ?? throw new Exception("catalogue command without a level: "+spec.Name);
    Check(required==(spec.Writes?PermissionLevel.WriteCopies:PermissionLevel.Read),spec.Name+" policy level on a test copy follows read/write");
    Check(tool["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()==LevelName(required) && tool["permission_level"]==null,spec.ToolName+" lists _meta bim-agentic-os/permission_level "+LevelName(required)+" (P10-03 policy on a test copy) and no top-level field");
    var annotations=tool["annotations"]?.AsObject();
    // view.export_image is read-only for the model (permission level read) but writes image files, so it is not
    // listed readOnlyHint true (chat-export-save review).
    var writesFiles=RevitCommandCatalogue.WritesFiles(spec.Name);
    Check(annotations?["readOnlyHint"]?.GetValue<bool>()==!(spec.Writes || writesFiles),spec.ToolName+" annotations.readOnlyHint is "+!(spec.Writes || writesFiles));
    if(spec.Writes) {
        var destructive=new[]{"element.set_parameter","element.delete","wall.set_profile","material.set","element.change_type","element.move","element.rotate","element.mirror","document.save","document.save_as","command.batch"}.Contains(spec.Name);
        Check(annotations!["destructiveHint"]?.GetValue<bool>()==destructive && annotations["idempotentHint"]?.GetValue<bool>()==true,spec.ToolName+" write annotations: destructiveHint "+destructive.ToString().ToLowerInvariant()+" (only element.set_parameter, element.delete, wall.set_profile, material.set, element.change_type, element.move, element.rotate and element.mirror change or remove existing elements, document.save overwrites the saved file, document.save_as re-points the open document to a new file, and a command.batch step may do either), idempotentHint true (operation_id replay)");
        var description=tool["description"]!.GetValue<string>();
        Check(PermissionPolicy.RequiredLevel(spec.Name,McpReal,copiesRoots)==PermissionLevel.WriteRealProject
            && description.Contains(".bim/native-runs",StringComparison.Ordinal) && description.Contains(".bim/revit-enabled-roots.txt",StringComparison.Ordinal)
            && description.Contains("no per-document or per-actor check",StringComparison.Ordinal),spec.ToolName+" on a real project needs write-real-project; description states the directory-root write rule");
    }
    else if(writesFiles) Check(annotations!.Count==3 && annotations["destructiveHint"]?.GetValue<bool>()==false && annotations["idempotentHint"]?.GetValue<bool>()==false,spec.ToolName+" writes new files on every call: destructiveHint false, idempotentHint false");
    else Check(annotations!.Count==1,spec.ToolName+" read annotations carry only readOnlyHint");
}
Check(tools.Where(t=>!t!["name"]!.GetValue<string>().StartsWith(RevitCommandSpec.ToolPrefix,StringComparison.Ordinal) && t!["name"]!.GetValue<string>()!="audit_verify" && t!["name"]!.GetValue<string>()!="diff_intent" && t!["name"]!.GetValue<string>()!="mirror_drift_adopt").All(t=>t!["_meta"]==null && t!["permission_level"]==null && t!["annotations"]==null),"core tools other than audit_verify, diff_intent and mirror_drift_adopt carry no permission level or annotations");
Check(McpServer.PermissionLevelName(PermissionLevel.Read)=="read" && McpServer.PermissionLevelName(PermissionLevel.WriteCopies)=="write-copies" && McpServer.PermissionLevelName(PermissionLevel.WriteRealProject)=="write-real-project","PermissionLevelName names the three P10-03 levels");
var outOfRange=false;
try { McpServer.PermissionLevelName((PermissionLevel)99); } catch(ArgumentOutOfRangeException) { outOfRange=true; }
Check(outOfRange,"PermissionLevelName rejects an undefined level with ArgumentOutOfRangeException");

// The tool list fails closed at construction: duplicate or non-MCP tool names throw instead of shadowing a tool.
RevitCommandSpec Synthetic(string name,bool writes=false)=>new(name,writes,"Synthetic test command.","""{"type":"object","properties":{},"additionalProperties":false}""",_=>new EmptyQueryArgs());
string? BuildError(IReadOnlyList<RevitCommandSpec> catalogue) { try { _=new McpServer(catalogue,RevitCommandPolicy.None,null,null); return null; } catch(InvalidOperationException error) { return error.Message; } }
string? NameError(params string[] names) { try { McpServer.CheckToolNames(names); return null; } catch(InvalidOperationException error) { return error.Message; } }
Check(BuildError(new[]{Synthetic("query.levels"),Synthetic("wall.create",true)}) is null,"an injected catalogue with distinct valid names builds");
var syntheticServer=new McpServer(new[]{Synthetic("query.levels"),Synthetic("wall.create",true)},RevitCommandPolicy.None,null,null);
Check(string.Join(",",Node(syntheticServer.Handle(Request(2,"tools/list",null))!)["result"]!["tools"]!.AsArray().Select(t=>t!["name"]!.GetValue<string>()))=="diff,validate,room,plan,mirror_drift,mirror_drift_select,mirror_drift_adopt,skills_lock_verify,audit_verify,diff_intent,revit_query_levels,revit_wall_create","the tool list follows the injected catalogue");
Check(BuildError(new[]{Synthetic("a.b_c"),Synthetic("a_b.c")})=="tool_name_duplicate: revit_a_b_c","colliding tool names (a.b_c vs a_b.c) fail closed at construction");
Check(BuildError(new[]{Synthetic("query.levels"),Synthetic("query.levels")})=="tool_name_duplicate: revit_query_levels","a duplicated command fails closed at construction");
Check(BuildError(new[]{Synthetic("a b")})=="tool_name_invalid: revit_a b","a tool name outside [a-zA-Z0-9_-] fails closed at construction");
Check(BuildError(new[]{Synthetic(new string('x',59))})?.StartsWith("tool_name_invalid: ",StringComparison.Ordinal)==true,"a tool name longer than 64 characters fails closed at construction");
Check(BuildError(new[]{Synthetic("not.in_catalogue")})=="permission_level_unknown: not.in_catalogue","an entry the permission policy cannot classify fails closed at construction");
Check(NameError("revit_"+new string('x',58),"diff")==null && NameError("")!=null && NameError("diff","diff")=="tool_name_duplicate: diff","the name rule accepts 64 characters and rejects empty and duplicate names");

// v2 tools reach the same guarded service: reads go to the pipe on any document, writes only on enabled test copies.
var forwardedLines=new List<string>();
var guarded=new McpServer(new RevitCommandPolicy(new[]{@"C:\Repo\.bim\native-runs"}) with { ContentLibraryRoots=new[]{@"C:\ProgramData\Autodesk\RVT 2025\Libraries"} },(pipe,line)=>{ forwardedLines.Add(line); return null; },new RevitFileSystemProbe(_=>false,_=>null));
JsonObject Guarded(string tool,JsonNode commandArgs,string path)=>Node(guarded.Handle(Request(11,"tools/call",new JsonObject{["name"]=tool,["arguments"]=new JsonObject{["pipe_name"]="bim-agentic-os-2025-4242",["operation_id"]="op-p9-05",["document"]=new JsonObject{["path"]=path,["unique_id"]=McpUid},["args"]=commandArgs}})) ?? throw new Exception("no response")).AsObject();
string FailureCode(JsonObject response)=>Node(Text(response))["code"]!.GetValue<string>();
JsonArray Outline()=>new JsonArray(new JsonArray(0,0),new JsonArray(5000,0),new JsonArray(5000,4000),new JsonArray(0,4000));
var v2Writes=new (string Tool,Func<JsonNode> Args)[] {
    ("revit_roof_create_footprint",()=>new JsonObject{["level"]="Level 1",["type"]="Basic Roof",["boundary"]=Outline()}),
    ("revit_stair_create",()=>new JsonObject{["base_level"]="Level 1",["top_level"]="Level 2",["run"]=new JsonObject{["start"]=new JsonArray(0,0),["end"]=new JsonArray(4000,0)},["width_mm"]=1000}),
    ("revit_curtain_wall_create",()=>new JsonObject{["level"]="Level 1",["type"]="Curtain Wall 1",["height_mm"]=3000,["curve"]=new JsonObject{["kind"]="line",["start"]=new JsonArray(0,0),["end"]=new JsonArray(5000,0)}}),
    ("revit_floor_create_opening",()=>new JsonObject{["host_floor_uid"]=McpUid,["profile"]=new JsonArray(new JsonArray(1000,1000),new JsonArray(2000,1000),new JsonArray(2000,2000),new JsonArray(1000,2000))}),
};
var roofTypes=Guarded("revit_query_roof_types",new JsonObject(),McpReal);
Check(IsError(roofTypes) && FailureCode(roofTypes)==RevitCommandCodes.NoResponse && forwardedLines.Count==1 && forwardedLines[0].Contains("query.roof_types",StringComparison.Ordinal),"revit_query_roof_types is read-only: forwarded to the pipe even on a real project");
foreach(var (tool,makeArgs) in v2Writes) {
    var before=forwardedLines.Count;
    var refused=Guarded(tool,makeArgs(),McpReal);
    Check(IsError(refused) && FailureCode(refused)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" on a real project is refused document_not_enabled before the pipe");
    var allowed=Guarded(tool,makeArgs(),McpCopy);
    Check(IsError(allowed) && FailureCode(allowed)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1,tool+" on an enabled test copy passes the guards and is forwarded");
}

// Every catalogue entry with sample arguments is forwarded as an envelope whose command is that entry's name.
var sampleArgs=new Dictionary<string,Func<JsonNode>>{
    ["query.levels"]=()=>new JsonObject(), ["query.wall_types"]=()=>new JsonObject(), ["query.floor_types"]=()=>new JsonObject(),
    ["query.family_symbols"]=()=>new JsonObject{["category"]="door"},
    ["wall.create"]=()=>new JsonObject{["level"]="Level 1",["type"]="Generic - 200mm",["height_mm"]=3000,["curve"]=new JsonObject{["kind"]="line",["start"]=new JsonArray(0,0),["end"]=new JsonArray(5000,0)}},
    ["opening.place"]=()=>new JsonObject{["category"]="door",["symbol"]="Single-Flush: 0915 x 2134mm",["host_wall_uid"]=McpUid,["position_along_mm"]=1000},
    ["floor.create"]=()=>new JsonObject{["level"]="Level 1",["type"]="Generic 150mm",["boundary"]=Outline()},
    ["room.create"]=()=>new JsonObject{["level"]="Level 1",["point"]=new JsonArray(1000,1000)},
    ["query.roof_types"]=()=>new JsonObject(),
    ["query.elements"]=()=>new JsonObject{["category"]="wall",["level"]="Level 1",["parameters"]=new JsonArray("Mark"),["limit"]=50},
    ["query.inspect"]=()=>new JsonObject{["unique_id"]=McpUid},
    ["query.grids"]=()=>new JsonObject(), ["query.structural_types"]=()=>new JsonObject{["category"]="framing"},
    ["grid.create"]=()=>new JsonObject{["line"]=new JsonObject{["start"]=new JsonArray(0,0),["end"]=new JsonArray(0,12000)},["name"]="A"},
    ["structural_column.create"]=()=>new JsonObject{["type"]="W10x49",["point"]=new JsonArray(6000,4000),["base_level"]="Level 1",["top_level"]="Level 2"},
    ["structural_beam.create"]=()=>new JsonObject{["level"]="Level 2",["type"]="W12x26",["line"]=new JsonObject{["start"]=new JsonArray(0,0),["end"]=new JsonArray(6000,0)}},
    ["foundation.create_isolated"]=()=>new JsonObject{["level"]="Level 1",["type"]="1800 x 1200 x 450mm",["point"]=new JsonArray(6000,4000)},
    ["foundation.create_wall"]=()=>new JsonObject{["host_wall_uid"]=McpUid,["type"]="Bearing Footing - 900 x 300"},
    ["query.title_blocks"]=()=>new JsonObject(),
    ["dimension.create_linear"]=()=>new JsonObject{["view_uid"]=McpUid,["first_uid"]="60f91daf-3dd7-4283-a86d-24137b73f3da-00000e01",["second_uid"]="60f91daf-3dd7-4283-a86d-24137b73f3da-00000e02"},
    ["tag.create"]=()=>new JsonObject{["view_uid"]=McpUid,["element_uid"]="60f91daf-3dd7-4283-a86d-24137b73f3da-00000e01",["head"]=new JsonArray(3000,500),["leader"]=true},
    ["schedule.create"]=()=>new JsonObject{["category"]="door",["fields"]=new JsonArray("Mark","Type"),["sort_by"]="Mark"},
    ["sheet.create"]=()=>new JsonObject{["number"]="A101",["name"]="Ground Floor"},
    ["sheet.place_view"]=()=>new JsonObject{["sheet_uid"]=McpUid,["view_uid"]="60f91daf-3dd7-4283-a86d-24137b73f3da-00000e01",["center"]=new JsonArray(420,297)},
    ["element.set_parameter"]=()=>new JsonObject{["element_uid"]=McpUid,["parameter"]=new JsonObject{["built_in"]="ALL_MODEL_INSTANCE_COMMENTS"},["value"]=new JsonObject{["kind"]="text",["text"]="checked"},["dry_run"]=true},
    ["element.delete"]=()=>new JsonObject{["element_uids"]=new JsonArray(McpUid),["dry_run"]=true},
    ["query.materials"]=()=>new JsonObject(),
    ["roof_type.create"]=()=>new JsonObject{["name"]="Cabin roof",["base_type"]="Basic Roof - Generic 300mm",["layers"]=new JsonArray(new JsonObject{["function"]="finish1",["material"]="Roofing - Tile",["thickness_mm"]=20},new JsonObject{["function"]="membrane",["material"]="Roofing - Membrane",["thickness_mm"]=0},new JsonObject{["function"]="structure",["material"]="Wood",["thickness_mm"]=150})},
    ["family.load"]=()=>new JsonObject{["path"]=@"C:\ProgramData\Autodesk\RVT 2025\Libraries\English\Structural Columns\Wood\M_Timber-Column.rfa"},
    ["family_instance.place"]=()=>new JsonObject{["category"]="generic_model",["symbol"]="Focus Gyrofocus: Suspended",["level"]="Level 1",["point"]=new JsonArray(4000,15000),["offset_mm"]=1200},
    ["query.toposolid_types"]=()=>new JsonObject(),
    ["toposolid.create"]=()=>new JsonObject{["level"]="Level 1",["type"]="Toposolid 2000mm",["points"]=new JsonArray(new JsonArray(0,0,0),new JsonArray(30000,0,500),new JsonArray(30000,20000,4500),new JsonArray(0,20000,4000)),["comment"]="site"},
    ["toposolid.create_subdivision"]=()=>new JsonObject{["host_uid"]=McpUid,["outline"]=new JsonArray(new JsonArray(0,9000),new JsonArray(30000,9000),new JsonArray(30000,10500),new JsonArray(0,10500)),["material"]="Gravel"},
    ["wall.set_profile"]=()=>new JsonObject{["wall_uid"]=McpUid,["profile"]=new JsonArray(new JsonArray(0,0),new JsonArray(6000,0),new JsonArray(6000,2700),new JsonArray(3000,5200),new JsonArray(0,2700))},
    ["query.fill_patterns"]=()=>new JsonObject{["target"]="model"},
    ["material.create"]=()=>new JsonObject{["name"]="Chapa negra",["color"]=new JsonArray(45,47,50),["base_material"]="Metal Deck"},
    ["material.set"]=()=>new JsonObject{["material"]="Chapa negra",["color"]=new JsonArray(30,31,33)},
    ["wall_type.create"]=()=>new JsonObject{["name"]="Muro piedra 600 + revestimiento lenga",["base_type"]="Generic - 200mm",["layers"]=new JsonArray(new JsonObject{["function"]="structure",["material"]="Piedra local",["thickness_mm"]=550},new JsonObject{["function"]="thermal_air",["material"]="Air",["thickness_mm"]=25},new JsonObject{["function"]="finish2",["material"]="Madera lenga",["thickness_mm"]=25})},
    ["floor_type.create"]=()=>new JsonObject{["name"]="Piso lenga sobre platea",["base_type"]="Generic 150mm",["layers"]=new JsonArray(new JsonObject{["function"]="finish1",["material"]="Madera lenga",["thickness_mm"]=22},new JsonObject{["function"]="structure",["material"]="Concrete",["thickness_mm"]=150})},
    ["element.change_type"]=()=>new JsonObject{["element_uid"]=McpUid,["type"]="Muro piedra 600 + revestimiento lenga"},
    ["view.export_image"]=()=>new JsonObject{["view"]="{3D}",["pixel_size"]=1600,["format"]="png",["output_name"]="fachada-norte"},
    ["document.save"]=()=>new JsonObject(),
    ["direct_shape.create"]=()=>new JsonObject{["category"]="site",["shape"]=new JsonObject{["kind"]="extrusion",["level"]="Level 1",["outline"]=new JsonArray(new JsonArray(0,0),new JsonArray(1200,0),new JsonArray(1200,800),new JsonArray(0,800)),["height_mm"]=900},["name"]="boulder 3"},
    ["roof.create_extrusion"]=()=>new JsonObject{["level"]="Level 1",["type"]="Chapa curva",["plane"]=new JsonObject{["start"]=new JsonArray(0,0),["end"]=new JsonArray(6000,0)},["profile"]=new JsonArray(new JsonObject{["kind"]="arc",["start"]=new JsonArray(-300,2700),["end"]=new JsonArray(6300,2700),["through"]=new JsonArray(3000,3800)}),["extrusion_start_mm"]=-300,["extrusion_end_mm"]=10300},
    ["element.move"]=()=>new JsonObject{["element_uids"]=new JsonArray(McpUid),["vector_mm"]=new JsonArray(1000,0,0)},
    ["element.copy"]=()=>new JsonObject{["element_uids"]=new JsonArray(McpUid),["vector_mm"]=new JsonArray(0,0,3000)},
    ["element.rotate"]=()=>new JsonObject{["element_uids"]=new JsonArray(McpUid),["center"]=new JsonArray(0,0),["angle_deg"]=90},
    ["element.mirror"]=()=>new JsonObject{["element_uids"]=new JsonArray(McpUid),["axis"]=new JsonObject{["start"]=new JsonArray(0,0),["end"]=new JsonArray(0,1000)},["keep_original"]=true},
};
foreach(var (tool,makeArgs) in v2Writes) sampleArgs[RevitCommandCatalogue.All.Single(spec=>spec.ToolName==tool).Name]=makeArgs;
Check(RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).All(spec=>sampleArgs.ContainsKey(spec.Name)),"sample arguments cover every v1 and v2 command");
Check(RevitCommandCatalogue.Elements.Select(spec=>spec.Name).SequenceEqual(new[]{"query.elements","query.inspect"}) && RevitCommandCatalogue.Elements.All(spec=>sampleArgs.ContainsKey(spec.Name)),"sample arguments cover the P1-02 element queries");
// P1-13: revit_query_elements advertises the optional pagination cursor; a bad cursor is refused before the pipe.
var elementsTool=tools.Single(t=>t!["name"]!.GetValue<string>()=="revit_query_elements")!;
Check(elementsTool["inputSchema"]!["properties"]!["args"]!["properties"]!["cursor"]?["type"]?.GetValue<string>()=="string" && elementsTool["inputSchema"]!["properties"]!["args"]!["properties"]!["cursor"]!["maxLength"]!.GetValue<int>()==RevitElementQueryCursor.MaxChars,"revit_query_elements inputSchema has the optional string cursor");
var cursorBefore=forwardedLines.Count;
var badCursor=Guarded("revit_query_elements",new JsonObject{["category"]="wall",["cursor"]="%%%"},McpReal);
var foreignCursor=Guarded("revit_query_elements",new JsonObject{["category"]="wall",["cursor"]=RevitElementQueryCursor.Encode("11111111-2222-3333-4444-555555555555-0001fd0b",RevitElementQueryCursor.FilterDigest("OST_Walls",null,Array.Empty<string>()),7)},McpReal);
Check(IsError(badCursor) && FailureCode(badCursor)==RevitCommandCodes.CursorMalformed && IsError(foreignCursor) && FailureCode(foreignCursor)==RevitCommandCodes.CursorForeignDocument && forwardedLines.Count==cursorBefore,"revit_query_elements refuses a malformed or foreign cursor with its typed code before the pipe");
var goodCursor=Guarded("revit_query_elements",new JsonObject{["category"]="wall",["cursor"]=RevitElementQueryCursor.Encode(McpUid,RevitElementQueryCursor.FilterDigest("OST_Walls",null,Array.Empty<string>()),7)},McpReal);
Check(IsError(goodCursor) && FailureCode(goodCursor)==RevitCommandCodes.NoResponse && forwardedLines.Count==cursorBefore+1,"revit_query_elements forwards a cursor issued for this document and filter");
// P1-16: revit_query_elements gains the optional boolean all. all:true pages the listing with RevitElementQueryPaging.All
// over the same transport (each page through RevitCommandService.Run, like the CLI revit-command --all); the text is the
// ElementQueryAllResult JSON, byte-equal to the CLI --all stdout. Absent (or false), the call keeps today's bytes.
Check(elementsTool["inputSchema"]!["properties"]!["all"]?["type"]?.GetValue<string>()=="boolean" && Required("revit_query_elements")=="pipe_name,operation_id,document,args"
    && elementsTool["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="read" && elementsTool["annotations"]?["readOnlyHint"]?.GetValue<bool>()==true,"revit_query_elements inputSchema has the optional boolean all; permission level read and readOnlyHint unchanged");
Check(revitTools.Where(t=>t!["name"]!.GetValue<string>()!="revit_query_elements").All(t=>t!["inputSchema"]!["properties"]!["all"]==null),"no other revit_* tool advertises all");
var allModel=Enumerable.Range(0,23).Select(i=>3000L+i*7).ToArray();
var allMode="page";
var allFat=false;
var allLock=new object();
void AllSet(string mode,bool fat=false) { lock(allLock) { allMode=mode; allFat=fat; } }
var allForwarded=new List<string>();
string AllUid(long id)=>"60f91daf-3dd7-4283-a86d-24137b73f3da-"+id.ToString("x8",System.Globalization.CultureInfo.InvariantCulture);
// The fake add-in: pages allModel (walls) with the real paging rule and cursors; in loop mode every cursor page answers
// no rows and the very cursor it was sent (a next_cursor that does not move).
string AllReply(string wire) {
    lock(allLock) {
        allForwarded.Add(wire);
        string command;
        using(var wireDocument=JsonDocument.Parse(wire)) command=wireDocument.RootElement.GetProperty("Command").GetRawText();
        var envelope=RevitCommandProtocol.Parse(command);
        var queryArgs=(ElementQueryArgs)envelope.Args;
        var (selected,truncated)=RevitElementQueryRules.Page(allModel,queryArgs.Limit,queryArgs.Cursor?.After);
        var rows=selected.Select(id=>new ObservedQueryElement(id,AllUid(id),"Walls","OST_Walls"){TypeName=(allFat ? new string('x',1000) : "T")+id.ToString(System.Globalization.CultureInfo.InvariantCulture)}).ToArray();
        var readback=RevitElementQueryJson.Elements(envelope.Document.UniqueId,queryArgs,null,allModel.Length,truncated,rows,selected);
        if(allMode=="loop" && queryArgs.Cursor is not null) {
            var looped=Node(readback).AsObject();
            looped["elements"]=new JsonArray(); looped["returned"]=0; looped["truncated"]=true; looped["truncated_reason"]="limit";
            looped["next_cursor"]=Node(command)["args"]!["cursor"]!.GetValue<string>();
            readback=looped.ToJsonString();
        }
        return RevitCommandResponse.Success(new RevitCommandReceipt(envelope.OperationId,envelope.Command,envelope.Document.UniqueId,envelope.RequestSha256,false),Array.Empty<string>(),Array.Empty<string>(),Array.Empty<string>(),readback,Array.Empty<string>()).ToJson();
    }
}
string? AllTransport(string pipe,string line)=>AllReply(line);
int AllSent() { lock(allLock) return allForwarded.Count; }
JsonObject AllEnvelope(JsonObject queryArgs)=>new(){["protocol_version"]=1,["operation_id"]="op-p1-16",["document"]=new JsonObject{["path"]=McpReal,["unique_id"]=McpUid},["command"]="query.elements",["args"]=queryArgs.DeepClone()};
JsonObject AllCall(McpServer target,string pipe,JsonObject queryArgs,JsonNode? all) {
    var arguments=new JsonObject{["pipe_name"]=pipe,["operation_id"]="op-p1-16",["document"]=new JsonObject{["path"]=McpReal,["unique_id"]=McpUid},["args"]=queryArgs.DeepClone()};
    if(all is not null) arguments["all"]=all.DeepClone();
    return Node(target.Handle(Request(16,"tools/call",new JsonObject{["name"]="revit_query_elements",["arguments"]=arguments})) ?? throw new Exception("no response")).AsObject();
}
const string AllInProcessPipe="bim-agentic-os-2025-1616";
var allServer=new McpServer(RevitCommandCatalogue.All,RevitCommandPolicy.None,AllTransport,null);
JsonObject Walls(int limit)=>new(){["category"]="wall",["limit"]=limit};
string Direct(JsonObject queryArgs,int maxPages=RevitElementQueryPaging.MaxPages,int maxElements=RevitElementQueryPaging.MaxElements,long maxBytes=RevitElementQueryPaging.MaxListingBytes)=>
    RevitElementQueryPaging.All(AllEnvelope(queryArgs).ToJsonString(),new RevitServiceTransport(AllInProcessPipe,RevitCommandPolicy.None,AllTransport),maxPages,maxElements,maxBytes).ToJson();
var allBefore=AllSent();
var paged=AllCall(allServer,AllInProcessPipe,Walls(5),true);
var pagedSent=AllSent()-allBefore;
var pagedJson=Node(Text(paged));
Check(!IsError(paged) && HasType(paged) && pagedJson["status"]!.GetValue<string>()=="succeeded" && pagedJson["code"]!.GetValue<string>()=="ok" && pagedJson["pages"]!.GetValue<int>()==5 && pagedJson["matched"]!.GetValue<int>()==23 && pagedJson["returned"]!.GetValue<int>()==23
    && pagedJson["elements"]!.AsArray().Select(e=>e!["id"]!.GetValue<long>()).SequenceEqual(allModel) && pagedSent==5,"revit_query_elements all:true pages every wall (limit 5: five pipe requests) and concatenates the listing | "+Text(paged));
allBefore=AllSent();
var pagedDirect=Direct(Walls(5));
Check(Text(paged)==pagedDirect && AllSent()-allBefore==5,"revit_query_elements all:true text is byte-equal to RevitElementQueryPaging.All over RevitServiceTransport on the same transport");
Check(allForwarded.Skip(allForwarded.Count-5).Select(l=>Node(l)["Command"]!).All(c=>c["command"]!.GetValue<string>()=="query.elements" && c["args"]!["limit"]!.GetValue<int>()==5)
    && Node(allForwarded[^5])["Command"]!["args"]!["cursor"]==null && allForwarded.Skip(allForwarded.Count-4).All(l=>Node(l)["Command"]!["args"]!["cursor"]?.GetValue<string>() is {Length:>0}),"revit_query_elements all:true sends only query.elements with the same limit, the first page without a cursor and every next one with the reply's next_cursor");
allBefore=AllSent();
var single=AllCall(allServer,AllInProcessPipe,Walls(5),null);
var singleSent=AllSent()-allBefore;
var singleFalse=AllCall(allServer,AllInProcessPipe,Walls(5),false);
var singleDirect=RevitCommandService.Run(AllInProcessPipe,AllEnvelope(Walls(5)).ToJsonString(),RevitCommandPolicy.None,AllTransport);
Check(!IsError(single) && singleSent==1 && Text(single)==singleDirect.Line && Text(singleFalse)==Text(single) && !IsError(singleFalse) && Node(Text(single))["readback"]?["next_cursor"] is not null,"revit_query_elements without all (or all:false) keeps today's bytes: one request, the single-page RevitCommandService.Run line");
AllSet("loop");
var loop=AllCall(allServer,AllInProcessPipe,Walls(5),true);
Check(IsError(loop) && FailureCode(loop)==RevitElementQueryPagingCodes.CursorLoop && Text(loop)==Direct(Walls(5)) && Node(Text(loop))["elements"]!.AsArray().Count==0 && Node(Text(loop))["returned"]!.GetValue<int>()==0,"revit_query_elements all:true reports cursor_loop (isError, no partial elements), byte-equal to the core paging | "+Text(loop));
AllSet("page");
var allPages=allModel;
allModel=Enumerable.Range(0,RevitElementQueryPaging.MaxPages+1).Select(i=>5000L+i).ToArray();
allBefore=AllSent();
var tooManyPages=AllCall(allServer,AllInProcessPipe,Walls(1),true);
var tooManyPagesSent=AllSent()-allBefore;
var tooManyPagesDirect=Direct(Walls(1));
Check(IsError(tooManyPages) && FailureCode(tooManyPages)==RevitElementQueryPagingCodes.PageLimitExceeded && Text(tooManyPages)==tooManyPagesDirect && tooManyPagesSent==RevitElementQueryPaging.MaxPages && Node(Text(tooManyPages))["pages"]!.GetValue<int>()==RevitElementQueryPaging.MaxPages && Node(Text(tooManyPages))["elements"]!.AsArray().Count==0,"revit_query_elements all:true stops at the 1000-page cap with page_limit_exceeded (isError, no further page sent, no partial elements)");
allModel=allPages;
var capped=new McpServer(RevitCommandCatalogue.All,RevitCommandPolicy.None,AllTransport,null){QueryAllCaps=(2,RevitElementQueryPaging.MaxElements,RevitElementQueryPaging.MaxListingBytes)};
var cappedPages=AllCall(capped,AllInProcessPipe,Walls(5),true);
Check(IsError(cappedPages) && FailureCode(cappedPages)==RevitElementQueryPagingCodes.PageLimitExceeded && Text(cappedPages)==Direct(Walls(5),maxPages:2),"revit_query_elements all:true with a 2-page cap reports page_limit_exceeded, byte-equal to the core paging");
var cappedElements=new McpServer(RevitCommandCatalogue.All,RevitCommandPolicy.None,AllTransport,null){QueryAllCaps=(RevitElementQueryPaging.MaxPages,7,RevitElementQueryPaging.MaxListingBytes)};
var tooManyElements=AllCall(cappedElements,AllInProcessPipe,Walls(5),true);
Check(IsError(tooManyElements) && FailureCode(tooManyElements)==RevitElementQueryPagingCodes.ListingTooLarge && Text(tooManyElements)==Direct(Walls(5),maxElements:7) && Node(Text(tooManyElements))["elements"]!.AsArray().Count==0,"revit_query_elements all:true over the element cap reports listing_too_large (isError, no partial elements) | "+Text(tooManyElements));
var cappedBytes=new McpServer(RevitCommandCatalogue.All,RevitCommandPolicy.None,AllTransport,null){QueryAllCaps=(RevitElementQueryPaging.MaxPages,RevitElementQueryPaging.MaxElements,100)};
var tooManyBytes=AllCall(cappedBytes,AllInProcessPipe,Walls(5),true);
Check(IsError(tooManyBytes) && FailureCode(tooManyBytes)==RevitElementQueryPagingCodes.ListingTooLarge && Text(tooManyBytes)==Direct(Walls(5),maxBytes:100),"revit_query_elements all:true over the byte cap reports listing_too_large");
var defaultCaps=new McpServer(RevitCommandCatalogue.All,RevitCommandPolicy.None,AllTransport,null);
Check(defaultCaps.QueryAllCaps==(RevitElementQueryPaging.MaxPages,RevitElementQueryPaging.MaxElements,McpServer.DefaultQueryAllBytes) && McpServer.DefaultQueryAllBytes==32L*1024*1024
    && defaultCaps.QueryAllDeadline==McpServer.DefaultQueryAllDeadline && McpServer.DefaultQueryAllDeadline==RevitCommandService.DefaultReadTimeout,"the MCP paging caps default to the core page and element caps, a 32 MiB element-JSON cap (below the CLI's 256 MiB) and a 1800 s overall deadline");
// SF2: a 40000-row listing of about 48 MB of element JSON: listing_too_large at the MCP default 32 MiB cap, while the
// core (CLI) default 256 MiB cap lists it; byte-equal to the core paging with that cap.
allPages=allModel;
allModel=Enumerable.Range(0,40000).Select(i=>100000L+i).ToArray();
AllSet("page",fat:true);
var overMcpCap=AllCall(defaultCaps,AllInProcessPipe,Walls(5000),true);
var underCliCap=RevitElementQueryPaging.All(AllEnvelope(Walls(5000)).ToJsonString(),new RevitServiceTransport(AllInProcessPipe,RevitCommandPolicy.None,AllTransport));
Check(IsError(overMcpCap) && FailureCode(overMcpCap)==RevitElementQueryPagingCodes.ListingTooLarge && Node(Text(overMcpCap))["elements"]!.AsArray().Count==0
    && Text(overMcpCap)==Direct(Walls(5000),maxBytes:McpServer.DefaultQueryAllBytes) && underCliCap.IsSuccess && underCliCap.Elements.Count==40000,"revit_query_elements all:true over the default 32 MiB cap reports listing_too_large (the CLI's 256 MiB cap lists it) | "+Node(Text(overMcpCap))["detail"]);
AllSet("page");
allModel=allPages;
// SF1: an overall deadline on an injected clock; each page takes one minute, the deadline is three: the fourth page is
// refused before it is sent, as query_all_deadline_exceeded in the listing shape.
var allClock=new DateTimeOffset(2026,11,11,0,0,0,TimeSpan.Zero);
var slowServer=new McpServer(RevitCommandCatalogue.All,RevitCommandPolicy.None,(pipe,line)=>{ lock(allLock) allClock=allClock.AddMinutes(1); return AllReply(line); },null){QueryAllClock=()=>{ lock(allLock) return allClock; },QueryAllDeadline=TimeSpan.FromMinutes(3)};
allBefore=AllSent();
var lateListing=AllCall(slowServer,AllInProcessPipe,Walls(5),true);
Check(IsError(lateListing) && FailureCode(lateListing)==McpServer.QueryAllDeadlineExceeded && McpServer.QueryAllDeadlineExceeded=="query_all_deadline_exceeded" && Node(Text(lateListing))["pages"]!.GetValue<int>()==3
    && Node(Text(lateListing))["elements"]!.AsArray().Count==0 && AllSent()-allBefore==3,"revit_query_elements all:true past its overall deadline refuses the next page with query_all_deadline_exceeded (isError, no partial elements) | "+Text(lateListing));
var quickServer=new McpServer(RevitCommandCatalogue.All,RevitCommandPolicy.None,(pipe,line)=>{ lock(allLock) allClock=allClock.AddMinutes(1); return AllReply(line); },null){QueryAllClock=()=>{ lock(allLock) return allClock; },QueryAllDeadline=TimeSpan.FromMinutes(5)};
Check(!IsError(AllCall(quickServer,AllInProcessPipe,Walls(5),true)),"revit_query_elements all:true within its overall deadline lists every page");
// Optional 3: like a single read, all:true never reads the owner policy files (a malformed one does not refuse it).
var brokenPolicy=new McpServer(RevitCommandCatalogue.All,()=>throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid,"policy","malformed"),AllTransport,null);
Check(Text(AllCall(brokenPolicy,AllInProcessPipe,Walls(5),true))==pagedDirect,"revit_query_elements all:true does not read the owner policy (a malformed owner file does not refuse it; the CLI reads it)");
allBefore=AllSent();
var allBadCursor=AllCall(allServer,AllInProcessPipe,new JsonObject{["category"]="wall",["cursor"]="%%%"},true);
Check(IsError(allBadCursor) && FailureCode(allBadCursor)==RevitCommandCodes.CursorMalformed && Node(Text(allBadCursor))["pages"]!.GetValue<int>()==0 && AllSent()==allBefore,"revit_query_elements all:true refuses a malformed cursor in the listing shape before the pipe");
var allBadPipe=AllCall(allServer,"not-a-process-pipe",Walls(5),true);
Check(IsError(allBadPipe) && Text(allBadPipe)==ElementQueryAllResult.Refusal(RevitCommandCodes.ExplicitProcessPipeRequired,"pipe_name","bim-agentic-os-2025-<pid> required").ToJson() && AllSent()==allBefore,"revit_query_elements all:true refuses a non-process pipe in the listing shape before the pipe");
var allSurrogate=Node(allServer.Handle("{\"jsonrpc\":\"2.0\",\"id\":16,\"method\":\"tools/call\",\"params\":{\"name\":\"revit_query_elements\",\"arguments\":{\"pipe_name\":\"\\ud800\",\"operation_id\":\"op-p1-16\",\"document\":{\"path\":\"C:\\\\x.rvt\",\"unique_id\":\""+McpUid+"\"},\"args\":{\"category\":\"wall\"},\"all\":true}}}")!).AsObject();
var allSurrogateJson=Node(Text(allSurrogate));
Check(IsError(allSurrogate) && allSurrogateJson["code"]!.GetValue<string>()==RevitCommandCodes.EnvelopeInvalid && allSurrogateJson["pages"]!.GetValue<int>()==0 && allSurrogateJson["returned"]!.GetValue<int>()==0
    && allSurrogateJson["elements"]!.AsArray().Count==0 && allSurrogateJson.AsObject().ContainsKey("matched") && AllSent()==allBefore,"revit_query_elements all:true with a lone surrogate is envelope_invalid in the listing shape (pages 0) | "+Text(allSurrogate));
var allNotBool=AllCall(allServer,AllInProcessPipe,Walls(5),"yes");
Check(ErrorCode(allNotBool)==-32602 && allNotBool["error"]!["message"]!.GetValue<string>()=="invalid_params: all must be boolean" && AllSent()==allBefore,"revit_query_elements all must be a JSON boolean (-32602)");
var inspectAll=Node(allServer.Handle(Request(16,"tools/call",new JsonObject{["name"]="revit_query_inspect",["arguments"]=new JsonObject{["pipe_name"]=AllInProcessPipe,["operation_id"]="op-p1-16",["document"]=new JsonObject{["path"]=McpReal,["unique_id"]=McpUid},["args"]=sampleArgs["query.inspect"](),["all"]=true}}))!).AsObject();
Check(ErrorCode(inspectAll)==-32602 && inspectAll["error"]!["message"]!.GetValue<string>()=="invalid_params: unexpected argument all" && AllSent()==allBefore,"all is not an argument of other revit_* tools (-32602)");
// The real CLI and the MCP server on the same fake add-in process pipe (one connection per page; the next listening
// instance exists before the current one is disposed, like tests/integration/fake-revit-command-pipe.ps1).
// On Unix a .NET pipe is a Unix domain socket under Path.GetTempPath(), whose path must fit 108 characters; verify.ps1
// points TMPDIR at a long evidence directory, so this section (and the CLI it starts, which inherits the environment)
// uses /tmp/ and restores TMPDIR afterwards.
var allTmpDir=Environment.GetEnvironmentVariable("TMPDIR");
if(!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("TMPDIR","/tmp/");
var allStop=new CancellationTokenSource();
Thread? allPipeThread=null;
try {
var allPipeName="bim-agentic-os-2025-"+Random.Shared.Next(100000000,999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
System.IO.Pipes.NamedPipeServerStream NewAllListener()=>new(allPipeName,System.IO.Pipes.PipeDirection.InOut,2,System.IO.Pipes.PipeTransmissionMode.Byte,System.IO.Pipes.PipeOptions.Asynchronous);
var allListener=NewAllListener();
allPipeThread=new Thread(()=>{
    while(!allStop.IsCancellationRequested) {
        var current=allListener;
        try {
            current.WaitForConnectionAsync(allStop.Token).GetAwaiter().GetResult();
            allListener=NewAllListener();
            using var reader=new StreamReader(current,new System.Text.UTF8Encoding(false),false,4096,leaveOpen:true);
            var request=reader.ReadLine();
            bool stall;
            lock(allLock) stall=allMode=="stall" && request is not null && Node(request)["Command"]?["args"]?["cursor"] is not null;
            if(stall) { lock(allLock) allForwarded.Add(request!); reader.ReadLine(); }
            else if(request is not null) {
                current.Write(new System.Text.UTF8Encoding(false).GetBytes(AllReply(request)+"\n"));
                current.Flush();
                if(OperatingSystem.IsWindows()) current.WaitForPipeDrain();
            }
        } catch(OperationCanceledException) { current.Dispose(); break; }
        catch(Exception) { }
        finally { current.Dispose(); }
    }
    allListener.Dispose();
}){IsBackground=true};
allPipeThread.Start();
var pipedServer=new McpServer(RevitCommandPolicy.None);
var allEnvelopePath=Input("p1-16-envelope.json",AllEnvelope(Walls(5)).ToJsonString());
allBefore=AllSent();
var pipedAll=AllCall(pipedServer,allPipeName,Walls(5),true);
var pipedAllSent=AllSent()-allBefore;
var cliAll=Cli("revit-command",allEnvelopePath,allPipeName,"--all");
Check(!IsError(pipedAll) && Node(Text(pipedAll))["pages"]!.GetValue<int>()==5 && Node(Text(pipedAll))["returned"]!.GetValue<int>()==23 && pipedAllSent==5 && Text(pipedAll)==cliAll && Text(pipedAll)==pagedDirect,"revit_query_elements all:true on the fake process pipe is byte-equal to the CLI revit-command --all stdout | "+Text(pipedAll)+" | "+cliAll);
var pipedSingle=AllCall(pipedServer,allPipeName,Walls(5),null);
var cliSingle=Cli("revit-command",allEnvelopePath,allPipeName);
Check(!IsError(pipedSingle) && Text(pipedSingle)==cliSingle && Text(pipedSingle)==Text(single),"revit_query_elements without all on the fake process pipe is byte-equal to the CLI revit-command stdout (today's bytes)");
AllSet("loop");
var pipedLoop=AllCall(pipedServer,allPipeName,Walls(5),true);
var cliLoop=Cli("revit-command",allEnvelopePath,allPipeName,"--all");
AllSet("page");
Check(IsError(pipedLoop) && FailureCode(pipedLoop)==RevitElementQueryPagingCodes.CursorLoop && Text(pipedLoop)==cliLoop,"revit_query_elements all:true cursor_loop on the fake process pipe is byte-equal to the CLI --all stdout");
var cliBadPipe=Cli("revit-command",allEnvelopePath,"not-a-process-pipe","--all");
Check(Text(allBadPipe)==cliBadPipe,"revit_query_elements all:true pipe refusal is byte-equal to the CLI --all early refusal");
// SF1 on the real pipe transport: a stalled page is cut at the remaining overall deadline, not the 1800 s page timeout.
var deadlinePiped=new McpServer(RevitCommandCatalogue.All,RevitCommandPolicy.None,null,null){QueryAllDeadline=TimeSpan.FromSeconds(3)};
AllSet("stall");
var stallWatch=System.Diagnostics.Stopwatch.StartNew();
var stalled=AllCall(deadlinePiped,allPipeName,Walls(5),true);
stallWatch.Stop();
AllSet("page");
Check(IsError(stalled) && FailureCode(stalled)==McpServer.QueryAllDeadlineExceeded && Node(Text(stalled))["pages"]!.GetValue<int>()==1 && Node(Text(stalled))["elements"]!.AsArray().Count==0 && stallWatch.Elapsed<TimeSpan.FromSeconds(60),"revit_query_elements all:true on the pipe ends a stalled page at the overall deadline with query_all_deadline_exceeded | "+Text(stalled)+" in "+stallWatch.ElapsedMilliseconds+" ms");
} finally {
    allStop.Cancel();
    allPipeThread?.Join(TimeSpan.FromSeconds(10));
    if(!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("TMPDIR",allTmpDir);
}
// P1-20: the server option --read-timeout-seconds n (whole seconds 1-86400) is the real pipe read timeout. Through the
// real Release Bim.Mcp.dll (verify.ps1 builds interfaces/mcp first): a bad option is a typed refusal on stderr (the
// RevitCommandResponse shape, field args[i], exit 1) before stdin or any pipe is read, with nothing on stdout; with
// --read-timeout-seconds 1 a revit_* call on a stalled add-in pipe (StalledAddIn, tests/revit-commands/PipeReadTimeout.cs)
// is pipe_read_timeout after 1000 ms; without the option stdout is today's bytes.
var mcpDll=Path.Combine(root,"interfaces","mcp","bin","Release","net8.0","Bim.Mcp.dll");
if(!File.Exists(mcpDll)) throw new Exception("MCP binary missing: "+mcpDll+" (build interfaces/mcp --configuration Release first, as scripts/verify.ps1 does)");
(int Exit,string Out,string Err,long Ms) RunMcp(string stdin,params string[] arguments) {
    var start=new System.Diagnostics.ProcessStartInfo(dotnet){RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,
        StandardInputEncoding=new System.Text.UTF8Encoding(false),StandardOutputEncoding=new System.Text.UTF8Encoding(false),StandardErrorEncoding=new System.Text.UTF8Encoding(false)};
    start.ArgumentList.Add(mcpDll);
    foreach(var argument in arguments) start.ArgumentList.Add(argument);
    var watch=System.Diagnostics.Stopwatch.StartNew();
    using var process=System.Diagnostics.Process.Start(start) ?? throw new Exception("MCP process did not start: "+dotnet);
    var stdout=process.StandardOutput.ReadToEndAsync();
    var stderr=process.StandardError.ReadToEndAsync();
    try { process.StandardInput.Write(stdin); process.StandardInput.Close(); } catch(IOException) { }
    if(!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("MCP server timed out: "+string.Join(" ",arguments)); }
    process.WaitForExit();
    watch.Stop();
    return (process.ExitCode,stdout.Result.TrimEnd('\r','\n'),stderr.Result.TrimEnd('\r','\n'),watch.ElapsedMilliseconds);
}
const string McpUsage="usage: Bim.Mcp.dll [--read-timeout-seconds n]";
string McpRefusal(string code,int index,string detail)=>RevitCommandResponse.Failure(code,"args["+index.ToString(System.Globalization.CultureInfo.InvariantCulture)+"]",detail+"; "+McpUsage).ToJson();
Check(new McpServer(RevitCommandPolicy.None).ReadTimeout==RevitCommandService.DefaultReadTimeout && new McpServer(RevitCommandPolicy.None){ReadTimeout=TimeSpan.FromSeconds(5)}.ReadTimeout==TimeSpan.FromSeconds(5),"McpServer.ReadTimeout defaults to the 1800 s core default and takes another value");
foreach(var badTimeout in new[]{TimeSpan.Zero,TimeSpan.FromSeconds(-1),Timeout.InfiniteTimeSpan,RevitCommandService.MaxReadTimeout+TimeSpan.FromTicks(1)}) {
    var refusedTimeout=false;
    try { _=new McpServer(RevitCommandPolicy.None){ReadTimeout=badTimeout}; } catch(ArgumentOutOfRangeException) { refusedTimeout=true; }
    Check(refusedTimeout,"McpServer.ReadTimeout "+badTimeout+" is refused (RevitCommandService range)");
}
Check(McpServer.ParseOptions(Array.Empty<string>(),out var noOptionTimeout)==null && noOptionTimeout==RevitCommandService.DefaultReadTimeout
    && McpServer.ParseOptions(new[]{"--read-timeout-seconds","86400"},out var maxOptionTimeout)==null && maxOptionTimeout==RevitCommandService.MaxReadTimeout
    && McpServer.ParseOptions(new[]{"--read-timeout-seconds","1"},out var minOptionTimeout)==null && minOptionTimeout==TimeSpan.FromSeconds(1),"McpServer.ParseOptions: none keeps the default; 1 and 86400 seconds are accepted");
Check(RevitCommandCodes.ReadTimeoutInvalid=="read_timeout_invalid" && RevitCommandCodes.McpOptionUnknown=="mcp_option_unknown" && RevitCommandCodes.ClientSide.Contains(RevitCommandCodes.ReadTimeoutInvalid)
    && RevitCommandCodes.ClientSide.Contains(RevitCommandCodes.McpOptionUnknown),"read_timeout_invalid and mcp_option_unknown are client-side RevitCommandCodes");
var toolsList=Request(30,"tools/list",null)+"\n";
// P1-22: the MCP refusal set is the CLI's (tests/cli/RevitCommandReadTimeoutTests.cs): signed, padded and overflowing too.
foreach(var (label,value) in new[]{("zero","0"),("negative","-1"),("over 24 h","86401"),("fractional","1.5"),("not a number","abc"),("empty",""),("signed","+5"),("padded"," 5"),("overflowing","99999999999999999999")}) {
    var refused=RunMcp(toolsList,"--read-timeout-seconds",value);
    Check(refused.Exit==1 && refused.Out.Length==0 && refused.Err==McpRefusal("read_timeout_invalid",1,"--read-timeout-seconds takes whole seconds from 1 to 86400, got "+value),
        "Bim.Mcp --read-timeout-seconds "+label+" is refused read_timeout_invalid on stderr before serving | exit "+refused.Exit+" out "+refused.Out+" err "+refused.Err);
}
var mcpNoValue=RunMcp(toolsList,"--read-timeout-seconds");
Check(mcpNoValue.Exit==1 && mcpNoValue.Out.Length==0 && mcpNoValue.Err==McpRefusal("read_timeout_invalid",0,"--read-timeout-seconds needs a value"),"Bim.Mcp --read-timeout-seconds without a value is refused at the flag | "+mcpNoValue.Err);
var mcpUnknown=RunMcp(toolsList,"--bogus");
Check(mcpUnknown.Exit==1 && mcpUnknown.Out.Length==0 && mcpUnknown.Err==McpRefusal("mcp_option_unknown",0,"unknown option --bogus"),"Bim.Mcp refuses an unknown option by name | "+mcpUnknown.Err);
var mcpRepeated=RunMcp(toolsList,"--read-timeout-seconds","5","--read-timeout-seconds","6");
Check(mcpRepeated.Exit==1 && mcpRepeated.Out.Length==0 && mcpRepeated.Err==McpRefusal("mcp_option_unknown",2,"repeated option --read-timeout-seconds"),"Bim.Mcp refuses a repeated option at the repeat | "+mcpRepeated.Err);
Check(McpServer.ParseOptions(new[]{"--read-timeout-seconds","0030"},out var paddedZeroTimeout)==null && paddedZeroTimeout==TimeSpan.FromSeconds(30),"McpServer.ParseOptions accepts 0030 as 30 s, as revit-command does");
foreach(var value in new[]{"+5"," 5","99999999999999999999","5 ","\u0665"}) {
    Check(McpServer.ParseOptions(new[]{"--read-timeout-seconds",value},out var refusedValueTimeout)==McpRefusal("read_timeout_invalid",1,"--read-timeout-seconds takes whole seconds from 1 to 86400, got "+value) && refusedValueTimeout==RevitCommandService.DefaultReadTimeout,
        "McpServer.ParseOptions refuses --read-timeout-seconds "+value.Replace("\u0665","<arabic-indic 5>")+" read_timeout_invalid and keeps the default");
}
// P1-22: one option grammar. McpServer.ParseOptions parses no value itself: it calls the value parser and refusal format of
// interfaces/cli/RevitCommandOptions.cs (RevitOptionGrammar), linked into the server and its test projects, so the
// accepted grammar, the range and the refusal lines of revit-command and Bim.Mcp cannot drift.
{
    var serverSource=File.ReadAllText(Path.Combine(root,"interfaces","mcp","McpServer.cs"));
    // The ParseOptions body: from its signature to the brace matching the first '{' after it (no layout assumption).
    var p122Start=serverSource.IndexOf("static string? ParseOptions(",StringComparison.Ordinal);
    var p122Open=p122Start<0 ? -1 : serverSource.IndexOf('{',p122Start);
    var p122End=-1;
    for(int p122At=Math.Max(p122Open,0),p122Depth=0;p122Open>=0 && p122At<serverSource.Length;p122At++) {
        if(serverSource[p122At]=='{') p122Depth++;
        else if(serverSource[p122At]=='}' && --p122Depth==0) { p122End=p122At+1; break; }
    }
    var parseBody=p122End<0 ? "" : serverSource[p122Start..p122End];
    Check(parseBody.Length>0 && parseBody.Contains("RevitOptionGrammar.ReadTimeout(",StringComparison.Ordinal),"McpServer.ParseOptions reads --read-timeout-seconds through the shared RevitOptionGrammar.ReadTimeout");
    var ownGrammar=new[]{"TryParse","NumberStyles","PipeTransportWithReadTimeout","takes whole seconds","needs a value","unknown option","repeated option","\"args[\"","Failure("}.Where(token=>parseBody.Contains(token,StringComparison.Ordinal)).ToArray();
    Check(parseBody.Length>0 && ownGrammar.Length==0,"McpServer.ParseOptions has no value grammar, range or refusal text of its own ["+string.Join(", ",ownGrammar)+"]");
    Check(!serverSource.Contains("OptionRefusal(",StringComparison.Ordinal),"McpServer.cs keeps no private option refusal helper");
    // Each project compiles the shared file itself (a Compile Include resolving to interfaces/cli/RevitCommandOptions.cs,
    // either path separator), read as XML so attribute order and layout do not matter.
    var p122Shared=Path.GetFullPath(Path.Combine(root,"interfaces","cli","RevitCommandOptions.cs"));
    foreach(var p122Project in new[]{Path.Combine(root,"interfaces","mcp","Bim.Mcp.csproj"),Path.Combine(root,"tests","mcp","Bim.Mcp.Tests.csproj"),
        Path.Combine(root,"tests","revit-commands","Bim.RevitCommands.Tests.csproj"),Path.Combine(root,"tests","cli","Bim.Cli.Tests.csproj")}) {
        var p122Dir=Path.GetDirectoryName(p122Project)!;
        var p122Linked=System.Xml.Linq.XDocument.Load(p122Project).Descendants().Where(e=>e.Name.LocalName=="Compile")
            .Select(e=>(string?)e.Attribute("Include")).Where(include=>include is not null)
            .Any(include=>string.Equals(Path.GetFullPath(Path.Combine(p122Dir,include!.Replace('\\','/').Replace('/',Path.DirectorySeparatorChar))),p122Shared,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Check(p122Linked,Path.GetFileName(p122Project)+" compiles interfaces/cli/RevitCommandOptions.cs (shared source, not a copy)");
    }
}
// Pinned literally, independent of RevitCommandResponse.Failure and its escaping: the padded value " 5".
Check(McpServer.ParseOptions(new[]{"--read-timeout-seconds"," 5"},out _)=="""{"status":"failed","code":"read_timeout_invalid","field":"args[1]","detail":"--read-timeout-seconds takes whole seconds from 1 to 86400, got  5; usage: Bim.Mcp.dll [--read-timeout-seconds n]","created":[],"modified":[],"deleted":[],"readback":null,"receipt":null,"warnings":[]}""",
    "McpServer.ParseOptions refuses --read-timeout-seconds ' 5' with the literal pinned line");
// The server exposes only --read-timeout-seconds: --deadline-seconds is an unknown server option (MCP deadline sharing is P1-23).
Check(McpServer.ParseOptions(new[]{"--deadline-seconds","5"},out var p122DeadlineTimeout)==McpRefusal("mcp_option_unknown",0,"unknown option --deadline-seconds") && p122DeadlineTimeout==RevitCommandService.DefaultReadTimeout,
    "McpServer.ParseOptions refuses --deadline-seconds as mcp_option_unknown (the server has no deadline option)");
var mcpPlain=RunMcp(toolsList);
var mcpFlagged=RunMcp(toolsList,"--read-timeout-seconds","86400");
Check(mcpPlain.Exit==0 && Node(mcpPlain.Out)["id"]!.GetValue<int>()==30 && mcpFlagged.Exit==0 && mcpFlagged.Out==mcpPlain.Out && mcpFlagged.Err.Length==0,"Bim.Mcp with --read-timeout-seconds 86400 serves the same bytes as without it");
var stallTmpDir=Environment.GetEnvironmentVariable("TMPDIR");
if(!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("TMPDIR","/tmp/");
try {
    var stallPipe="bim-agentic-os-2025-"+Random.Shared.Next(100000000,999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
    using var stallAddIn=new StalledAddIn(stallPipe);
    var levelsCall=Request(31,"tools/call",new JsonObject{["name"]="revit_query_levels",["arguments"]=new JsonObject{["pipe_name"]=stallPipe,["operation_id"]="op-p1-20",["document"]=new JsonObject{["path"]=McpReal,["unique_id"]=McpUid},["args"]=new JsonObject()}})+"\n";
    var mcpStalled=RunMcp(levelsCall,"--read-timeout-seconds","1");
    var stalledText=mcpStalled.Exit==0 ? Text(Node(mcpStalled.Out).AsObject()) : "";
    var stalledResponse=stalledText.Length>0 ? RevitCommandResponse.Parse(stalledText) : null;
    Check(stalledResponse is {Code:RevitCommandCodes.PipeReadTimeout,Field:"pipe_name",Detail:{} mcpStallDetail} && mcpStallDetail.StartsWith("no reply within 1000 ms; read-only",StringComparison.Ordinal) && IsError(Node(mcpStalled.Out).AsObject()),
        "Bim.Mcp --read-timeout-seconds 1: a revit_* call on a stalled add-in pipe is pipe_read_timeout after 1000 ms | "+mcpStalled.Out+" "+mcpStalled.Err);
    Check(mcpStalled.Ms>=1000 && mcpStalled.Ms<30000 && stallAddIn.WaitForRequests(1) && stallAddIn.Received.Count==1,"the stalled MCP call is sent once and ends within the bound | "+mcpStalled.Ms+" ms");
    // all:true: a page's read timeout is the server read timeout while that is below the time left of the 1800 s overall
    // deadline, so a stalled page ends with the page's pipe_read_timeout (listing shape), not after the deadline.
    var allStallCall=Request(32,"tools/call",new JsonObject{["name"]="revit_query_elements",["arguments"]=new JsonObject{["pipe_name"]=stallPipe,["operation_id"]="op-p1-20-all",["document"]=new JsonObject{["path"]=McpReal,["unique_id"]=McpUid},["args"]=Walls(5),["all"]=true}})+"\n";
    var mcpAllStalled=RunMcp(allStallCall,"--read-timeout-seconds","1");
    var allStalledResponse=mcpAllStalled.Exit==0 ? Node(mcpAllStalled.Out).AsObject() : null;
    var allStalledListing=allStalledResponse is null ? null : Node(Text(allStalledResponse)).AsObject();
    Check(allStalledResponse is not null && IsError(allStalledResponse) && allStalledListing is not null && allStalledListing["status"]!.GetValue<string>()=="failed"
        && allStalledListing["code"]!.GetValue<string>()==RevitCommandCodes.PipeReadTimeout && allStalledListing["pages"]!.GetValue<int>()==0 && allStalledListing["elements"]!.AsArray().Count==0
        && allStalledListing["detail"]?.GetValue<string>() is {} allStallDetail && allStallDetail.Contains("1000 ms",StringComparison.Ordinal),
        "Bim.Mcp --read-timeout-seconds 1: revit_query_elements all:true on a stalled pipe is pipe_read_timeout after 1000 ms in the listing shape | "+mcpAllStalled.Out+" "+mcpAllStalled.Err);
    Check(mcpAllStalled.Ms>=1000 && mcpAllStalled.Ms<30000 && stallAddIn.WaitForRequests(2) && stallAddIn.Received.Count==2,"the stalled all:true page is sent once and ends within the bound | "+mcpAllStalled.Ms+" ms");
    // Reverse: an overall deadline below the server read timeout cuts the stalled page to the time left (query_all_deadline_exceeded).
    var cutServer=new McpServer(RevitCommandCatalogue.All,RevitCommandPolicy.None,null,null){ReadTimeout=TimeSpan.FromSeconds(120),QueryAllDeadline=TimeSpan.FromSeconds(2)};
    var cutWatch=System.Diagnostics.Stopwatch.StartNew();
    var cutStalled=AllCall(cutServer,stallPipe,Walls(5),true);
    cutWatch.Stop();
    Check(IsError(cutStalled) && FailureCode(cutStalled)==McpServer.QueryAllDeadlineExceeded && Node(Text(cutStalled))["pages"]!.GetValue<int>()==0 && cutWatch.Elapsed<TimeSpan.FromSeconds(30)
        && stallAddIn.WaitForRequests(3) && stallAddIn.Received.Count==3,"a 2 s all:true deadline under a 120 s server read timeout ends the stalled page with query_all_deadline_exceeded | "+Text(cutStalled)+" in "+cutWatch.ElapsedMilliseconds+" ms");
} finally {
    if(!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("TMPDIR",stallTmpDir);
}
// P9-02: the structure commands are generated tools with the same read/write split and guards.
Check(RevitCommandCatalogue.Structure.Select(spec=>spec.Name).SequenceEqual(new[]{"query.grids","query.structural_types","grid.create","structural_column.create","structural_beam.create","foundation.create_isolated","foundation.create_wall"}) && RevitCommandCatalogue.Structure.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover the P9-02 structure commands, all in the catalogue enumeration");
foreach(var (tool,readOnly) in new[]{("revit_query_grids",true),("revit_query_structural_types",true),("revit_grid_create",false),("revit_structural_column_create",false),("revit_structural_beam_create",false),("revit_foundation_create_isolated",false),("revit_foundation_create_wall",false)}) {
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()==tool);
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==readOnly && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()==(readOnly?"read":"write-copies"),"structure tool "+tool+" is listed with readOnlyHint "+readOnly);
    var spec=RevitCommandCatalogue.All.SingleOrDefault(c=>c.ToolName==tool);
    if(spec is null) continue;
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,sampleArgs[spec.Name](),McpReal);
    if(readOnly) Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1,tool+" is read-only: forwarded to the pipe even on a real project");
    else Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" on a real project is refused document_not_enabled before the pipe");
}
// P4-02: the documentation commands are generated tools with the same read/write split and guards.
Check(RevitCommandCatalogue.Documentation.Select(spec=>spec.Name).SequenceEqual(new[]{"query.title_blocks","dimension.create_linear","tag.create","schedule.create","sheet.create","sheet.place_view"}) && RevitCommandCatalogue.Documentation.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover the P4-02 documentation commands, all in the catalogue enumeration");
foreach(var (tool,readOnly) in new[]{("revit_query_title_blocks",true),("revit_dimension_create_linear",false),("revit_tag_create",false),("revit_schedule_create",false),("revit_sheet_create",false),("revit_sheet_place_view",false)}) {
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()==tool);
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==readOnly && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()==(readOnly?"read":"write-copies"),"documentation tool "+tool+" is listed with readOnlyHint "+readOnly);
    var spec=RevitCommandCatalogue.All.SingleOrDefault(c=>c.ToolName==tool);
    if(spec is null) continue;
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,sampleArgs[spec.Name](),McpReal);
    if(readOnly) Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1,tool+" is read-only: forwarded to the pipe even on a real project");
    else Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" on a real project is refused document_not_enabled before the pipe");
}
// P1-05: the modify and delete commands are generated write tools, flagged destructive, guarded even as dry runs.
Check(RevitCommandCatalogue.Modify.Select(spec=>spec.Name).SequenceEqual(new[]{"element.set_parameter","element.delete"}) && RevitCommandCatalogue.Modify.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover the P1-05 modify commands, all in the catalogue enumeration");
foreach(var tool in new[]{"revit_element_set_parameter","revit_element_delete"}) {
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()==tool);
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && listed?["annotations"]?["destructiveHint"]?.GetValue<bool>()==true && listed?["annotations"]?["idempotentHint"]?.GetValue<bool>()==true
        && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write-copies","modify tool "+tool+" is listed write-copies with destructiveHint true and idempotentHint true");
    var spec=RevitCommandCatalogue.All.SingleOrDefault(c=>c.ToolName==tool);
    if(spec is null) continue;
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,sampleArgs[spec.Name](),McpReal);
    Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" dry run on a real project is refused document_not_enabled before the pipe");
    var onCopy=Guarded(tool,sampleArgs[spec.Name](),McpCopy);
    Check(IsError(onCopy) && FailureCode(onCopy)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1 && Node(forwardedLines[^1])["Command"]?["args"]?["dry_run"]?.GetValue<bool>()==true,tool+" dry run on an enabled test copy is forwarded with dry_run true");
}
// chat-roof-gable: materials query and layered roof types are generated tools with the same read/write split and guards.
Check(RevitCommandCatalogue.Materials.Select(spec=>spec.Name).SequenceEqual(new[]{"query.materials","roof_type.create"}) && RevitCommandCatalogue.Materials.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover the materials commands, all in the catalogue enumeration");
foreach(var (tool,readOnly) in new[]{("revit_query_materials",true),("revit_roof_type_create",false)}) {
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()==tool);
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==readOnly && (readOnly || listed?["annotations"]?["destructiveHint"]?.GetValue<bool>()==false) && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()==(readOnly?"read":"write-copies"),"materials tool "+tool+" is listed with readOnlyHint "+readOnly+" (an added type is not destructive)");
    var spec=RevitCommandCatalogue.All.SingleOrDefault(c=>c.ToolName==tool);
    if(spec is null) continue;
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,sampleArgs[spec.Name](),McpReal);
    if(readOnly) Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1,tool+" is read-only: forwarded to the pipe even on a real project");
    else Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" on a real project is refused document_not_enabled before the pipe");
}
// chat-roof-gable: a gable footprint roof (sloped_edges) goes through the generated tool unchanged.
{
    var before=forwardedLines.Count;
    var gableArgs=new JsonObject{["level"]="Level 2",["type"]="Cabin roof",["boundary"]=new JsonArray(new JsonArray(0,0),new JsonArray(8000,0),new JsonArray(8000,6000),new JsonArray(0,6000)),["slope_deg"]=30,["sloped_edges"]=new JsonArray(0,2)};
    var gable=Guarded("revit_roof_create_footprint",gableArgs,McpCopy);
    Check(IsError(gable) && FailureCode(gable)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1 && Node(forwardedLines[^1])["Command"]?["args"]?["sloped_edges"]?.ToJsonString()=="[0,2]","revit_roof_create_footprint forwards a gable request with its sloped_edges");
    var gableInvalid=Guarded("revit_roof_create_footprint",new JsonObject{["level"]="Level 2",["type"]="Cabin roof",["boundary"]=Outline(),["sloped_edges"]=new JsonArray(0,2)},McpCopy);
    Check(IsError(gableInvalid) && FailureCode(gableInvalid)==RevitCommandCodes.ArgsInvalid && forwardedLines.Count==before+1,"sloped_edges without slope_deg is args_invalid before the pipe");
}
// Family commands: generated additive write tools, guarded like every write; family.load also checks its source.
Check(RevitCommandCatalogue.Families.Select(spec=>spec.Name).SequenceEqual(new[]{"family.load","family_instance.place"}) && RevitCommandCatalogue.Families.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover the family commands, all in the catalogue enumeration");
foreach(var tool in new[]{"revit_family_load","revit_family_instance_place"}) {
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()==tool);
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && listed?["annotations"]?["destructiveHint"]?.GetValue<bool>()==false && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write-copies","family tool "+tool+" is listed write-copies, additive (destructiveHint false)");
    var spec=RevitCommandCatalogue.All.SingleOrDefault(c=>c.ToolName==tool);
    if(spec is null) continue;
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,sampleArgs[spec.Name](),McpReal);
    Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" on a real project is refused document_not_enabled before the pipe");
}
var outsideLoad=Guarded("revit_family_load",new JsonObject{["path"]=@"C:\Users\owner\Downloads\Stove.rfa"},McpCopy);
Check(IsError(outsideLoad) && FailureCode(outsideLoad)==RevitCommandCodes.FamilySourceRefused,"revit_family_load from outside .bim/families and the Autodesk library is family_source_refused before the pipe");
// Site commands: the toposolid tools are generated with the same read/write split and copies-only guard.
Check(RevitCommandCatalogue.Site.Select(spec=>spec.Name).SequenceEqual(new[]{"query.toposolid_types","toposolid.create","toposolid.create_subdivision"}) && RevitCommandCatalogue.Site.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover the site commands, all in the catalogue enumeration");
foreach(var (tool,readOnly) in new[]{("revit_query_toposolid_types",true),("revit_toposolid_create",false),("revit_toposolid_create_subdivision",false)}) {
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()==tool);
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==readOnly && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()==(readOnly?"read":"write-copies")
        && (readOnly || listed?["annotations"]?["destructiveHint"]?.GetValue<bool>()==false),"site tool "+tool+" is listed with readOnlyHint "+readOnly+(readOnly?"":" and destructiveHint false"));
    var spec=RevitCommandCatalogue.All.SingleOrDefault(c=>c.ToolName==tool);
    if(spec is null) continue;
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,sampleArgs[spec.Name](),McpReal);
    if(readOnly) Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1,tool+" is read-only: forwarded to the pipe even on a real project");
    else Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" on a real project is refused document_not_enabled before the pipe");
}
// wall.set_profile: a generated write tool that changes an existing wall (destructiveHint true), copies only.
Check(RevitCommandCatalogue.WallProfile.Select(spec=>spec.Name).SequenceEqual(new[]{"wall.set_profile"}) && RevitCommandCatalogue.WallProfile.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover wall.set_profile, in the catalogue enumeration");
{
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="revit_wall_set_profile");
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && listed?["annotations"]?["destructiveHint"]?.GetValue<bool>()==true && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write-copies","revit_wall_set_profile is listed write-copies with destructiveHint true");
    var before=forwardedLines.Count;
    var onReal=Guarded("revit_wall_set_profile",sampleArgs["wall.set_profile"](),McpReal);
    Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,"revit_wall_set_profile on a real project is refused document_not_enabled before the pipe");
    var onCopy=Guarded("revit_wall_set_profile",sampleArgs["wall.set_profile"](),McpCopy);
    Check(IsError(onCopy) && FailureCode(onCopy)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1 && Node(forwardedLines[^1])["Command"]?["args"]?["profile"]?.AsArray().Count==5,"revit_wall_set_profile on an enabled copy forwards the profile");
    var bowTie=Guarded("revit_wall_set_profile",new JsonObject{["wall_uid"]=McpUid,["profile"]=new JsonArray(new JsonArray(0,0),new JsonArray(6000,2700),new JsonArray(6000,0),new JsonArray(0,2700))},McpCopy);
    Check(IsError(bowTie) && FailureCode(bowTie)==RevitCommandCodes.BoundarySelfIntersecting && forwardedLines.Count==before+1,"a self-intersecting wall profile is refused before the pipe");
}
// P9-07 roof.create_extrusion: a generated additive write tool (destructiveHint false), copies only; the profile rules
// refuse a self-intersecting profile before the pipe.
Check(RevitCommandCatalogue.RoofExtrusion.Select(spec=>spec.Name).SequenceEqual(new[]{"roof.create_extrusion"}) && RevitCommandCatalogue.RoofExtrusion.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover roof.create_extrusion, in the catalogue enumeration");
{
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="revit_roof_create_extrusion");
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && listed?["annotations"]?["destructiveHint"]?.GetValue<bool>()==false && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write-copies","revit_roof_create_extrusion is listed write-copies with destructiveHint false");
    var before=forwardedLines.Count;
    var onReal=Guarded("revit_roof_create_extrusion",sampleArgs["roof.create_extrusion"](),McpReal);
    Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,"revit_roof_create_extrusion on a real project is refused document_not_enabled before the pipe");
    var onCopy=Guarded("revit_roof_create_extrusion",sampleArgs["roof.create_extrusion"](),McpCopy);
    Check(IsError(onCopy) && FailureCode(onCopy)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1 && Node(forwardedLines[^1])["Command"]?["args"]?["profile"]?.AsArray().Count==1,"revit_roof_create_extrusion on an enabled copy forwards the profile");
    var crossing=Guarded("revit_roof_create_extrusion",new JsonObject{["level"]="Level 1",["type"]="Chapa curva",["plane"]=new JsonObject{["start"]=new JsonArray(0,0),["end"]=new JsonArray(6000,0)},["profile"]=new JsonArray(new JsonObject{["kind"]="line",["start"]=new JsonArray(0,0),["end"]=new JsonArray(4000,0)},new JsonObject{["kind"]="line",["start"]=new JsonArray(4000,0),["end"]=new JsonArray(4000,2000)},new JsonObject{["kind"]="line",["start"]=new JsonArray(4000,2000),["end"]=new JsonArray(2000,-1000)}),["extrusion_start_mm"]=0,["extrusion_end_mm"]=10000},McpCopy);
    Check(IsError(crossing) && FailureCode(crossing)==RevitCommandCodes.ArgsInvalid && forwardedLines.Count==before+1,"a self-intersecting extrusion roof profile is refused before the pipe");
}
// P1-09 element.move / element.copy: generated write tools, copies only; a move changes existing elements
// (destructiveHint true), a copy only adds (destructiveHint false); a zero vector is refused before the pipe.
Check(RevitCommandCatalogue.Transform.Select(spec=>spec.Name).SequenceEqual(new[]{"element.move","element.copy"}) && RevitCommandCatalogue.Transform.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover element.move and element.copy, in the catalogue enumeration");
foreach(var (tool,command,destructive) in new[]{("revit_element_move","element.move",true),("revit_element_copy","element.copy",false)}) {
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()==tool);
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && listed?["annotations"]?["destructiveHint"]?.GetValue<bool>()==destructive && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write-copies",tool+" is listed write-copies with destructiveHint "+destructive);
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,sampleArgs[command](),McpReal);
    Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" on a real project is refused document_not_enabled before the pipe");
    var onCopy=Guarded(tool,sampleArgs[command](),McpCopy);
    Check(IsError(onCopy) && FailureCode(onCopy)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1 && Node(forwardedLines[^1])["Command"]?["args"]?["vector_mm"]?.AsArray().Count==3,tool+" on an enabled copy forwards the vector");
    var zero=Guarded(tool,new JsonObject{["element_uids"]=new JsonArray(McpUid),["vector_mm"]=new JsonArray(0,0,0)},McpCopy);
    Check(IsError(zero) && FailureCode(zero)==RevitCommandCodes.LengthInvalid && forwardedLines.Count==before+1,tool+": a zero vector is length_invalid before the pipe");
}
// P1-10 element.rotate / element.mirror: generated write tools, copies only, both destructiveHint true (a rotation
// turns existing elements; a mirror without keep_original turns the originals); a full-turn angle and a zero-length
// axis are refused before the pipe.
Check(RevitCommandCatalogue.RotateMirror.Select(spec=>spec.Name).SequenceEqual(new[]{"element.rotate","element.mirror"}) && RevitCommandCatalogue.RotateMirror.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover element.rotate and element.mirror, in the catalogue enumeration");
foreach(var (tool,command,invalidArgs,code) in new[]{("revit_element_rotate","element.rotate",(JsonNode)new JsonObject{["element_uids"]=new JsonArray(McpUid),["center"]=new JsonArray(0,0),["angle_deg"]=360},RevitCommandCodes.AngleInvalid),("revit_element_mirror","element.mirror",new JsonObject{["element_uids"]=new JsonArray(McpUid),["axis"]=new JsonObject{["start"]=new JsonArray(0,0),["end"]=new JsonArray(0,0)},["keep_original"]=false},RevitCommandCodes.CurveTooShort)}) {
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()==tool);
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && listed?["annotations"]?["destructiveHint"]?.GetValue<bool>()==true && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write-copies",tool+" is listed write-copies with destructiveHint true");
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,sampleArgs[command](),McpReal);
    Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" on a real project is refused document_not_enabled before the pipe");
    var onCopy=Guarded(tool,sampleArgs[command](),McpCopy);
    Check(IsError(onCopy) && FailureCode(onCopy)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1 && Node(forwardedLines[^1])["Command"]?["command"]?.GetValue<string>()==command,tool+" on an enabled copy forwards the command");
    var bad=Guarded(tool,invalidArgs,McpCopy);
    Check(IsError(bad) && FailureCode(bad)==code && forwardedLines.Count==before+1,tool+": "+code+" before the pipe");
}
// chat-materials-types: fill patterns, materials, layered wall/floor types and type changes are generated tools;
// material.set and element.change_type change existing elements (destructiveHint true), the rest only add.
Check(RevitCommandCatalogue.MaterialTypes.Select(spec=>spec.Name).SequenceEqual(new[]{"query.fill_patterns","material.create","material.set","wall_type.create","floor_type.create","element.change_type"}) && RevitCommandCatalogue.MaterialTypes.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover the material type commands, all in the catalogue enumeration");
foreach(var (tool,readOnly,destructive) in new[]{("revit_query_fill_patterns",true,false),("revit_material_create",false,false),("revit_material_set",false,true),("revit_wall_type_create",false,false),("revit_floor_type_create",false,false),("revit_element_change_type",false,true)}) {
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()==tool);
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==readOnly && (readOnly || listed?["annotations"]?["destructiveHint"]?.GetValue<bool>()==destructive) && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()==(readOnly?"read":"write-copies"),"material type tool "+tool+" is listed with readOnlyHint "+readOnly+(readOnly?"":" and destructiveHint "+destructive));
    var spec=RevitCommandCatalogue.All.SingleOrDefault(c=>c.ToolName==tool);
    if(spec is null) continue;
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,sampleArgs[spec.Name](),McpReal);
    if(readOnly) Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1,tool+" is read-only: forwarded to the pipe even on a real project");
    else Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,tool+" on a real project is refused document_not_enabled before the pipe");
}
{
    var before=forwardedLines.Count;
    var onCopy=Guarded("revit_wall_type_create",sampleArgs["wall_type.create"](),McpCopy);
    Check(IsError(onCopy) && FailureCode(onCopy)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1 && Node(forwardedLines[^1])["Command"]?["args"]?["layers"]?.AsArray().Count==3,"revit_wall_type_create on an enabled copy forwards the layers");
    var badColor=Guarded("revit_material_create",new JsonObject{["name"]="Chapa negra",["color"]=new JsonArray(45,47,300)},McpCopy);
    Check(IsError(badColor) && FailureCode(badColor)==RevitCommandCodes.ArgsInvalid && forwardedLines.Count==before+1,"a color channel above 255 is args_invalid before the pipe");
}
// chat-export-save: view.export_image is read-only (forwarded on any document, image files only under .bim/exports);
// document.save is a destructive write gated like every write.
Check(RevitCommandCatalogue.ExportSave.Select(spec=>spec.Name).SequenceEqual(new[]{"view.export_image","document.save"}) && RevitCommandCatalogue.ExportSave.All(spec=>sampleArgs.ContainsKey(spec.Name) && RevitCommandCatalogue.All.Contains(spec)),"sample arguments cover view.export_image and document.save, in the catalogue enumeration");
{
    var exportTool=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="revit_view_export_image");
    Check(exportTool?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && exportTool?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="read","revit_view_export_image keeps permission level read but is not listed readOnlyHint (it writes image files)");
    Check(RevitCommandCatalogue.WritesFiles("view.export_image") && !RevitCommandCatalogue.WritesFiles("document.save") && !RevitCommandCatalogue.WritesFiles("query.levels") && !RevitCommandCatalogue.WritesFiles(null),"only view.export_image writes files besides the model");
    var saveTool=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="revit_document_save");
    Check(saveTool?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && saveTool?["annotations"]?["destructiveHint"]?.GetValue<bool>()==true && saveTool?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write-copies","revit_document_save is listed as a destructive write with permission level write-copies");
    var before=forwardedLines.Count;
    var exportReal=Guarded("revit_view_export_image",sampleArgs["view.export_image"](),McpReal);
    Check(IsError(exportReal) && FailureCode(exportReal)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1 && Node(forwardedLines[^1])["Command"]?["args"]?["output_name"]?.GetValue<string>()=="fachada-norte","revit_view_export_image is forwarded on a real project with its arguments");
    var saveReal=Guarded("revit_document_save",new JsonObject(),McpReal);
    Check(IsError(saveReal) && FailureCode(saveReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before+1,"revit_document_save on a real project is refused document_not_enabled before the pipe");
    var badName=Guarded("revit_view_export_image",new JsonObject{["view"]="{3D}",["output_name"]="../fuera"},McpReal);
    Check(IsError(badName) && FailureCode(badName)==RevitCommandCodes.ArgsInvalid && forwardedLines.Count==before+1,"an output name with a path is args_invalid before the pipe");
    var styledReal=Guarded("revit_view_export_image",new JsonObject{["view"]="{3D}",["visual_style"]="realistic"},McpReal);
    Check(IsError(styledReal) && FailureCode(styledReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before+1,"view.export_image with visual_style opens a transaction: refused document_not_enabled on a real project before the pipe");
    var styledCopy=Guarded("revit_view_export_image",new JsonObject{["view"]="{3D}",["visual_style"]="realistic"},McpCopy);
    Check(IsError(styledCopy) && FailureCode(styledCopy)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+2,"view.export_image with visual_style on an enabled copy is forwarded");
    before=forwardedLines.Count;
    var saveCopy=Guarded("revit_document_save",new JsonObject(),McpCopy);
    Check(IsError(saveCopy) && FailureCode(saveCopy)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1,"revit_document_save on an enabled copy is forwarded");
    // P1-08: document.save_copy is a non-destructive write (it never overwrites); its target guard runs before the pipe.
    var copyTool=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="revit_document_save_copy");
    Check(copyTool?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && copyTool?["annotations"]?["destructiveHint"]?.GetValue<bool>()==false && copyTool?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write-copies","revit_document_save_copy is listed as a non-destructive write with permission level write-copies");
    before=forwardedLines.Count;
    var copyArgs=new JsonObject{["path"]=@"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test-copy.rvt"};
    var copyReal=Guarded("revit_document_save_copy",copyArgs.DeepClone(),McpReal);
    Check(IsError(copyReal) && FailureCode(copyReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,"revit_document_save_copy of a real project is refused document_not_enabled before the pipe");
    var copyName=Guarded("revit_document_save_copy",new JsonObject{["path"]=@"C:\Repo\.bim\native-runs\run-1\Copia.rvt"},McpCopy);
    Check(IsError(copyName) && FailureCode(copyName)==RevitCommandCodes.ArgsInvalid && forwardedLines.Count==before,"revit_document_save_copy to a name other than BIM-Agentic-Test*.rvt is args_invalid before the pipe");
    var copyNoRepo=Guarded("revit_document_save_copy",copyArgs.DeepClone(),McpCopy);
    Check(IsError(copyNoRepo) && FailureCode(copyNoRepo)==RevitCommandCodes.SaveCopyRefused && forwardedLines.Count==before,"revit_document_save_copy without a known repository is save_copy_refused before the pipe");
    // P9-08: document.save_as is a destructive write (it re-points the open document); its path, root and existence
    // guard runs before the pipe with typed codes.
    var saveAsTool=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="revit_document_save_as");
    Check(saveAsTool?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==false && saveAsTool?["annotations"]?["destructiveHint"]?.GetValue<bool>()==true && saveAsTool?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write-copies"
        && saveAsTool?["inputSchema"]?["properties"]?["args"]?["required"]?[0]?.GetValue<string>()=="path","revit_document_save_as is listed as a destructive write with permission level write-copies and a required path");
    var saveAsReal=Guarded("revit_document_save_as",new JsonObject{["path"]=@"C:\Projects\Real\Tower v2.rvt"},McpReal);
    Check(IsError(saveAsReal) && FailureCode(saveAsReal)==RevitCommandCodes.DocumentNotEnabled && forwardedLines.Count==before,"revit_document_save_as of a document outside the enabled roots is document_not_enabled before the pipe");
    foreach(var (label,path) in new[]{("a traversal",@"C:\Repo\.bim\native-runs\run-1\..\..\Tower.rvt"),("a UNC path",@"\\server\share\Tower.rvt"),("mixed separators",@"C:\Repo/.bim\native-runs\run-1\Tower.rvt"),("a device name",@"C:\Repo\.bim\native-runs\run-1\NUL.rvt"),("a .rfa name",@"C:\Repo\.bim\native-runs\run-1\Tower.rfa"),("an alternate data stream",@"C:\Repo\.bim\native-runs\run-1\Tower.rvt:x")}) {
        var bad=Guarded("revit_document_save_as",new JsonObject{["path"]=path},McpCopy);
        Check(IsError(bad) && FailureCode(bad)==RevitCommandCodes.ArgsInvalid && forwardedLines.Count==before,"revit_document_save_as with "+label+" is args_invalid before the pipe");
    }
    var saveAsOutside=Guarded("revit_document_save_as",new JsonObject{["path"]=@"C:\Projects\Real\Tower v2.rvt"},McpCopy);
    Check(IsError(saveAsOutside) && FailureCode(saveAsOutside)==RevitCommandCodes.SaveAsRefused && Node(Text(saveAsOutside))["detail"]!.GetValue<string>().StartsWith("outside_enabled_roots:",StringComparison.Ordinal) && forwardedLines.Count==before,"revit_document_save_as outside the enabled roots is save_as_refused outside_enabled_roots before the pipe");
    var saveAsMissing=Guarded("revit_document_save_as",new JsonObject{["path"]=@"C:\Repo\.bim\native-runs\run-404\Tower v2.rvt"},McpCopy);
    Check(IsError(saveAsMissing) && FailureCode(saveAsMissing)==RevitCommandCodes.SaveAsRefused && Node(Text(saveAsMissing))["detail"]!.GetValue<string>().StartsWith("folder_missing:",StringComparison.Ordinal) && forwardedLines.Count==before,"revit_document_save_as into a missing folder is save_as_refused folder_missing before the pipe");
    // P9-08 review: the tool stays write-copies on a test copy because a test copy may only be saved inside
    // .bim/native-runs; with an owner root enabled too, a test copy saved into it is refused before the pipe.
    var ownerServer=new McpServer(new RevitCommandPolicy(new[]{@"C:\Repo\.bim\native-runs",@"C:\Projects\Real"}),(pipe,line)=>{ forwardedLines.Add(line); return null; },new RevitFileSystemProbe(_=>false,_=>null));
    var escalate=Node(ownerServer.Handle(Request(12,"tools/call",new JsonObject{["name"]="revit_document_save_as",["arguments"]=new JsonObject{["pipe_name"]="bim-agentic-os-2025-4242",["operation_id"]="op-p9-08",["document"]=new JsonObject{["path"]=McpCopy,["unique_id"]=McpUid},["args"]=new JsonObject{["path"]=@"C:\Projects\Real\Tower v2.rvt"}}})) ?? throw new Exception("no response")).AsObject();
    Check(IsError(escalate) && FailureCode(escalate)==RevitCommandCodes.SaveAsRefused && Node(Text(escalate))["detail"]!.GetValue<string>().StartsWith("outside_copies_root:",StringComparison.Ordinal) && forwardedLines.Count==before
        && PermissionPolicy.RequiredLevel("document.save_as",McpCopy,copiesRoots)==PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("document.save_as",McpReal,copiesRoots)==PermissionLevel.WriteRealProject,
        "revit_document_save_as of a test copy into an owner-enabled root is save_as_refused outside_copies_root before the pipe, so write-copies stays truthful");
}
// chat-export-save: the policy is re-read when .bim/revit-enabled-roots.txt changes, so a folder the owner enables
// works without restarting the MCP server (and Claude).
{
    var reloadRepo=Path.Combine(Path.GetTempPath(),"mcp-reload-"+Guid.NewGuid().ToString("N"));
    try {
        Directory.CreateDirectory(Path.Combine(reloadRepo,".bim"));
        File.WriteAllText(Path.Combine(reloadRepo,"global.json"),"{}");
        var ownerFile=Path.Combine(reloadRepo,".bim","revit-enabled-roots.txt");
        var ownerFolder=Path.Combine(reloadRepo,"owner","ARQ");
        var ownerDocument=Path.Combine(ownerFolder,"Cabana","Cabana.rvt");
        var source=new RevitCommandPolicySource(reloadRepo);
        var reloadLines=new List<string>();
        var reloading=new McpServer(RevitCommandCatalogue.All,source.Current,(pipe,line)=>{ reloadLines.Add(line); return null; },new RevitFileSystemProbe(_=>false,_=>null));
        JsonObject Save()=>Node(reloading.Handle(Request(12,"tools/call",new JsonObject{["name"]="revit_document_save",["arguments"]=new JsonObject{["pipe_name"]="bim-agentic-os-2025-4242",["operation_id"]="op-save-1",["document"]=new JsonObject{["path"]=ownerDocument,["unique_id"]=McpUid},["args"]=new JsonObject()}})) ?? throw new Exception("no response")).AsObject();
        var refused=Save();
        Check(IsError(refused) && FailureCode(refused)==RevitCommandCodes.DocumentNotEnabled && reloadLines.Count==0,"before the owner enables the folder, a save there is document_not_enabled");
        File.WriteAllText(ownerFile,ownerFolder+"\n");
        var allowed=Save();
        Check(IsError(allowed) && FailureCode(allowed)==RevitCommandCodes.NoResponse && reloadLines.Count==1,"after the owner adds the folder to .bim/revit-enabled-roots.txt the same server forwards the save, without a restart");
        File.WriteAllText(ownerFile,"not/absolute\n");
        var malformedSave=Save();
        Check(IsError(malformedSave) && FailureCode(malformedSave)==RevitCommandCodes.AllowlistInvalid && reloadLines.Count==1,"a malformed owner file is reported as allowlist_invalid and enables no write");
        var read=Node(reloading.Handle(Request(13,"tools/call",new JsonObject{["name"]="revit_view_export_image",["arguments"]=new JsonObject{["pipe_name"]="bim-agentic-os-2025-4242",["operation_id"]="op-export-1",["document"]=new JsonObject{["path"]=ownerDocument,["unique_id"]=McpUid},["args"]=new JsonObject{["view"]="{3D}"}}}))!).AsObject();
        Check(IsError(read) && FailureCode(read)==RevitCommandCodes.NoResponse && reloadLines.Count==2,"read-only commands still work while the owner file is malformed");
        var styled=Node(reloading.Handle(Request(14,"tools/call",new JsonObject{["name"]="revit_view_export_image",["arguments"]=new JsonObject{["pipe_name"]="bim-agentic-os-2025-4242",["operation_id"]="op-export-2",["document"]=new JsonObject{["path"]=ownerDocument,["unique_id"]=McpUid},["args"]=new JsonObject{["view"]="{3D}",["visual_style"]="shaded"}}}))!).AsObject();
        Check(IsError(styled) && FailureCode(styled)==RevitCommandCodes.AllowlistInvalid && reloadLines.Count==2,"an export with visual_style needs the write policy: allowlist_invalid while the owner file is malformed");
        File.WriteAllText(ownerFile,"# fixed\n"+ownerFolder+"\n");
        var fixedSave=Save();
        Check(IsError(fixedSave) && FailureCode(fixedSave)==RevitCommandCodes.NoResponse && reloadLines.Count==3,"fixing the owner file enables the save again");
    } finally { if(Directory.Exists(reloadRepo)) Directory.Delete(reloadRepo,true); }
}
// P2-05: query.journal (the external edit journal, journal.read through the catalogue) is a read-only generated tool:
// readOnlyHint true, permission level read, forwarded on a real project; bad paging arguments are refused before the pipe.
{
    var listed=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="revit_query_journal");
    Check(listed?["annotations"]?["readOnlyHint"]?.GetValue<bool>()==true && listed?["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="read","revit_query_journal is listed with readOnlyHint true and permission level read");
    if(listed is not null) {
    var before=forwardedLines.Count;
    var onReal=Guarded("revit_query_journal",new JsonObject{["peek"]=true,["max_entries"]=10},McpReal);
    Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1 && Node(forwardedLines[^1])["Command"]?["args"]?["max_entries"]?.GetValue<int>()==10,"revit_query_journal is read-only: forwarded to the pipe even on a real project with its arguments");
    foreach(var bad in new[]{new JsonObject{["max_entries"]=0},new JsonObject{["max_entries"]=257},new JsonObject{["after_sequence"]=-1}}) {
        var refused=Guarded("revit_query_journal",bad,McpCopy);
        Check(IsError(refused) && FailureCode(refused)==RevitCommandCodes.ArgsInvalid && forwardedLines.Count==before+1,"revit_query_journal "+bad.ToJsonString()+" is args_invalid before the pipe");
    }
    }
}
foreach(var tool in new[]{"revit_query_elements","revit_query_inspect"}) {
    var listed=tools.Single(t=>t!["name"]!.GetValue<string>()==tool)!;
    Check(listed["annotations"]?["readOnlyHint"]?.GetValue<bool>()==true && listed["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="read",tool+" is listed read-only with permission level read");
}
foreach(var (tool,toolArgs) in new (string,Func<JsonNode>)[]{("revit_query_elements",sampleArgs["query.elements"]),("revit_query_inspect",sampleArgs["query.inspect"])}) {
    var before=forwardedLines.Count;
    var onReal=Guarded(tool,toolArgs(),McpReal);
    Check(IsError(onReal) && FailureCode(onReal)==RevitCommandCodes.NoResponse && forwardedLines.Count==before+1,tool+" is read-only: forwarded to the pipe even on a real project");
}
foreach(var spec in RevitCommandCatalogue.All.Where(spec=>sampleArgs.ContainsKey(spec.Name))) {
    var before=forwardedLines.Count;
    var forwarded=Guarded(spec.ToolName,sampleArgs[spec.Name](),McpCopy);
    var wire=forwardedLines.Count==before+1 ? Node(forwardedLines[^1]) : null;
    Check(IsError(forwarded) && FailureCode(forwarded)==RevitCommandCodes.NoResponse && wire?["Command"]?["command"]?.GetValue<string>()==spec.Name && wire["Command"]!["document"]!["path"]!.GetValue<string>()==McpCopy,spec.ToolName+" forwards one envelope with command "+spec.Name);
}

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

// P2-08 mirror_drift: the same engine as the CLI mirror-drift verb. The tool text equals the CLI output file byte for byte
// (and a refusal equals the CLI stdout); nothing is applied to the store; the tool is listed read-only.
{
    var driftFixtures=Path.Combine(root,"tests","mirror-journal","fixtures","drift");
    var committedText=File.ReadAllText(Path.Combine(driftFixtures,"committed.json"));
    var snapshotText=File.ReadAllText(Path.Combine(driftFixtures,"snapshot.json"));
    var committed=MirrorCommittedModel.Parse(committedText);
    const string driftDoc="60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    var driftStore=Path.Combine(cliDir,"drift-store");
    new ProjectStore(driftStore).Initialize(committed.Project);
    var storeBytes=File.ReadAllBytes(Path.Combine(driftStore,"project.json"));
    var drifted=Node(snapshotText).AsObject();
    foreach(var element in drifted["elements"]!.AsArray().ToArray())
        if((string?)element!["Id"]=="db98eeb5-3a66-7972-29a5-494ec813984a") { element!["GeometryMetres"]![0]=5.05; element!["GeometryMetres"]![2]=5.05; }
    var driftedText=drifted.ToJsonString();
    JsonObject Mirror(int revision=3,string document=driftDoc) => new JsonObject {
        ["document"]=new JsonObject{["path"]=@"C:\Repo\.bim\native-runs\p2-08\copy.rvt",["unique_id"]=document},
        ["core_project_id"]=committed.Project.Id.ToString("D"),
        ["core_revision"]=revision,
        ["map"]=Node(committedText)["map"]!.DeepClone(),
        ["wall_types"]=new JsonArray(new JsonObject{["thickness_metres"]=0.2,["type"]="Generic - 200mm"}),
        ["door_symbol"]="Single-Flush: 0915 x 2134mm",
    };
    var driftTool=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="mirror_drift");
    Check(driftTool is not null && driftTool["description"]!.GetValue<string>().Contains("never applies",StringComparison.Ordinal),"mirror_drift is listed and says it never applies anything");
    var driftCall=ToolCall("mirror_drift",new JsonObject{["store_path"]=driftStore,["snapshot"]=Node(driftedText),["mirror"]=Mirror()});
    var snapshotPath=Input("drift-snapshot.json",driftedText);
    var mirrorPath=Input("drift-mirror.json",Mirror().ToJsonString());
    var outPath=Path.Combine(cliDir,"drift-out.json");
    var cliSummary=Cli("mirror-drift",driftStore,snapshotPath,mirrorPath,outPath);
    Check(!IsError(driftCall) && HasType(driftCall) && File.Exists(outPath),"mirror_drift succeeds and the CLI wrote its output | "+cliSummary);
    Check(Text(driftCall)==File.ReadAllText(outPath,new System.Text.UTF8Encoding(false)),"mirror_drift text byte-equal to the CLI mirror-drift output file");
    using(var document=JsonDocument.Parse(Text(driftCall))) {
        var top=document.RootElement;
        Check(top.GetProperty("applied").GetBoolean()==false && top.GetProperty("report").GetProperty("summary").GetProperty("changed").GetInt32()==1
            && top.GetProperty("resolution").GetProperty("proposals").GetArrayLength()==1,"mirror_drift returns the changed wall with one proposal, not applied");
    }
    Check(File.ReadAllBytes(Path.Combine(driftStore,"project.json")).SequenceEqual(storeBytes),"mirror_drift applies nothing to the store");
    var stale=ToolCall("mirror_drift",new JsonObject{["store_path"]=driftStore,["snapshot"]=Node(driftedText),["mirror"]=Mirror(revision:4)});
    var staleCli=Cli("mirror-drift",driftStore,snapshotPath,Input("drift-mirror-stale.json",Mirror(revision:4).ToJsonString()),Path.Combine(cliDir,"drift-stale.json"));
    Check(IsError(stale) && JsonDocument.Parse(Text(stale)).RootElement.GetProperty("code").GetString()=="drift_resolve_report_stale" && Text(stale)==staleCli,"mirror_drift stale refusal is typed and byte-equal to the CLI stdout");
    var other="11111111-2222-3333-4444-555555555555-00000001";
    var mismatch=ToolCall("mirror_drift",new JsonObject{["store_path"]=driftStore,["snapshot"]=Node(driftedText),["mirror"]=Mirror(document:other)});
    var mismatchCli=Cli("mirror-drift",driftStore,snapshotPath,Input("drift-mirror-other.json",Mirror(document:other).ToJsonString()),Path.Combine(cliDir,"drift-other.json"));
    Check(IsError(mismatch) && JsonDocument.Parse(Text(mismatch)).RootElement.GetProperty("code").GetString()=="drift_document_mismatch" && Text(mismatch)==mismatchCli,"mirror_drift document mismatch is typed and byte-equal to the CLI stdout");
    var driftNoStore=ToolCall("mirror_drift",new JsonObject{["store_path"]=Path.Combine(cliDir,"no-drift-store"),["snapshot"]=Node(driftedText),["mirror"]=Mirror()});
    Check(IsError(driftNoStore) && JsonDocument.Parse(Text(driftNoStore)).RootElement.GetProperty("code").GetString()=="input_not_found" && !Directory.Exists(Path.Combine(cliDir,"no-drift-store")),"mirror_drift on a missing store is input_not_found and creates nothing");
    var badSnapshot=ToolCall("mirror_drift",new JsonObject{["store_path"]=driftStore,["snapshot"]=new JsonObject{["snapshot_version"]=2},["mirror"]=Mirror()});
    Check(IsError(badSnapshot) && JsonDocument.Parse(Text(badSnapshot)).RootElement.GetProperty("code").GetString()=="drift_snapshot_invalid","mirror_drift refuses a snapshot that is not a query.snapshot");
    // Review fixes: a malformed store and a duplicate mirror member are typed refusals, equal to the CLI.
    var malformedStore=Path.Combine(cliDir,"drift-driftMalformed-store"); Directory.CreateDirectory(malformedStore);
    var malformedProject=Node(JsonSerializer.Serialize(committed.Project)).AsObject(); malformedProject["Elements"]![1]!["GeometryMetres"]=null;
    File.WriteAllText(Path.Combine(malformedStore,"project.json"),"{\"Project\":"+malformedProject.ToJsonString()+",\"Receipts\":{}}");
    var driftMalformed=ToolCall("mirror_drift",new JsonObject{["store_path"]=malformedStore,["snapshot"]=Node(driftedText),["mirror"]=Mirror()});
    var driftMalformedCli=Cli("mirror-drift",malformedStore,snapshotPath,mirrorPath,Path.Combine(cliDir,"drift-driftMalformed.json"));
    Check(IsError(driftMalformed) && JsonDocument.Parse(Text(driftMalformed)).RootElement.GetProperty("code").GetString()=="drift_store_invalid" && Text(driftMalformed)==driftMalformedCli,"mirror_drift on a driftMalformed store is drift_store_invalid, byte-equal to the CLI | "+Text(driftMalformed));
    var mirrorText=Mirror().ToJsonString();
    var duplicateMirror="{\"core_revision\":2,"+mirrorText[1..];
    var duplicateLine="{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"mirror_drift\",\"arguments\":{\"store_path\":"+JsonSerializer.Serialize(driftStore)+",\"snapshot\":"+driftedText+",\"mirror\":"+duplicateMirror+"}}}";
    var duplicate=Call(duplicateLine);
    var duplicateCli=Cli("mirror-drift",driftStore,snapshotPath,Input("drift-mirror-dup.json",duplicateMirror),Path.Combine(cliDir,"drift-dup.json"));
    Check(IsError(duplicate) && JsonDocument.Parse(Text(duplicate)).RootElement.GetProperty("code").GetString()=="mirror_drift_request_invalid" && Text(duplicate)==duplicateCli,"mirror_drift refuses a duplicate core_revision, byte-equal to the CLI | "+Text(duplicate));
    Check(ErrorCode(ToolCall("mirror_drift",new JsonObject{["store_path"]=driftStore,["snapshot"]=Node(driftedText)}))==-32602,"mirror_drift without mirror returns -32602");
}

// P2-10 mirror_drift_select: the same engine as the CLI mirror-drift-select verb (MirrorDriftSelectTool). The tool text
// equals the CLI output file (the canonical P2-09 selection JSON) byte for byte, and a refusal equals the CLI stdout;
// nothing is applied to the store; the tool is listed read-only.
{
    var selectFixtures=Path.Combine(root,"tests","mirror-journal","fixtures","drift");
    var committedText=File.ReadAllText(Path.Combine(selectFixtures,"committed.json"));
    var snapshotText=File.ReadAllText(Path.Combine(selectFixtures,"snapshot.json"));
    var committed=MirrorCommittedModel.Parse(committedText);
    const string selectDoc="60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string w2="60f91daf-3dd7-4283-a86d-24137b73f3da-00001002";
    var selectStore=Path.Combine(cliDir,"select-store");
    new ProjectStore(selectStore).Initialize(committed.Project);
    var storeBytes=File.ReadAllBytes(Path.Combine(selectStore,"project.json"));
    string StoreListing()=>string.Join(",",Directory.GetFileSystemEntries(selectStore).Select(entry=>Path.GetFileName(entry)).Order(StringComparer.Ordinal));
    var storeListing=StoreListing();
    var drifted=Node(snapshotText).AsObject();
    foreach(var element in drifted["elements"]!.AsArray().ToArray())
        if((string?)element!["Id"]=="db98eeb5-3a66-7972-29a5-494ec813984a") { element!["GeometryMetres"]![0]=5.05; element!["GeometryMetres"]![2]=5.05; }
    var driftedText=drifted.ToJsonString();
    JsonObject Mirror(int revision=3) => new JsonObject {
        ["document"]=new JsonObject{["path"]=@"C:\Repo\.bim\native-runs\p2-10\copy.rvt",["unique_id"]=selectDoc},
        ["core_project_id"]=committed.Project.Id.ToString("D"),
        ["core_revision"]=revision,
        ["map"]=Node(committedText)["map"]!.DeepClone(),
        ["wall_types"]=new JsonArray(new JsonObject{["thickness_metres"]=0.2,["type"]="Generic - 200mm"}),
        ["door_symbol"]="Single-Flush: 0915 x 2134mm",
    };
    JsonObject Choices(string id,string action)=>new JsonObject{["choices"]=new JsonArray(new JsonObject{["proposal_id"]=id,["action"]=action})};
    JsonObject SelectCall(JsonObject mirror,JsonNode choices,string? store=null,string? snapshot=null)=>ToolCall("mirror_drift_select",new JsonObject{["store_path"]=store ?? selectStore,["snapshot"]=Node(snapshot ?? driftedText),["mirror"]=mirror,["choices"]=choices});
    string Code(JsonObject response)=>JsonDocument.Parse(Text(response)).RootElement.GetProperty("code").GetString() ?? "";

    var selectTool=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="mirror_drift_select");
    Check(selectTool is not null && selectTool["description"]!.GetValue<string>().Contains("never applies",StringComparison.Ordinal) && selectTool["description"]!.GetValue<string>().Contains("Read-only",StringComparison.Ordinal)
        && selectTool["description"]!.GetValue<string>().Contains("mirror-drift-select",StringComparison.Ordinal),"mirror_drift_select is listed, read-only, never applies, naming the CLI verb");
    Check(Required("mirror_drift_select")=="store_path,snapshot,mirror,choices","mirror_drift_select requires store_path, snapshot, mirror and choices");

    // Expected, straight from Core.
    var bindings=new RevitBindings(new RevitDocumentRef(@"C:\Repo\.bim\native-runs\p2-10\copy.rvt",selectDoc),new Dictionary<Guid,string>(),new[]{new RevitWallTypeBinding(0.2,"Generic - 200mm")},"Single-Flush: 0915 x 2134mm",null);
    var parsedSnapshot=MirrorDrift.ParseSnapshot(driftedText);
    var resolution=MirrorDriftResolve.Plan(MirrorDrift.Compare(parsedSnapshot,committed,(IReadOnlyList<MirrorJournalEntry>?)null,new MirrorDriftOptions()),parsedSnapshot,committed,bindings);
    var expected=MirrorDriftSelect.Select(resolution,new[]{new MirrorDriftChoice(w2,"adopt")}).ToJson();

    var ok=SelectCall(Mirror(),Choices(w2,"adopt"));
    var snapshotPath=Input("select-snapshot.json",driftedText);
    var mirrorPath=Input("select-mirror.json",Mirror().ToJsonString());
    var outPath=Path.Combine(cliDir,"select-out.json");
    var cliSummary=Cli("mirror-drift-select",selectStore,snapshotPath,mirrorPath,Input("select-choices.json",Choices(w2,"adopt").ToJsonString()),outPath);
    Check(!IsError(ok) && HasType(ok) && File.Exists(outPath),"mirror_drift_select succeeds and the CLI wrote its output | "+cliSummary+" | "+Text(ok));
    Check(Text(ok)==File.ReadAllText(outPath,new System.Text.UTF8Encoding(false)),"mirror_drift_select text byte-equal to the CLI mirror-drift-select output file");
    Check(Text(ok)==expected,"mirror_drift_select text byte-equal to MirrorDriftSelect.Select(..).ToJson() | "+Text(ok));
    using(var document=JsonDocument.Parse(Text(ok))) {
        var top=document.RootElement;
        Check(top.GetProperty("applied").GetBoolean()==false && top.GetProperty("requires_approval").GetBoolean() && top.GetProperty("operations").GetArrayLength()==1
            && top.GetProperty("restore_plan").GetProperty("steps").GetArrayLength()==0,"mirror_drift_select adopting the moved wall gives one BIM-IR operation and no restore step, not applied");
    }
    Check(File.ReadAllBytes(Path.Combine(selectStore,"project.json")).SequenceEqual(storeBytes) && StoreListing()==storeListing,"mirror_drift_select applies nothing to the store");

    // Refusals: typed, isError, byte-equal to the CLI stdout, no CLI output file.
    var unknownChoices=Choices("60f91daf-3dd7-4283-a86d-24137b73f3da-0000beef","restore");
    var unknown=SelectCall(Mirror(),unknownChoices);
    var unknownOut=Path.Combine(cliDir,"select-unknown.json");
    var unknownCli=Cli("mirror-drift-select",selectStore,snapshotPath,mirrorPath,Input("select-unknown-choices.json",unknownChoices.ToJsonString()),unknownOut);
    Check(IsError(unknown) && Code(unknown)=="drift_select_proposal_unknown" && Text(unknown)==unknownCli && !File.Exists(unknownOut),"mirror_drift_select unknown proposal is drift_select_proposal_unknown, byte-equal to the CLI | "+Text(unknown));
    var invalidChoices=Choices(w2,"apply");
    var invalidCall=SelectCall(Mirror(),invalidChoices);
    Check(IsError(invalidCall) && Code(invalidCall)=="drift_select_choice_invalid" && Text(invalidCall)==Cli("mirror-drift-select",selectStore,snapshotPath,mirrorPath,Input("select-invalidCall-choices.json",invalidChoices.ToJsonString()),Path.Combine(cliDir,"select-invalidCall.json")),
        "mirror_drift_select action other than adopt/restore is drift_select_choice_invalid, byte-equal to the CLI | "+Text(invalidCall));
    var stale=SelectCall(Mirror(revision:4),Choices(w2,"adopt"));
    var staleCli=Cli("mirror-drift-select",selectStore,snapshotPath,Input("select-mirror-stale.json",Mirror(revision:4).ToJsonString()),Input("select-stale-choices.json",Choices(w2,"adopt").ToJsonString()),Path.Combine(cliDir,"select-stale.json"));
    Check(IsError(stale) && Code(stale)=="drift_resolve_report_stale" && Text(stale)==staleCli,"mirror_drift_select stale mirror is drift_resolve_report_stale, byte-equal to the CLI");
    var shape=SelectCall(Mirror(),new JsonObject{["choices"]="all"});
    Check(IsError(shape) && Code(shape)=="drift_select_request_invalid","mirror_drift_select refuses a choices member that is not an array (drift_select_request_invalid)");
    var many=new JsonArray();
    for(var i=0;i<=MirrorDriftOptions.MaxFindingsLimit;i++) many.Add(new JsonObject{["proposal_id"]="x"+i.ToString(System.Globalization.CultureInfo.InvariantCulture),["action"]="adopt"});
    var tooMany=SelectCall(Mirror(),new JsonObject{["choices"]=many});
    Check(IsError(tooMany) && Code(tooMany)=="drift_select_choices_too_large","mirror_drift_select refuses more than 20000 choices (drift_select_choices_too_large)");
    var selectNoStore=SelectCall(Mirror(),Choices(w2,"adopt"),Path.Combine(cliDir,"no-select-store"));
    Check(IsError(selectNoStore) && Code(selectNoStore)=="input_not_found" && !Directory.Exists(Path.Combine(cliDir,"no-select-store")),"mirror_drift_select on a missing store is input_not_found and creates nothing");
    Check(ErrorCode(ToolCall("mirror_drift_select",new JsonObject{["store_path"]=selectStore,["snapshot"]=Node(driftedText),["mirror"]=Mirror()}))==-32602,"mirror_drift_select without choices returns -32602");
    Check(ErrorCode(ToolCall("mirror_drift_select",new JsonObject{["store_path"]=selectStore,["snapshot"]=Node(driftedText),["mirror"]=Mirror(),["choices"]=new JsonArray()}))==-32602,"mirror_drift_select with a non-object choices returns -32602");
    Check(File.ReadAllBytes(Path.Combine(selectStore,"project.json")).SequenceEqual(storeBytes),"mirror_drift_select refusals leave the store untouched");
    // ---- P2-10 review fixes: refusals byte-equal to the CLI stdout ----
    string CliSelect(string snapshotFile,string mirrorJson,string choicesJson,string name)=>
        Cli("mirror-drift-select",selectStore,snapshotFile,Input("select-"+name+"-mirror.json",mirrorJson),Input("select-"+name+"-choices.json",choicesJson),Path.Combine(cliDir,"select-"+name+"-out.json"));
    // SF1: no wall type bound, so restoring the moved wall is unsupported.
    var noTypes=Mirror(); noTypes.Remove("wall_types");
    var noTypesJson=noTypes.ToJsonString();
    var unsupported=SelectCall(noTypes,Choices(w2,"restore"));
    var unsupportedCli=CliSelect(snapshotPath,noTypesJson,Choices(w2,"restore").ToJsonString(),"unsupported");
    Check(IsError(unsupported) && Code(unsupported)=="drift_select_action_unsupported" && JsonDocument.Parse(Text(unsupported)).RootElement.GetProperty("proposal_id").GetString()==w2
        && Text(unsupported)==unsupportedCli && !File.Exists(Path.Combine(cliDir,"select-unsupported-out.json")),"mirror_drift_select unsupported restore is drift_select_action_unsupported with proposal_id, byte-equal to the CLI | "+Text(unsupported)+" | "+unsupportedCli);
    // SF2/SF3: strict choices shape, drift_select_request_invalid, byte-equal to the CLI.
    foreach(var (label,json) in new[]{("null choices","{\"choices\":null}"),("no choices member","{}"),("null choice","{\"choices\":[null]}"),
        ("null proposal_id","{\"choices\":[{\"proposal_id\":null,\"action\":\"adopt\"}]}"),("missing action","{\"choices\":[{\"proposal_id\":\""+w2+"\"}]}"),
        ("lone surrogate proposal_id","{\"choices\":[{\"proposal_id\":\"\\ud800\",\"action\":\"adopt\"}]}"),("choices not an array","{\"choices\":\"all\"}")}) {
        var requestLine="{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"tools/call\",\"params\":{\"name\":\"mirror_drift_select\",\"arguments\":{\"store_path\":"+JsonSerializer.Serialize(selectStore)+",\"snapshot\":"+driftedText+",\"mirror\":"+Mirror().ToJsonString()+",\"choices\":"+json+"}}}";
        var strictCall=Call(requestLine);
        var strictCli=CliSelect(snapshotPath,Mirror().ToJsonString(),json,"strict-"+label.Replace(' ','-'));
        Check(IsError(strictCall) && Code(strictCall)=="drift_select_request_invalid" && Text(strictCall)==strictCli,"mirror_drift_select "+label+" is drift_select_request_invalid, byte-equal to the CLI | "+Text(strictCall)+" | "+strictCli);
    }
    var manyCli=CliSelect(snapshotPath,Mirror().ToJsonString(),new JsonObject{["choices"]=many.DeepClone()}.ToJsonString(),"too-many");
    Check(Text(tooMany)==manyCli,"mirror_drift_select choices_too_large byte-equal to the CLI | "+Text(tooMany)+" | "+manyCli);
    // SF3: requires_incomplete with missing, full envelope byte-equal to the CLI (W1, the door's host, and D1 both gone).
    const string w1="60f91daf-3dd7-4283-a86d-24137b73f3da-00001001", d1="60f91daf-3dd7-4283-a86d-24137b73f3da-00002001";
    var coupled=Node(snapshotText).AsObject();
    var gone=new[]{"b3c223e3-23ef-a05c-2345-ab547eb5c0a5","5f83f664-4e98-113a-3e1b-38f4a4fe7143"};
    foreach(var element in coupled["elements"]!.AsArray().ToArray()) if(gone.Contains((string?)element!["Id"] ?? "")) coupled["elements"]!.AsArray().Remove(element);
    foreach(var source in coupled["sources"]!.AsArray().ToArray()) if(gone.Contains((string?)source!["id"] ?? "")) coupled["sources"]!.AsArray().Remove(source);
    var coupledText=coupled.ToJsonString();
    var incomplete=SelectCall(Mirror(),Choices(d1,"restore"),snapshot:coupledText);
    var incompleteCli=CliSelect(Input("select-coupled-snapshot.json",coupledText),Mirror().ToJsonString(),Choices(d1,"restore").ToJsonString(),"incomplete");
    using(var incompleteDoc=JsonDocument.Parse(Text(incomplete)))
        Check(IsError(incomplete) && Code(incomplete)=="drift_select_requires_incomplete" && incompleteDoc.RootElement.GetProperty("proposal_id").GetString()==d1
            && incompleteDoc.RootElement.GetProperty("missing").EnumerateArray().Select(m=>m.GetString()).SequenceEqual(new[]{w1}) && Text(incomplete)==incompleteCli,
            "mirror_drift_select requires_incomplete envelope (proposal_id, missing) byte-equal to the CLI | "+Text(incomplete)+" | "+incompleteCli);
}

// P2-15 mirror_drift_adopt: confirm-gated write. Without confirm:true it returns the plan only and writes nothing; with
// confirm:true it runs the CLI mirror-drift-adopt engine (MirrorDriftAdoptTool) and its text equals that verb's stdout byte
// for byte, success and refusal alike. selection is the canonical selection JSON text (mirror_drift_select's text).
// Listed with permission label write.
{
    var adoptFixtures=Path.Combine(root,"tests","mirror-journal","fixtures","drift");
    var committedText=File.ReadAllText(Path.Combine(adoptFixtures,"committed.json"));
    var snapshotText=File.ReadAllText(Path.Combine(adoptFixtures,"snapshot.json"));
    var committed=MirrorCommittedModel.Parse(committedText);
    const string adoptDoc="60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string w2="60f91daf-3dd7-4283-a86d-24137b73f3da-00001002";
    var adoptRoot=Path.Combine(cliDir,"adopt"); Directory.CreateDirectory(adoptRoot);
    string NewStore(string name,Project project) { var path=Path.Combine(adoptRoot,name); new ProjectStore(path).Initialize(project); return path; }
    string Listing(string store)=>string.Join(",",Directory.GetFileSystemEntries(store).Select(entry=>Path.GetFileName(entry)+":"+(Path.GetFileName(entry)=="lock"?"lock"+new FileInfo(entry).Length.ToString(System.Globalization.CultureInfo.InvariantCulture):File.Exists(entry)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry))):"dir")).Order(StringComparer.Ordinal));
    var drifted=Node(snapshotText).AsObject();
    foreach(var element in drifted["elements"]!.AsArray().ToArray())
        if((string?)element!["Id"]=="db98eeb5-3a66-7972-29a5-494ec813984a") { element!["GeometryMetres"]![0]=5.05; element!["GeometryMetres"]![2]=5.05; }
    var driftedText=drifted.ToJsonString();
    JsonObject Mirror() => new JsonObject {
        ["document"]=new JsonObject{["path"]=@"C:\Repo\.bim\native-runs\p2-15\copy.rvt",["unique_id"]=adoptDoc},
        ["core_project_id"]=committed.Project.Id.ToString("D"),
        ["core_revision"]=3,
        ["map"]=Node(committedText)["map"]!.DeepClone(),
        ["wall_types"]=new JsonArray(new JsonObject{["thickness_metres"]=0.2,["type"]="Generic - 200mm"}),
        ["door_symbol"]="Single-Flush: 0915 x 2134mm",
    };
    JsonObject Choices(string action)=>new JsonObject{["choices"]=new JsonArray(new JsonObject{["proposal_id"]=w2,["action"]=action})};
    var mcpStore=NewStore("mcp-store",committed.Project);
    var cliStore=NewStore("cli-store",committed.Project);
    string SelectText(string action) {
        var response=ToolCall("mirror_drift_select",new JsonObject{["store_path"]=mcpStore,["snapshot"]=Node(driftedText),["mirror"]=Mirror(),["choices"]=Choices(action)});
        if(IsError(response)) throw new Exception("mirror_drift_select fixture failed: "+Text(response));
        return Text(response);
    }
    var adoptSelection=SelectText("adopt");
    var restoreSelection=SelectText("restore");
    var adoptPath=Input("adopt-selection.json",adoptSelection);
    var restorePath=Input("adopt-restore-selection.json",restoreSelection);
    JsonObject Adopt(string store,string selection,JsonNode? confirm)=>ToolCall("mirror_drift_adopt",confirm is null
        ? new JsonObject{["store_path"]=store,["selection"]=selection}
        : new JsonObject{["store_path"]=store,["selection"]=selection,["confirm"]=confirm});
    string Code(JsonObject response)=>JsonDocument.Parse(Text(response)).RootElement.TryGetProperty("code",out var code) ? code.GetString() ?? "" : "";
    string Status(JsonObject response)=>JsonDocument.Parse(Text(response)).RootElement.GetProperty("status").GetString() ?? "";

    // Listing: name, schema, permission label write, write annotations.
    var adoptTool=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="mirror_drift_adopt");
    Check(adoptTool is not null,"mirror_drift_adopt is listed");
    var adoptDescription=adoptTool!["description"]!.GetValue<string>();
    Check(adoptDescription.Contains("mirror-drift-adopt",StringComparison.Ordinal) && adoptDescription.Contains("confirm",StringComparison.Ordinal),"mirror_drift_adopt description names the CLI verb and the confirm gate | "+adoptDescription);
    Check(Required("mirror_drift_adopt")=="store_path,selection","mirror_drift_adopt requires store_path and selection (confirm optional)");
    var adoptSchema=adoptTool["inputSchema"]!;
    Check(adoptSchema["properties"]!["store_path"]!["type"]!.GetValue<string>()=="string" && adoptSchema["properties"]!["selection"]!["type"]!.GetValue<string>()=="string"
        && adoptSchema["properties"]!["confirm"]!["type"]!.GetValue<string>()=="boolean" && adoptSchema["properties"]!.AsObject().Count==3 && adoptSchema["additionalProperties"]!.GetValue<bool>()==false,
        "mirror_drift_adopt schema: store_path string, selection string, confirm boolean, nothing else | "+adoptSchema.ToJsonString());
    Check(adoptTool["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="write" && adoptTool["permission_level"]==null,"mirror_drift_adopt lists _meta bim-agentic-os/permission_level write | "+adoptTool.ToJsonString());
    var adoptAnnotations=adoptTool["annotations"]?.AsObject();
    Check(adoptAnnotations is not null && adoptAnnotations.Count==3 && adoptAnnotations["readOnlyHint"]?.GetValue<bool>()==false && adoptAnnotations["destructiveHint"]?.GetValue<bool>()==true && adoptAnnotations["idempotentHint"]?.GetValue<bool>()==true,
        "mirror_drift_adopt annotations: readOnlyHint false, destructiveHint true (updates and deletes elements), idempotentHint true (replay by key)");

    // Expected plan, straight from Core.
    var bindings=new RevitBindings(new RevitDocumentRef(@"C:\Repo\.bim\native-runs\p2-15\copy.rvt",adoptDoc),new Dictionary<Guid,string>(),new[]{new RevitWallTypeBinding(0.2,"Generic - 200mm")},"Single-Flush: 0915 x 2134mm",null);
    var parsedSnapshot=MirrorDrift.ParseSnapshot(driftedText);
    var resolution=MirrorDriftResolve.Plan(MirrorDrift.Compare(parsedSnapshot,committed,(IReadOnlyList<MirrorJournalEntry>?)null,new MirrorDriftOptions()),parsedSnapshot,committed,bindings);
    var selection=MirrorDriftSelect.Select(resolution,new[]{new MirrorDriftChoice(w2,"adopt")});
    Check(selection.ToJson()==adoptSelection,"fixture: the mirror_drift_select text is the Core selection");
    var adoptKey=MirrorDriftAdoptCommit.Key(selection);
    var expectedPlan=JsonSerializer.Serialize(new { status="planned", applied=false, outcome="would_commit", revision=4, key=adoptKey, plan=MirrorDriftAdoptCommit.Plan(new ProjectStore(mcpStore).Read(),selection).Plan });

    // Without confirm (absent or false): the plan only, nothing written (no file added, changed or removed; no lock file).
    var before=Listing(mcpStore);
    var planned=Adopt(mcpStore,adoptSelection,null);
    Check(!IsError(planned) && HasType(planned) && Text(planned)==expectedPlan,"mirror_drift_adopt without confirm returns the plan only | "+Text(planned)+" | "+expectedPlan);
    Check(Listing(mcpStore)==before,"mirror_drift_adopt without confirm writes nothing to the store");
    var plannedFalse=Adopt(mcpStore,adoptSelection,false);
    Check(!IsError(plannedFalse) && Text(plannedFalse)==expectedPlan && Listing(mcpStore)==before,"mirror_drift_adopt with confirm:false returns the same plan and writes nothing");

    // With confirm:true: the CLI engine, byte-equal to its stdout; both stores end byte-identical.
    var committedCall=Adopt(mcpStore,adoptSelection,true);
    var committedCli=Cli("mirror-drift-adopt",cliStore,adoptPath);
    Check(!IsError(committedCall) && Text(committedCall)==committedCli,"mirror_drift_adopt confirm:true text byte-equal to the CLI mirror-drift-adopt stdout | "+Text(committedCall)+" | "+committedCli);
    Check(Text(committedCall)==JsonSerializer.Serialize(new { status="succeeded", outcome="committed", revision=4, key=adoptKey }),"mirror_drift_adopt confirm:true commits revision 4 under the adopt key");
    Check(new ProjectStore(mcpStore).Read().Revision==4 && Listing(mcpStore)==Listing(cliStore),"mirror_drift_adopt confirm:true leaves the store byte-identical to the CLI's | "+Listing(mcpStore)+" | "+Listing(cliStore));

    // Replay: confirmed again is replayed (byte-equal to the CLI); the plan says would_replay; nothing written.
    var afterCommit=Listing(mcpStore);
    var replay=Adopt(mcpStore,adoptSelection,true);
    var replayCli=Cli("mirror-drift-adopt",cliStore,adoptPath);
    Check(!IsError(replay) && Text(replay)==replayCli && Status(replay)=="succeeded" && JsonDocument.Parse(Text(replay)).RootElement.GetProperty("outcome").GetString()=="replayed","mirror_drift_adopt replay is replayed, byte-equal to the CLI | "+Text(replay));
    var replayPlan=Adopt(mcpStore,adoptSelection,null);
    Check(!IsError(replayPlan) && Text(replayPlan)==JsonSerializer.Serialize(new { status="planned", applied=false, outcome="would_replay", revision=4, key=adoptKey, plan=(ExecutionPlan?)null }) && Listing(mcpStore)==afterCommit,
        "mirror_drift_adopt plan after the commit is would_replay with no plan, nothing written | "+Text(replayPlan));

    // Restore-only selection: nothing_to_adopt in both modes, the store never opened.
    var nothingPlan=Adopt(mcpStore,restoreSelection,null);
    Check(!IsError(nothingPlan) && Text(nothingPlan)==JsonSerializer.Serialize(new { status="planned", applied=false, outcome="nothing_to_adopt", revision=3, key=(string?)null, plan=(ExecutionPlan?)null }),"mirror_drift_adopt plan of a restore-only selection is nothing_to_adopt | "+Text(nothingPlan));
    var nothing=Adopt(Path.Combine(adoptRoot,"never"),restoreSelection,true);
    Check(!IsError(nothing) && Text(nothing)==Cli("mirror-drift-adopt",Path.Combine(adoptRoot,"never"),restorePath) && !Directory.Exists(Path.Combine(adoptRoot,"never")),"mirror_drift_adopt confirmed restore-only selection is nothing_to_adopt, byte-equal to the CLI, store never opened | "+Text(nothing));

    // Typed refusals, the same text in plan and confirm mode, byte-equal to the CLI, store unchanged.
    void Refused(string label,string code,string store,string selectionText,string selectionPath) {
        var listing=Directory.Exists(store) ? Listing(store) : null;
        var planCall=Adopt(store,selectionText,null);
        var confirmed=Adopt(store,selectionText,true);
        var cli=Cli("mirror-drift-adopt",store,selectionPath);
        Check(IsError(planCall) && IsError(confirmed) && Status(confirmed)=="failed" && Code(confirmed)==code && Text(planCall)==Text(confirmed) && Text(confirmed)==cli,
            "mirror_drift_adopt "+label+" is "+code+" in both modes, byte-equal to the CLI | "+Text(planCall)+" | "+Text(confirmed)+" | "+cli);
        Check(Directory.Exists(store) ? Listing(store)==listing : listing is null && !Directory.Exists(store),"mirror_drift_adopt "+label+" writes nothing");
    }
    Refused("stale revision","drift_adopt_stale_revision",NewStore("stale-store",committed.Project with { Revision=2 }),adoptSelection,adoptPath);
    Refused("another project","drift_adopt_project_mismatch",NewStore("other-store",committed.Project with { Id=Guid.Parse("b0000000-0000-4000-8000-000000000000") }),adoptSelection,adoptPath);
    var spaced=adoptSelection+" ";
    Refused("non-canonical selection","drift_adopt_selection_invalid",NewStore("spaced-store",committed.Project),spaced,Input("adopt-spaced.json",spaced));
    Refused("non-JSON selection","drift_adopt_selection_invalid",NewStore("garbage-store",committed.Project),"not json",Input("adopt-garbage.json","not json"));
    Refused("missing store","input_not_found",Path.Combine(adoptRoot,"missing-store"),adoptSelection,adoptPath);
    var malformedStore=Path.Combine(adoptRoot,"malformed-store"); Directory.CreateDirectory(malformedStore);
    File.WriteAllText(Path.Combine(malformedStore,"project.json"),"{\"Project\":null,\"Receipts\":{}}");
    Refused("store with a null project","drift_adopt_store_invalid",malformedStore,adoptSelection,adoptPath);
    // Review: the duplicated read-only store reader agrees with Commit's, and Core's operation checks reach both modes.
    string RawStore(string name,byte[] bytes) { var path=Path.Combine(adoptRoot,name); Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path,"project.json"),bytes); return path; }
    Refused("store whose project.json is not UTF-8","drift_adopt_store_invalid",RawStore("not-utf8-store",new byte[]{0x7B,0xC3,0x28,0xFF,0xFE,0x00,0x7D}),adoptSelection,adoptPath);
    Refused("store whose project.json is not JSON","drift_adopt_store_invalid",RawStore("not-json-store",System.Text.Encoding.UTF8.GetBytes("not json")),adoptSelection,adoptPath);
    var utf16State=JsonSerializer.Serialize(new ProjectStore.State(committed.Project with { Revision=2 },new Dictionary<string,string>()));
    Refused("UTF-16 (BOM) store at another revision","drift_adopt_stale_revision",RawStore("utf16-store",System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(utf16State)).ToArray()),adoptSelection,adoptPath);
    var existing=new ProjectStore(cliStore).Read().Elements.First(element=>element.Id!=selection.Operations[0].Element.Id);
    var createExisting=(selection with { Operations=new[]{ new Operation("create",existing) } }).ToJson();
    Refused("create of an element the head has","drift_adopt_selection_invalid",NewStore("create-store",committed.Project),createExisting,Input("adopt-create-existing.json",createExisting));
    var danglingElement=selection.Operations[0].Element with { Dependencies=new[]{ Guid.Parse("d0000000-0000-4000-8000-0000000000ff") } };
    var invalidIr=(selection with { Operations=new[]{ new Operation(selection.Operations[0].Action,danglingElement) } }).ToJson();
    Refused("selection whose adopted model fails validation","drift_adopt_invalid_ir",NewStore("invalid-ir-store",committed.Project),invalidIr,Input("adopt-invalid-ir.json",invalidIr));

    // Review: the plan takes no store lock and touches no entry (names, bytes and LastWriteTimeUtc); a held lock blocks only the commit.
    var lockedStore=NewStore("locked-store",committed.Project);
    string Times(string store)=>string.Join(",",Directory.GetFileSystemEntries(store).Order(StringComparer.Ordinal).Select(entry=>Path.GetFileName(entry)+"@"+File.GetLastWriteTimeUtc(entry).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    var lockedListing=Listing(lockedStore); var lockedTimes=Times(lockedStore);
    using(var held=new FileStream(Path.Combine(lockedStore,"lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
        var lockedPlan=Adopt(lockedStore,adoptSelection,null);
        Check(!IsError(lockedPlan) && Text(lockedPlan)==expectedPlan,"mirror_drift_adopt plan while the store lock is held is still would_commit (no lock taken) | "+Text(lockedPlan));
        Check(Listing(lockedStore)==lockedListing && Times(lockedStore)==lockedTimes,"mirror_drift_adopt plan changes no store entry (names, bytes, LastWriteTimeUtc)");
        var lockedCommit=Adopt(lockedStore,adoptSelection,true);
        Check(IsError(lockedCommit) && Code(lockedCommit)=="drift_adopt_store_unavailable" && Listing(lockedStore)==lockedListing,"mirror_drift_adopt confirm:true while the store lock is held is drift_adopt_store_unavailable, nothing written | "+Text(lockedCommit));
    }
    var unlocked=Adopt(lockedStore,adoptSelection,true);
    Check(!IsError(unlocked) && Text(unlocked)==committedCli,"mirror_drift_adopt confirm:true after the lock is released commits as the CLI did | "+Text(unlocked));
    var timesAfter=Times(mcpStore);
    Adopt(mcpStore,adoptSelection,null);
    Check(Times(mcpStore)==timesAfter,"mirror_drift_adopt would_replay plan changes no LastWriteTimeUtc");
    var blank=Adopt("   ",adoptSelection,null);
    Check(IsError(blank) && Text(blank)==JsonSerializer.Serialize(new { status="failed", code="input_path_invalid", detail="store" }) && Text(Adopt("   ",adoptSelection,true))==Cli("mirror-drift-adopt","   ",adoptPath),"mirror_drift_adopt blank store_path is input_path_invalid (store) in both modes, byte-equal to the CLI | "+Text(blank));
    var surrogate=Call("{\"jsonrpc\":\"2.0\",\"id\":41,\"method\":\"tools/call\",\"params\":{\"name\":\"mirror_drift_adopt\",\"arguments\":{\"store_path\":"+JsonSerializer.Serialize(mcpStore)+",\"selection\":\"\\ud800\",\"confirm\":true}}}");
    Check(IsError(surrogate) && Code(surrogate)=="drift_adopt_selection_invalid" && new ProjectStore(mcpStore).Read().Revision==4,"mirror_drift_adopt selection that is not text (lone surrogate) is drift_adopt_selection_invalid | "+Text(surrogate));

    // P2-17 MirrorDriftAdoptCommit.Preview: Core's read-only entry behind the plan mode. Core's own store reader, Commit's
    // order and refusals; no lock taken or created; no store entry written or touched (names, bytes, LastWriteTimeUtc).
    // The store directory's own LastWriteTimeUtc is in the snapshot too: a lock file created then deleted moves it.
    string DirTime(string store)=>"dir@"+Directory.GetLastWriteTimeUtc(store).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    string Stamped(string store) {
        var stamp=new DateTime(2001,2,3,4,5,6,DateTimeKind.Utc);
        foreach(var file in Directory.GetFiles(store)) File.SetLastWriteTimeUtc(file,stamp);
        Directory.SetLastWriteTimeUtc(store,stamp);
        return Listing(store)+"|"+Times(store)+"|"+DirTime(store);
    }
    string Unchanged(string store)=>Listing(store)+"|"+Times(store)+"|"+DirTime(store);
    string PlanJson(ExecutionPlan? plan)=>JsonSerializer.Serialize(plan);
    string Refusal(Func<MirrorDriftAdoptResult> call) { try { var result=call(); return "no refusal: "+result.Outcome; } catch(MirrorDriftAdoptException refusal) { return refusal.Message; } }
    var previewSource=NewStore("preview-source",committed.Project);
    var corePlan=MirrorDriftAdoptCommit.Plan(new ProjectStore(previewSource).Read(),selection).Plan;
    var bareStore=Path.Combine(adoptRoot,"preview-bare-store"); Directory.CreateDirectory(bareStore);
    File.Copy(Path.Combine(previewSource,"project.json"),Path.Combine(bareStore,"project.json"));
    var bareBefore=Stamped(bareStore);
    var preview=MirrorDriftAdoptCommit.Preview(bareStore,selection);
    Check(preview.Outcome=="would_commit" && preview.Outcome==MirrorDriftAdoptOutcomes.WouldCommit && preview.Revision==4 && preview.Key==adoptKey && preview.Head?.Revision==3 && PlanJson(preview.Plan)==PlanJson(corePlan),
        "MirrorDriftAdoptCommit.Preview is would_commit: revision 4, the adopt key, the head as read and MirrorDriftAdoptCommit.Plan's plan | "+preview.Outcome+" "+preview.Revision+" "+preview.Key+" "+PlanJson(preview.Plan));
    Check(Unchanged(bareStore)==bareBefore && !File.Exists(Path.Combine(bareStore,"lock")),"MirrorDriftAdoptCommit.Preview creates no lock file and changes no store entry (names, bytes, LastWriteTimeUtc) | "+Unchanged(bareStore)+" | "+bareBefore);
    var heldStore=NewStore("preview-held-store",committed.Project);
    var heldBefore=Stamped(heldStore);
    using(var held=new FileStream(Path.Combine(heldStore,"lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
        var heldPreview=MirrorDriftAdoptCommit.Preview(heldStore,selection);
        Check(heldPreview.Outcome==MirrorDriftAdoptOutcomes.WouldCommit && heldPreview.Revision==4 && PlanJson(heldPreview.Plan)==PlanJson(corePlan),"MirrorDriftAdoptCommit.Preview while the store lock is held is would_commit (no lock taken) | "+heldPreview.Outcome);
    }
    Check(Unchanged(heldStore)==heldBefore,"MirrorDriftAdoptCommit.Preview under a held lock changes no store entry (names, bytes, LastWriteTimeUtc)");
    // A fresh copy of the committed MCP store (head 4 with the adopt receipt), so the shared mcpStore is not re-stamped.
    var replayStore=Path.Combine(adoptRoot,"preview-replay-store"); Directory.CreateDirectory(replayStore);
    foreach(var file in Directory.GetFiles(mcpStore)) File.Copy(file,Path.Combine(replayStore,Path.GetFileName(file)));
    var replayBefore=Stamped(replayStore);
    var replayPreview=MirrorDriftAdoptCommit.Preview(replayStore,selection);
    Check(replayPreview.Outcome==MirrorDriftAdoptOutcomes.WouldReplay && replayPreview.Outcome=="would_replay" && replayPreview.Revision==4 && replayPreview.Key==adoptKey && replayPreview.Head?.Revision==4 && replayPreview.Plan is null && Unchanged(replayStore)==replayBefore,
        "MirrorDriftAdoptCommit.Preview after the commit is would_replay at the current head, no plan, nothing touched | "+replayPreview.Outcome+" "+replayPreview.Revision);
    var restoreCore=MirrorDriftSelect.Select(resolution,new[]{new MirrorDriftChoice(w2,"restore")});
    Check(restoreCore.ToJson()==restoreSelection,"fixture: the restore mirror_drift_select text is the Core selection");
    var neverPreview=Path.Combine(adoptRoot,"preview-never");
    var nothingPreview=MirrorDriftAdoptCommit.Preview(neverPreview,restoreCore);
    Check(nothingPreview.Outcome==MirrorDriftAdoptOutcomes.NothingToAdopt && nothingPreview.Revision==3 && nothingPreview.Key is null && nothingPreview.Head is null && nothingPreview.Plan is null && !Directory.Exists(neverPreview),
        "MirrorDriftAdoptCommit.Preview of a restore-only selection is nothing_to_adopt, store never opened");
    void SameRefusal(string label,string code,string store) {
        var stamp=Stamped(store);
        var previewed=Refusal(()=>MirrorDriftAdoptCommit.Preview(store,selection));
        Check(Unchanged(store)==stamp,"MirrorDriftAdoptCommit.Preview refusal ("+label+") changes no store entry");
        var commitRefusal=Refusal(()=>MirrorDriftAdoptCommit.Commit(store,selection));
        Check(previewed.StartsWith(code+" ",StringComparison.Ordinal) && previewed==commitRefusal,"MirrorDriftAdoptCommit.Preview refusal ("+label+") is "+code+", the same text as Commit's | "+previewed+" | "+commitRefusal);
    }
    SameRefusal("stale revision","drift_adopt_stale_revision",NewStore("preview-stale-store",committed.Project with { Revision=2 }));
    SameRefusal("another project","drift_adopt_project_mismatch",NewStore("preview-other-store",committed.Project with { Id=Guid.Parse("b0000000-0000-4000-8000-000000000000") }));
    SameRefusal("not UTF-8","drift_adopt_store_invalid",RawStore("preview-not-utf8-store",new byte[]{0x7B,0xC3,0x28,0xFF,0xFE,0x00,0x7D}));
    SameRefusal("not JSON","drift_adopt_store_invalid",RawStore("preview-not-json-store",System.Text.Encoding.UTF8.GetBytes("not json")));
    var editedHead=committed.Project with { Elements=committed.Project.Elements.Select((element,index)=>index==0?element with { Provenance=new Provenance("edited","edited") }:element).ToArray() };
    Check(editedHead.Revision==3 && Compiler.Hash(editedHead)!=Compiler.Hash(committed.Project),"fixture: the edited head keeps revision 3 with another hash");
    SameRefusal("stale by hash (same revision, edited element)","drift_adopt_stale_revision",NewStore("preview-stale-hash-store",editedHead));
    SameRefusal("null receipts","drift_adopt_store_invalid",RawStore("preview-null-receipts-store",System.Text.Encoding.UTF8.GetBytes("{\"Project\":"+JsonSerializer.Serialize(committed.Project)+",\"Receipts\":null}")));
    SameRefusal("null project","drift_adopt_store_invalid",RawStore("preview-null-project-store",System.Text.Encoding.UTF8.GetBytes("{\"Project\":null,\"Receipts\":{}}")));
    // Selection before store: a selection with null parts is refused before the (missing) store is looked at.
    var nullPartsSelection=selection with { Omitted=null! };
    var previewMissingStore=Path.Combine(adoptRoot,"preview-missing-store");
    var nullPartsPreview=Refusal(()=>MirrorDriftAdoptCommit.Preview(previewMissingStore,nullPartsSelection));
    var nullPartsCommit=Refusal(()=>MirrorDriftAdoptCommit.Commit(previewMissingStore,nullPartsSelection));
    Check(nullPartsPreview.StartsWith("drift_adopt_selection_invalid ",StringComparison.Ordinal) && nullPartsPreview==nullPartsCommit && !Directory.Exists(previewMissingStore),
        "MirrorDriftAdoptCommit.Preview of a null-parts selection against a missing store is drift_adopt_selection_invalid (selection before store), the same text as Commit's | "+nullPartsPreview+" | "+nullPartsCommit);
    var noProject=Path.Combine(adoptRoot,"preview-empty-store"); Directory.CreateDirectory(noProject);
    var noProjectRefusal=Refusal(()=>MirrorDriftAdoptCommit.Preview(noProject,selection));
    Check(noProjectRefusal=="drift_adopt_store_invalid the store has no project.json" && noProjectRefusal==Refusal(()=>MirrorDriftAdoptCommit.Commit(noProject,selection)) && Directory.GetFileSystemEntries(noProject).Length==0,
        "MirrorDriftAdoptCommit.Preview of a store without project.json is drift_adopt_store_invalid as Commit, nothing created | "+noProjectRefusal);
    // The MCP plan mode is that entry: no second store reader in interfaces/mcp.
    var adoptToolSource=File.ReadAllText(Path.Combine(root,"interfaces","mcp","MirrorDriftAdoptTool.cs"));
    Check(adoptToolSource.Contains("MirrorDriftAdoptCommit.Preview(",StringComparison.Ordinal) && !adoptToolSource.Contains("ProjectStore.State",StringComparison.Ordinal) && !adoptToolSource.Contains("project.json is not",StringComparison.Ordinal)
        && !adoptToolSource.Contains("ReadBytes(",StringComparison.Ordinal) && !adoptToolSource.Contains("Deserialize<",StringComparison.Ordinal),
        "mirror_drift_adopt plan mode calls MirrorDriftAdoptCommit.Preview and has no store reader of its own");

    // Argument shape: -32602 before anything runs.
    Check(ErrorCode(ToolCall("mirror_drift_adopt",new JsonObject{["store_path"]=mcpStore}))==-32602,"mirror_drift_adopt without selection returns -32602");
    Check(ErrorCode(ToolCall("mirror_drift_adopt",new JsonObject{["store_path"]=mcpStore,["selection"]=Node(adoptSelection)}))==-32602,"mirror_drift_adopt with an object selection returns -32602 (selection is the JSON text)");
    Check(ErrorCode(ToolCall("mirror_drift_adopt",new JsonObject{["store_path"]=mcpStore,["selection"]=adoptSelection,["confirm"]="true"}))==-32602,"mirror_drift_adopt with a non-boolean confirm returns -32602");
    Check(ErrorCode(ToolCall("mirror_drift_adopt",new JsonObject{["store_path"]=mcpStore,["selection"]=adoptSelection,["confirm"]=1}))==-32602,"mirror_drift_adopt with a numeric confirm returns -32602");
    Check(ErrorCode(ToolCall("mirror_drift_adopt",new JsonObject{["store_path"]=mcpStore,["selection"]=adoptSelection,["force"]=true}))==-32602,"mirror_drift_adopt with an unknown argument returns -32602");
    Check(ErrorCode(ToolCall("mirror_drift_adopt",new JsonObject{["store_path"]=mcpStore,["selection"]=adoptSelection,["confirm"]=null}))==-32602,"mirror_drift_adopt with confirm:null returns -32602");
    Check(ErrorCode(ToolCall("mirror_drift_adopt",new JsonObject{["store_path"]=5,["selection"]=adoptSelection,["confirm"]=true}))==-32602,"mirror_drift_adopt with a non-string store_path returns -32602");
    Check(new ProjectStore(mcpStore).Read().Revision==4 && Listing(mcpStore)==afterCommit,"mirror_drift_adopt refusals and rejected calls left the committed store unchanged");
}

// P5-09 skills_lock_verify: the read-only verify of the CLI skills-lock verb (one engine, SkillLockTool). The tool text
// equals the CLI stdout byte for byte, success and refusal alike; it reports skill_lock_drift / skill_lock_missing per
// pack and never writes the lock or anything beside it.
{
    var lockRoot=Path.Combine(cliDir,"skills-lock"); var skillsDir=Path.Combine(lockRoot,"skills");
    const string alpha="bim.test.alpha", beta="bim.test.beta";
    void Pack(string id) { Directory.CreateDirectory(Path.Combine(skillsDir,id)); File.WriteAllBytes(Path.Combine(skillsDir,id,"skill.json"),new System.Text.UTF8Encoding(false).GetBytes("{\"manifest_version\":1,\"id\":\""+id+"\",\"version\":\"2.1.0\",\"kind\":\"qa\",\"description\":\"MCP lock pack\",\"priority\":10}\n")); }
    Pack(alpha); Pack(beta);
    var lockFile=Path.Combine(lockRoot,"skills.lock.json");
    File.WriteAllBytes(lockFile,SkillLockFile.Serialize(SkillLocking.Pin(skillsDir,new[]{alpha,beta})));
    var lockBytes=File.ReadAllBytes(lockFile);
    string[] Listing()=>Directory.GetFileSystemEntries(lockRoot).Select(e=>Path.GetFileName(e)).Order(StringComparer.Ordinal).ToArray();
    var listing=Listing();
    JsonObject Verify(string skills,string lockPath)=>ToolCall("skills_lock_verify",new JsonObject{["skills_path"]=skills,["lock_path"]=lockPath});
    JsonElement Doc(JsonObject response)=>JsonDocument.Parse(Text(response)).RootElement.Clone();
    string State(JsonElement doc,string id)=>doc.GetProperty("packs").EnumerateArray().Single(p=>p.GetProperty("id").GetString()==id).GetProperty("state").GetString()!;

    var lockTool=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="skills_lock_verify");
    Check(lockTool is not null && lockTool["description"]!.GetValue<string>().Contains("read-only",StringComparison.OrdinalIgnoreCase) && lockTool["description"]!.GetValue<string>().Contains("skills-lock verify",StringComparison.Ordinal),"skills_lock_verify is listed, read-only, naming the CLI verb");
    var ok=Verify(skillsDir,lockFile);
    Check(!IsError(ok) && HasType(ok) && Doc(ok).GetProperty("status").GetString()=="succeeded" && Doc(ok).GetProperty("verified").GetBoolean() && State(Doc(ok),alpha)=="ok" && State(Doc(ok),beta)=="ok","skills_lock_verify succeeds on an unchanged directory | "+Text(ok));
    Check(Text(ok)==Cli("skills-lock","verify",skillsDir,lockFile),"skills_lock_verify text byte-equal to the CLI skills-lock verify stdout");

    File.WriteAllText(Path.Combine(skillsDir,beta,"extra.txt"),"edited");
    var drift=Verify(skillsDir,lockFile);
    Check(IsError(drift) && Doc(drift).GetProperty("code").GetString()=="skill_lock_drift" && State(Doc(drift),beta)=="skill_lock_drift" && State(Doc(drift),alpha)=="ok","skills_lock_verify reports skill_lock_drift for the edited pack | "+Text(drift));
    Check(Text(drift)==Cli("skills-lock","verify",skillsDir,lockFile),"skills_lock_verify drift report byte-equal to the CLI stdout");
    Directory.Delete(Path.Combine(skillsDir,alpha),true);
    var gone=Verify(skillsDir,lockFile);
    Check(IsError(gone) && State(Doc(gone),alpha)=="skill_lock_missing" && State(Doc(gone),beta)=="skill_lock_drift" && Text(gone)==Cli("skills-lock","verify",skillsDir,lockFile),"skills_lock_verify reports skill_lock_missing per pack, byte-equal to the CLI | "+Text(gone));
    Check(File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(lockBytes) && Listing().SequenceEqual(listing),"skills_lock_verify never writes the lock or anything beside it");

    var none=Path.Combine(lockRoot,"none.lock.json");
    var missing=Verify(skillsDir,none);
    Check(IsError(missing) && Text(missing)=="""{"status":"failed","code":"lock_missing","subject":"none.lock.json","detail":"lock file not found","mode":"verify","verified":false,"packs":[],"issues":[{"code":"lock_missing","subject":"none.lock.json","detail":"lock file not found"}]}""" && Text(missing)==Cli("skills-lock","verify",skillsDir,none) && !File.Exists(none),"skills_lock_verify of a missing lock is lock_missing, byte-equal to the CLI, and creates nothing");
    var emptyPath=Verify(skillsDir,"");
    Check(IsError(emptyPath) && Text(emptyPath)=="""{"status":"failed","code":"skills_lock_path_invalid","detail":"lock"}""","skills_lock_verify empty lock path is skills_lock_path_invalid");
    var nulPath=Verify("a\0b",lockFile);
    Check(IsError(nulPath) && Text(nulPath)=="""{"status":"failed","code":"skills_lock_path_invalid","detail":"skills"}""","skills_lock_verify skills path with NUL is skills_lock_path_invalid");
    Check(IsError(Verify(skillsDir,new string('x',5000))) && Doc(Verify(skillsDir,new string('x',5000))).GetProperty("code").GetString()=="skills_lock_path_invalid","skills_lock_verify refuses a path over the length bound");
    Check(ErrorCode(ToolCall("skills_lock_verify",new JsonObject{["skills_path"]=skillsDir}))==-32602,"skills_lock_verify without lock_path returns -32602");
    Check(ErrorCode(ToolCall("skills_lock_verify",new JsonObject{["skills_path"]=skillsDir,["lock_path"]=lockFile,["update"]=true}))==-32602,"skills_lock_verify takes no update argument (-32602)");
    Check(ErrorCode(ToolCall("skills_lock_verify",new JsonObject{["skills_path"]=skillsDir,["lock_path"]=1}))==-32602,"skills_lock_verify with a non-string lock_path returns -32602");
    foreach(var writer in new[]{"skills_lock_pin","skills_lock_update"}) {
        var refused=ToolCall(writer,new JsonObject());
        Check(IsError(refused) && Text(refused)=="""{"status":"failed","code":"unknown_tool"}""","MCP exposes no lock writer: "+writer+" is unknown_tool");
    }
}

// P10-12 audit_verify: the read-only MCP face of the P10-11 CLI verb audit-verify (one engine, AuditVerifyTool). The tool
// text equals the CLI stdout and AuditVerifier.VerifyBytes(...).ToJson() (or AuditSummary.ToJson with summary) byte for
// byte on the same log; isError exactly when the report has a finding; typed refusals; it never writes the log, an anchor
// or anything else; tools/list labels it permission level read.
{
    var auditRoot=Path.Combine(cliDir,"audit-verify"); Directory.CreateDirectory(auditRoot);
    var auditClock=new DateTimeOffset(2026,11,10,9,30,0,TimeSpan.Zero);
    var auditLogPath=Path.Combine(auditRoot,"audit.jsonl");
    var auditLog=AuditLog.Open(auditLogPath,()=>{ auditClock=auditClock.AddHours(13); return auditClock; });
    auditLog.Append("alice","op-1","wall.create","C:/models/a.rvt",true,"granted");
    auditLog.Append("bob","op-2","door.delete","C:/models/a.rvt",false,"read-only actor");
    auditLog.Append("alice","op-3","wall.create","C:/models/b.rvt",true,"granted");
    var auditBytes=File.ReadAllBytes(auditLogPath);
    var auditText=new System.Text.UTF8Encoding(false).GetString(auditBytes);
    var auditLines=auditText.Split('\n')[..^1];
    Check(auditLines.Length==3 && AuditLogFormat.VerifyBytes(auditBytes).Ok,"audit_verify fixture: a real AuditLog with three verified entries");
    var auditHead=AuditLogFormat.VerifyBytes(auditBytes).Head;
    string AuditFile(string name,string content) { var path=Path.Combine(auditRoot,name); File.WriteAllBytes(path,new System.Text.UTF8Encoding(false).GetBytes(content)); return path; }
    string AnchorJson(long seq,string hash)=>"{\"kind\":\"audit-anchor\",\"version\":1,\"sequence\":"+seq.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"hash\":\""+hash+"\"}\n";
    var headAnchor=AuditFile("head.json",AnchorJson(auditHead.Seq,auditHead.Hash));
    var truncatedLog=AuditFile("truncated.jsonl",auditLines[0]+"\n"+auditLines[1]+"\n");
    var editedLog=AuditFile("edited.jsonl",auditText.Replace("\"op-1\"","\"op-9\"",StringComparison.Ordinal));
    var badJsonAnchor=AuditFile("bad.json","{");
    var wrongKindAnchor=AuditFile("kind.json","{\"kind\":\"other\",\"version\":1,\"sequence\":3,\"hash\":\""+auditHead.Hash+"\"}\n");
    var auditDir=Path.Combine(auditRoot,"a-directory"); Directory.CreateDirectory(auditDir);
    var missingLog=Path.Combine(auditRoot,"missing.jsonl");
    var missingAnchor=Path.Combine(auditRoot,"missing-head.json");
    string[] AuditListing()=>Directory.GetFileSystemEntries(auditRoot).Select(e=>Path.GetFileName(e)!).Order(StringComparer.Ordinal).ToArray();
    var auditListing=AuditListing();
    var fileBytes=AuditListing().Where(name=>File.Exists(Path.Combine(auditRoot,name))).ToDictionary(name=>name,name=>File.ReadAllBytes(Path.Combine(auditRoot,name)));
    JsonObject Audit(JsonObject arguments)=>ToolCall("audit_verify",arguments);
    JsonObject Log(string path)=>new JsonObject{["log_path"]=path};
    string AuditCode(JsonObject response) { using var doc=JsonDocument.Parse(Text(response)); return doc.RootElement.GetProperty("code").GetString()!; }
    string Engine(byte[] bytes,AuditVerifyOptions? options=null,bool summary=false) { var report=AuditVerifier.VerifyBytes(bytes,options); return summary?AuditSummary.ToJson(report.VerifiedEntries):report.ToJson(); }

    var auditTool=tools.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="audit_verify");
    Check(auditTool is not null && auditTool["description"]!.GetValue<string>().Contains("read-only",StringComparison.OrdinalIgnoreCase) && auditTool["description"]!.GetValue<string>().Contains("audit-verify",StringComparison.Ordinal),"audit_verify is listed, read-only, naming the CLI verb");
    Check(auditTool!["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="read" && auditTool["permission_level"]==null,"audit_verify lists _meta bim-agentic-os/permission_level read");
    Check(auditTool["annotations"]?["readOnlyHint"]?.GetValue<bool>()==true && auditTool["annotations"]!.AsObject().Count==1,"audit_verify annotations: readOnlyHint true only");
    var auditSchema=auditTool["inputSchema"]!;
    Check(string.Join(",",auditSchema["properties"]!.AsObject().Select(p=>p.Key))=="log_path,anchor_path,actors,summary"
        && auditSchema["properties"]!["log_path"]!["type"]!.GetValue<string>()=="string" && auditSchema["properties"]!["anchor_path"]!["type"]!.GetValue<string>()=="string"
        && auditSchema["properties"]!["actors"]!["type"]!.GetValue<string>()=="array" && auditSchema["properties"]!["actors"]!["items"]!["type"]!.GetValue<string>()=="string"
        && auditSchema["properties"]!["summary"]!["type"]!.GetValue<string>()=="boolean" && auditSchema["additionalProperties"]!.GetValue<bool>()==false
        && !auditSchema["properties"]!.AsObject().ContainsKey("write_anchor"),"audit_verify schema: log_path required; anchor_path, actors, summary optional; no write_anchor");

    var clean=Audit(Log(auditLogPath));
    Check(!IsError(clean) && HasType(clean) && AuditCode(clean)=="ok" && Text(clean)==Engine(auditBytes),"audit_verify clean log: ok report byte-equal to AuditVerifier.VerifyBytes(...).ToJson() | "+Text(clean));
    Check(Text(clean)==Cli("audit-verify",auditLogPath),"audit_verify clean report byte-equal to the CLI audit-verify stdout");
    var summary=Audit(new JsonObject{["log_path"]=auditLogPath,["summary"]=true});
    Check(!IsError(summary) && Text(summary)==Engine(auditBytes,null,true) && Text(summary)==Cli("audit-verify",auditLogPath,"--summary"),"audit_verify summary byte-equal to AuditSummary.ToJson and the CLI --summary | "+Text(summary));
    var notSummary=Audit(new JsonObject{["log_path"]=auditLogPath,["summary"]=false});
    Check(!IsError(notSummary) && Text(notSummary)==Text(clean),"audit_verify summary false is the report");
    var anchored=Audit(new JsonObject{["log_path"]=auditLogPath,["anchor_path"]=headAnchor});
    Check(!IsError(anchored) && Text(anchored)==Engine(auditBytes,new AuditVerifyOptions(null,auditHead)) && Text(anchored)==Cli("audit-verify",auditLogPath,"--anchor",headAnchor),"audit_verify with the head anchor: ok, byte-equal to the engine and the CLI --anchor");
    var actorsOk=Audit(new JsonObject{["log_path"]=auditLogPath,["actors"]=new JsonArray("bob","alice")});
    Check(!IsError(actorsOk) && Text(actorsOk)==Engine(auditBytes,new AuditVerifyOptions(new[]{"alice","bob"})) && Text(actorsOk)==Cli("audit-verify",auditLogPath,"--actors","alice,bob"),"audit_verify with every actor known: ok, byte-equal to the CLI --actors");
    var actorUnknown=Audit(new JsonObject{["log_path"]=auditLogPath,["actors"]=new JsonArray("alice")});
    Check(IsError(actorUnknown) && AuditCode(actorUnknown)=="audit_actor_unknown" && Text(actorUnknown)==Engine(auditBytes,new AuditVerifyOptions(new[]{"alice"})) && Text(actorUnknown)==Cli("audit-verify",auditLogPath,"--actors","alice"),"audit_verify reports audit_actor_unknown (isError), byte-equal to the engine and the CLI | "+Text(actorUnknown));
    var truncated=Audit(new JsonObject{["log_path"]=truncatedLog,["anchor_path"]=headAnchor});
    Check(IsError(truncated) && AuditCode(truncated)=="audit_truncated" && Text(truncated)==Engine(File.ReadAllBytes(truncatedLog),new AuditVerifyOptions(null,auditHead)) && Text(truncated)==Cli("audit-verify",truncatedLog,"--anchor",headAnchor),"audit_verify detects tail truncation against the anchor, byte-equal to the engine and the CLI | "+Text(truncated));
    var truncatedSummary=Audit(new JsonObject{["log_path"]=truncatedLog,["anchor_path"]=headAnchor,["summary"]=true});
    Check(IsError(truncatedSummary) && Text(truncatedSummary)==Cli("audit-verify",truncatedLog,"--anchor",headAnchor,"--summary"),"audit_verify summary of a failing log is isError and byte-equal to the CLI");
    var edited=Audit(Log(editedLog));
    Check(IsError(edited) && AuditCode(edited)=="audit_entry_edited" && Text(edited)==Engine(File.ReadAllBytes(editedLog)) && Text(edited)==Cli("audit-verify",editedLog),"audit_verify detects an edited entry, byte-equal to the engine and the CLI | "+Text(edited));

    // Refusals: {"status":"failed","code"[,"detail"]}, isError, byte-equal to the CLI wherever the CLI can express the input.
    var noLog=Audit(Log(missingLog));
    Check(IsError(noLog) && Text(noLog)=="""{"status":"failed","code":"input_not_found"}""" && Text(noLog)==Cli("audit-verify",missingLog) && !File.Exists(missingLog),"audit_verify of a missing log is input_not_found, byte-equal to the CLI, creates nothing");
    var dirLog=Audit(Log(auditDir));
    Check(IsError(dirLog) && Text(dirLog)=="""{"status":"failed","code":"input_not_regular_file"}""" && Text(dirLog)==Cli("audit-verify",auditDir),"audit_verify of a directory is input_not_regular_file, byte-equal to the CLI");
    var noAnchor=Audit(new JsonObject{["log_path"]=auditLogPath,["anchor_path"]=missingAnchor});
    Check(IsError(noAnchor) && Text(noAnchor)=="""{"status":"failed","code":"input_not_found"}""" && Text(noAnchor)==Cli("audit-verify",auditLogPath,"--anchor",missingAnchor) && !File.Exists(missingAnchor),"audit_verify with a missing anchor is input_not_found and never creates the anchor");
    var badAnchor=Audit(new JsonObject{["log_path"]=auditLogPath,["anchor_path"]=badJsonAnchor});
    Check(IsError(badAnchor) && Text(badAnchor)=="""{"status":"failed","code":"audit_verify_anchor_invalid","detail":"not JSON"}""" && Text(badAnchor)==Cli("audit-verify",auditLogPath,"--anchor",badJsonAnchor),"audit_verify with a non-JSON anchor is audit_verify_anchor_invalid, byte-equal to the CLI");
    var kindAnchor=Audit(new JsonObject{["log_path"]=auditLogPath,["anchor_path"]=wrongKindAnchor});
    Check(IsError(kindAnchor) && AuditCode(kindAnchor)=="audit_verify_anchor_invalid" && Text(kindAnchor)==Cli("audit-verify",auditLogPath,"--anchor",wrongKindAnchor),"audit_verify with a wrong-kind anchor is audit_verify_anchor_invalid, byte-equal to the CLI | "+Text(kindAnchor));
    void ActorsRefused(string actorsJson,string label) {
        var refused=Audit(new JsonObject{["log_path"]=auditLogPath,["actors"]=Node(actorsJson)});
        Check(IsError(refused) && AuditCode(refused)=="audit_verify_actors_invalid","audit_verify refuses "+label+" as audit_verify_actors_invalid | "+Text(refused));
    }
    ActorsRefused("[]","an empty actor list");
    ActorsRefused("[\"\"]","an empty actor");
    ActorsRefused("[\"alice\",1]","a non-string actor");
    ActorsRefused("[\""+new string('a',PermissionPolicy.MaxActorChars+1)+"\"]","an over-long actor");
    ActorsRefused("[\"al\\u0001ice\"]","a control character");
    Check(IsError(Audit(Log(""))) && Text(Audit(Log("")))=="""{"status":"failed","code":"audit_verify_path_invalid","detail":"log"}""","audit_verify empty log_path is audit_verify_path_invalid (log)");
    Check(Text(Audit(Log("a\0b")))=="""{"status":"failed","code":"audit_verify_path_invalid","detail":"log"}""","audit_verify log_path with NUL is audit_verify_path_invalid (log)");
    Check(Text(Audit(Log(new string('x',5000))))=="""{"status":"failed","code":"audit_verify_path_invalid","detail":"log"}""","audit_verify refuses a log_path over the length bound");
    Check(Text(Audit(new JsonObject{["log_path"]=auditLogPath,["anchor_path"]=""}))=="""{"status":"failed","code":"audit_verify_path_invalid","detail":"anchor"}""","audit_verify empty anchor_path is audit_verify_path_invalid (anchor)");
    Check(ErrorCode(Audit(new JsonObject()))==-32602,"audit_verify without log_path returns -32602");
    Check(ErrorCode(Audit(new JsonObject{["log_path"]=1}))==-32602,"audit_verify with a non-string log_path returns -32602");
    Check(ErrorCode(Audit(new JsonObject{["log_path"]=auditLogPath,["summary"]="yes"}))==-32602,"audit_verify with a non-boolean summary returns -32602");
    Check(ErrorCode(Audit(new JsonObject{["log_path"]=auditLogPath,["actors"]="alice"}))==-32602,"audit_verify with a non-array actors returns -32602");
    Check(ErrorCode(Audit(new JsonObject{["log_path"]=auditLogPath,["anchor_path"]=1}))==-32602,"audit_verify with a non-string anchor_path returns -32602");
    Check(ErrorCode(Audit(new JsonObject{["log_path"]=auditLogPath,["write_anchor"]=Path.Combine(auditRoot,"new-head.json")}))==-32602 && !File.Exists(Path.Combine(auditRoot,"new-head.json")),"audit_verify takes no write_anchor argument (-32602) and writes no anchor");
    Check(AuditListing().SequenceEqual(auditListing) && fileBytes.All(pair=>File.ReadAllBytes(Path.Combine(auditRoot,pair.Key)).AsSpan().SequenceEqual(pair.Value)),"audit_verify never writes the log, an anchor or anything beside them");
    // Review SF1: a lone escaped surrogate is valid JSON but not text; it is refused by the engine, never a crash.
    JsonObject Raw(string arguments)=>Call("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"audit_verify\",\"arguments\":"+arguments+"}}");
    var surrogateLog=Raw("""{"log_path":"\ud800"}""");
    Check(IsError(surrogateLog) && Text(surrogateLog)=="""{"status":"failed","code":"audit_verify_path_invalid","detail":"log"}""","audit_verify log_path with a lone surrogate is audit_verify_path_invalid (log) | "+Text(surrogateLog));
    var surrogateAnchor=Raw("{\"log_path\":"+JsonSerializer.Serialize(auditLogPath)+",\"anchor_path\":\"\\ud800\"}");
    Check(IsError(surrogateAnchor) && Text(surrogateAnchor)=="""{"status":"failed","code":"audit_verify_path_invalid","detail":"anchor"}""","audit_verify anchor_path with a lone surrogate is audit_verify_path_invalid (anchor) | "+Text(surrogateAnchor));
    var surrogateActor=Raw("{\"log_path\":"+JsonSerializer.Serialize(auditLogPath)+",\"actors\":[\"\\ud800\"]}");
    Check(IsError(surrogateActor) && AuditCode(surrogateActor)=="audit_verify_actors_invalid","audit_verify actor with a lone surrogate is audit_verify_actors_invalid | "+Text(surrogateActor));
    // Review SF3: optional parameters present as null are wrongly typed (-32602), never treated as absent.
    foreach(var optional in new[]{"anchor_path","summary","actors"})
        Check(ErrorCode(Audit(new JsonObject{["log_path"]=auditLogPath,[optional]=null}))==-32602,"audit_verify with "+optional+" null returns -32602");
    // Review SF2: bounded-input refusals through the tool, byte-equal to the CLI.
    var overCapAnchor=Path.Combine(auditRoot,"over-cap.json");
    var overCap=new byte[Bim.Cli.BoundedInput.MaxBytes+1]; Array.Fill(overCap,(byte)' ');
    File.WriteAllBytes(overCapAnchor,overCap);
    var tooLarge=Audit(new JsonObject{["log_path"]=auditLogPath,["anchor_path"]=overCapAnchor});
    Check(IsError(tooLarge) && Text(tooLarge)=="""{"status":"failed","code":"input_too_large"}""" && Text(tooLarge)==Cli("audit-verify",auditLogPath,"--anchor",overCapAnchor),"audit_verify with an anchor of 8 MiB + 1 byte is input_too_large, byte-equal to the CLI");
    if(!OperatingSystem.IsWindows()) {
        bool Link(string at,string target) {
            try { File.CreateSymbolicLink(at,target); return true; }
            catch(Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
        }
        var logLink=Path.Combine(auditRoot,"link.jsonl");
        if(Link(logLink,auditLogPath)) {
            var linked=Audit(Log(logLink));
            Check(IsError(linked) && Text(linked)=="""{"status":"failed","code":"input_link_rejected"}""" && Text(linked)==Cli("audit-verify",logLink),"audit_verify log_path through a symbolic link is input_link_rejected, byte-equal to the CLI");
        }
        var anchorLink=Path.Combine(auditRoot,"anchor-link.json");
        if(Link(anchorLink,headAnchor)) {
            var linked=Audit(new JsonObject{["log_path"]=auditLogPath,["anchor_path"]=anchorLink});
            Check(IsError(linked) && Text(linked)=="""{"status":"failed","code":"input_link_rejected"}""" && Text(linked)==Cli("audit-verify",auditLogPath,"--anchor",anchorLink),"audit_verify anchor_path through a symbolic link is input_link_rejected, byte-equal to the CLI");
        }
    }
    // Review NIT1/NIT2: the generated schema honours ParameterSpec.Required, and the hand-written audit_verify schema
    // lists exactly the spec parameters and requires exactly the Required ones.
    var generatedAudit=Node(McpServer.GeneratedSchemaJson("audit_verify"));
    Check(string.Join(",",generatedAudit["required"]!.AsArray().Select(x=>x!.GetValue<string>()))=="log_path" && generatedAudit["properties"]!.AsObject().Count==4,"Schema() lists every parameter but requires only Required ones");
    Check(string.Join(",",McpServer.CoreParameterNames("audit_verify",true))==Required("audit_verify") && string.Join(",",McpServer.CoreParameterNames("audit_verify",false))==string.Join(",",auditSchema["properties"]!.AsObject().Select(p=>p.Key)),"audit_verify hand-written schema matches its parameter specs (properties and required)");
    foreach(var core in new[]{"diff","validate","room","plan","mirror_drift","mirror_drift_select","skills_lock_verify"})
        Check(string.Join(",",McpServer.CoreParameterNames(core,true))==Required(core) && McpServer.CoreParameterNames(core,true).SequenceEqual(McpServer.CoreParameterNames(core,false)),core+" requires all of its parameters, as listed");
    var auditWriter=ToolCall("audit_write_anchor",new JsonObject());
    Check(IsError(auditWriter) && Text(auditWriter)=="""{"status":"failed","code":"unknown_tool"}""","MCP exposes no anchor writer");
}

// P3-14 diff_intent: the read-only MCP face of the P3-12 CLI verb diff-intent, built on the very same code path
// (interfaces/cli/DiffIntentCommand.cs and its P3-13 shared helper interfaces/cli/IntentPlan.cs, linked into the server,
// no copied plan logic). The tool text equals the CLI stdout byte for byte on the same project and intent files, for
// plans and for every typed diff_intent_* / input_* / library refusal; isError exactly when the CLI exits 1; it never
// writes the project, the intent or anything beside them; tools/list labels it permission level read.
{
    var diRoot=Path.Combine(cliDir,"diff-intent"); Directory.CreateDirectory(diRoot);
    var diExit=Environment.ExitCode;
    var diProjectId=Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    var diSource=new Provenance("mcp diff_intent fixture","specified");
    IntentRoom DiRoom(string key,double x,double y,double width,double depth,string level="l0")=>new(key,"Room "+key,level,"a",x,y,width,depth);
    IntentOpening DiWindow(string key,IntentHost host,double offset,double width=1.2)=>new(key,IntentOpeningKind.Window,host,offset,width,1.2,0.9);
    IntentOpening DiDoor(string key,IntentHost host,double offset)=>new(key,IntentOpeningKind.Door,host,offset,0.9,2.1,0);
    LayoutIntent DiIntent(IntentRoom[] rooms,IntentOpening[] openings)=>new(LayoutIntent.CurrentVersion,diSource,new[]{new IntentLevel("l0","Ground",0)},new[]{new IntentWallType("a","A",0.2,2.8)},rooms,openings);
    var diBase=DiIntent(new[]{DiRoom("r0",0,0,4,3),DiRoom("r1",4,0,3,3)},
        new[]{DiDoor("d0",new IntentSharedWall("r0","r1"),0.8),DiWindow("w0",new IntentRoomSide("r0",IntentSide.South),1.0),DiWindow("w1",new IntentRoomSide("r1",IntentSide.North),0.8,1.0)});
    var diStep1=diBase with { Openings=new[]{DiDoor("d0",new IntentSharedWall("r0","r1"),0.8),DiWindow("w0",new IntentRoomSide("r0",IntentSide.South),1.5),DiWindow("zz",new IntentRoomSide("r0",IntentSide.West),0.8)} };
    var diStep2=diStep1 with { Rooms=new[]{DiRoom("r0",0,0,4,3),DiRoom("r1",4,0,3,3),DiRoom("r2",7,0,4,3)} };
    var diCounter=0;
    // Every input in its own directory, so "nothing written" covers the whole directory around it.
    string DiFile(string name,string content) {
        var directory=Path.Combine(diRoot,"in-"+(++diCounter).ToString(System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,name); File.WriteAllText(path,content); return path;
    }
    string DiProject(string name,Project project)=>DiFile(name+".project.json",JsonSerializer.Serialize(project));
    string DiIntentFile(string name,LayoutIntent intent)=>DiFile(name+".intent.json",IntentJson.Serialize(intent));
    string DiListing() {
        var entries=Directory.GetFileSystemEntries(diRoot,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(e=>Path.GetRelativePath(diRoot,e)+(File.Exists(e)?":"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(e)))+":"+File.GetLastWriteTimeUtc(e).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture):"/"));
        return string.Join("\n",entries);
    }
    JsonObject DiCall(string projectPath,string intentPath)=>ToolCall("diff_intent",new JsonObject{["project_path"]=projectPath,["intent_path"]=intentPath});
    string DiLibrary(Project head,LayoutIntent intent) {
        var set=IntentEdits.FromStored(head,intent);
        return JsonSerializer.Serialize(IncrementalDiff.Diff(head,set.Edits,createOrder:IncrementalDiff.OrderOf(set.After)).Plan);
    }

    var diList=Call(Request(2,"tools/list",null))["result"]!["tools"]!.AsArray();
    var diTool=diList.SingleOrDefault(t=>t!["name"]!.GetValue<string>()=="diff_intent");
    Check(diTool is not null && diTool["description"]!.GetValue<string>().Contains("read-only",StringComparison.OrdinalIgnoreCase) && diTool["description"]!.GetValue<string>().Contains("diff-intent",StringComparison.Ordinal),"diff_intent is listed, read-only, naming the CLI verb diff-intent");
    Check(diTool!["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>()=="read" && diTool["annotations"]?["readOnlyHint"]?.GetValue<bool>()==true && diTool["annotations"]!.AsObject().Count==1,"diff_intent lists _meta permission_level read and annotations readOnlyHint true only");
    Check(string.Join(",",diTool["inputSchema"]!["required"]!.AsArray().Select(x=>x!.GetValue<string>()))=="project_path,intent_path" && diTool["inputSchema"]!["properties"]!.AsObject().Count==2 && diTool["inputSchema"]!["additionalProperties"]!.GetValue<bool>()==false,"diff_intent schema: exactly project_path and intent_path, both required");

    // 1. Plans: byte-equal to the CLI stdout and to the library plan, on creates last, creates before survivors, no change,
    //    an empty head and a head out of compile order; nothing written; the process exit code and Console.Out untouched.
    var diHead0=IntentCompiler.Compile(diProjectId,0,diBase);
    var diHead1=IntentEdits.FromStored(diHead0,diStep1).After;
    var diReversed=diHead0 with { Elements=Enumerable.Reverse(diHead0.Elements).ToArray() };
    var diEmpty=new Project(diProjectId,0,Array.Empty<Element>());
    foreach(var (label,head,intent) in new (string,Project,LayoutIntent)[]{
        ("step 1 move, delete, create last",diHead0,diStep1),("step 2 creates before survivors",diHead1,diStep2),("no change",diHead1,diStep1),
        ("an empty head",diEmpty,diBase),("a head out of compile order",diReversed,diStep2)}) {
        var projectPath=DiProject("plan",head); var intentFile=DiIntentFile("plan",intent);
        var before=DiListing(); var stdout=Console.Out;
        var response=DiCall(projectPath,intentFile);
        Check(!IsError(response) && HasType(response) && Environment.ExitCode==diExit && ReferenceEquals(Console.Out,stdout),"diff_intent succeeds ("+label+"), leaving the exit code and Console.Out untouched | "+Text(response));
        Check(Text(response)==DiLibrary(head,intent),"diff_intent text is the shared-path library plan ("+label+")");
        Check(Text(response)==Cli("diff-intent","--project",projectPath,"--intent",intentFile),"diff_intent text is byte-equal to the CLI diff-intent stdout ("+label+")");
        Check(DiListing()==before,"diff_intent writes nothing ("+label+")");
    }
    Check(DiLibrary(diReversed,diStep2)!=JsonSerializer.Serialize(Compiler.Diff(diReversed,IntentEdits.FromStored(diReversed,diStep2).After)),"fixture: the reversed head really exercises survivor order (plan differs from the full diff)");

    // 2. Refusals: isError, the exact CLI stdout, nothing written, exit code restored.
    void DiRefused(string label,string projectPath,string intentFile,string? expected=null) {
        var before=DiListing();
        var response=DiCall(projectPath,intentFile);
        var cli=Cli("diff-intent","--project",projectPath,"--intent",intentFile);
        Check(IsError(response) && Text(response)==cli && (expected is null || cli==expected) && Environment.ExitCode==diExit,"diff_intent refuses "+label+" byte-equal to the CLI | "+Text(response)+" / "+cli);
        Check(DiListing()==before,"diff_intent writes nothing when refusing "+label);
    }
    string DiCode(string code)=>JsonSerializer.Serialize(new{status="failed",code});
    var diHeadPath=DiProject("head0",diHead0); var diOkIntent=DiIntentFile("ok",diStep1);
    DiRefused("a malformed intent",diHeadPath,DiFile("bad.intent.json","{"));
    DiRefused("an intent with an unknown member",diHeadPath,DiFile("unknown.intent.json",IntentJson.Serialize(diBase).Replace("\"levels\":","\"levelz\":[],\"levels\":",StringComparison.Ordinal)));
    var diOverlap=DiIntentFile("overlap",diBase with { Rooms=new[]{DiRoom("r0",0,0,4,3),DiRoom("r1",3,0,3,3)},Openings=Array.Empty<IntentOpening>() });
    DiRefused("overlapping rooms (compiler refusal)",diHeadPath,diOverlap);
    Check(Text(DiCall(diHeadPath,diOverlap)).Contains("\"code\":\"diff_intent_intent_refused\"",StringComparison.Ordinal),"diff_intent compiler refusal carries diff_intent_intent_refused");
    var diLevel=diHead0.Elements[0];
    var diDangling=diHead0 with { Elements=diHead0.Elements.Append(new Element(Guid.Parse("00000000-0000-0000-0000-0000000000f1"),"plan",Array.Empty<double>(),new[]{Guid.Parse("00000000-0000-0000-0000-0000000000f2"),diLevel.Id},diSource)).ToArray() };
    DiRefused("a head with a dangling dependency",DiProject("dangling",diDangling),diOkIntent,DiCode("invalid_ir"));
    DiRefused("a head with an empty project id",DiProject("anonymous",new Project(Guid.Empty,0,Array.Empty<Element>())),diOkIntent,DiCode("invalid_project_identity"));
    var diMany=DiIntent(Enumerable.Range(0,205).Select(i=>DiRoom("r"+i.ToString("000",System.Globalization.CultureInfo.InvariantCulture),i*10,0,4,3)).ToArray(),Array.Empty<IntentOpening>());
    DiRefused("an intent over IncrementalDiff.MaxEdits edits",DiProject("empty",diEmpty),DiIntentFile("many",diMany),DiCode(IncrementalDiff.BoundExceeded));
    DiRefused("a project that is not JSON",DiFile("bad.project.json","{"),diOkIntent,DiCode("diff_intent_project_invalid"));
    DiRefused("a missing project",Path.Combine(diRoot,"absent.project.json"),diOkIntent,DiCode("input_not_found"));
    DiRefused("a missing intent",diHeadPath,Path.Combine(diRoot,"absent.intent.json"),DiCode("input_not_found"));
    Check(!File.Exists(Path.Combine(diRoot,"absent.project.json")) && !File.Exists(Path.Combine(diRoot,"absent.intent.json")),"diff_intent creates no missing input");
    var diDirectory=Path.Combine(diRoot,"a-directory.json"); Directory.CreateDirectory(diDirectory);
    DiRefused("a directory as intent",diHeadPath,diDirectory,DiCode("input_not_regular_file"));
    // A flag-shaped path is the CLI usage refusal (the same argument rule), never a crash.
    {
        var response=DiCall("--intent",diOkIntent);
        Check(IsError(response) && Text(response)==DiCode("diff_intent_usage") && Environment.ExitCode==diExit,"diff_intent refuses a flag-shaped project_path as diff_intent_usage | "+Text(response));
    }
    // MCP-only path guard (like audit_verify_path_invalid): blank, NUL, over 4096 characters or rejected by
    // Path.GetFullPath is diff_intent_path_invalid with detail project or intent, checked before the verb runs (project
    // first); the exit code and Console.Out are untouched.
    string DiPathInvalid(string detail)=>"{\"status\":\"failed\",\"code\":\"diff_intent_path_invalid\",\"detail\":\""+detail+"\"}";
    foreach(var (label,bad) in new[]{("empty",""),("blank","   "),("with NUL","a\0b"),("of 5000 characters",new string('x',5000))}) {
        foreach(var (side,projectPath,intentFile) in new[]{("project",bad,diOkIntent),("intent",diHeadPath,bad)}) {
            var stdout=Console.Out; var before=DiListing();
            var response=DiCall(projectPath,intentFile);
            Check(IsError(response) && Text(response)==DiPathInvalid(side) && Environment.ExitCode==diExit && ReferenceEquals(Console.Out,stdout) && DiListing()==before,
                "diff_intent refuses a "+side+"_path "+label+" as diff_intent_path_invalid ("+side+"), exit code and Console.Out untouched | "+Text(response));
        }
    }
    Check(Text(DiCall("",""))==DiPathInvalid("project"),"diff_intent checks project_path before intent_path");
    foreach(var side in new[]{"project","intent"}) {
        var arguments=side=="project"?"{\"project_path\":\"\\ud800\",\"intent_path\":"+JsonSerializer.Serialize(diOkIntent)+"}":"{\"project_path\":"+JsonSerializer.Serialize(diHeadPath)+",\"intent_path\":\"\\ud800\"}";
        var surrogate=Call("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"diff_intent\",\"arguments\":"+arguments+"}}");
        Check(IsError(surrogate) && Text(surrogate)==DiPathInvalid(side) && Environment.ExitCode==diExit,"diff_intent "+side+"_path with a lone surrogate is diff_intent_path_invalid ("+side+"), never a crash | "+Text(surrogate));
    }
    Check(ErrorCode(ToolCall("diff_intent",new JsonObject{["project_path"]=diHeadPath}))==-32602,"diff_intent without intent_path returns -32602");
    Check(ErrorCode(ToolCall("diff_intent",new JsonObject{["project_path"]=diHeadPath,["intent_path"]=1}))==-32602,"diff_intent with a non-string intent_path returns -32602");
    Check(ErrorCode(ToolCall("diff_intent",new JsonObject{["project_path"]=diHeadPath,["intent_path"]=diOkIntent,["store_path"]=diRoot}))==-32602,"diff_intent takes no store_path (-32602): it never commits");
    var diApply=ToolCall("apply_intent",new JsonObject());
    Check(IsError(diApply) && Text(diApply)=="""{"status":"failed","code":"unknown_tool"}""","MCP exposes no apply_intent writer");

    // 3. Static: the tool runs the CLI verb's own code (DiffIntentCommand.Run, which plans only through IntentPlan), linked
    //    rather than copied; it inlines no plan step and has no file, store or commit access of its own.
    // Comments stripped: /* */ blocks first, then // to end of line.
    string DiCodeOf(string text)=>string.Join("\n",System.Text.RegularExpressions.Regex.Replace(text,@"/\*.*?\*/","",System.Text.RegularExpressions.RegexOptions.Singleline).Split('\n').Select(line=>{ var at=line.IndexOf("//",StringComparison.Ordinal); return at<0?line:line[..at]; }));
    Check(DiCodeOf("a /* ProjectStore\n x */ b // ProjectStore\nc")=="a  b \nc","fixture: DiCodeOf strips block and line comments");
    var diToolPath=Path.Combine(root,"interfaces","mcp","DiffIntentTool.cs");
    Check(File.Exists(diToolPath),"interfaces/mcp/DiffIntentTool.cs exists");
    var diToolCode=DiCodeOf(File.ReadAllText(diToolPath));
    Check(diToolCode.Contains("DiffIntentCommand.Run(",StringComparison.Ordinal),"diff_intent runs the CLI verb's shared path DiffIntentCommand.Run");
    var diInlined=new[]{"IntentJson.Parse(","IntentEdits.FromStored(","IncrementalDiff.Diff(","IncrementalDiff.OrderOf(","IntentException","IntentCompiler","Compiler.Diff(","JsonSerializer.Serialize(","File.","FileStream","Directory.","ProjectStore","Commit(","BoundedInput"}.Where(api=>diToolCode.Contains(api,StringComparison.Ordinal)).ToArray();
    Check(diInlined.Length==0,"diff_intent inlines no plan step, serializer or file/store access ["+string.Join(", ",diInlined)+"]");
    var diCommandCode=DiCodeOf(File.ReadAllText(Path.Combine(root,"interfaces","cli","DiffIntentCommand.cs")));
    Check(diCommandCode.Contains("IntentPlan.ParseIntent(",StringComparison.Ordinal) && diCommandCode.Contains("IntentPlan.Build(",StringComparison.Ordinal),"the verb the tool runs plans through the shared IntentPlan helper");
    foreach(var project in new[]{Path.Combine(root,"interfaces","mcp","Bim.Mcp.csproj"),Path.Combine(root,"tests","mcp","Bim.Mcp.Tests.csproj")}) {
        var csproj=File.ReadAllText(project);
        Check(new[]{"cli/IntentPlan.cs","cli/DiffIntentCommand.cs"}.All(file=>csproj.Contains(file,StringComparison.Ordinal)),Path.GetFileName(project)+" links the CLI IntentPlan.cs and DiffIntentCommand.cs (shared source, not a copy)");
    }
    var diOwners=Directory.GetFiles(Path.Combine(root,"interfaces","mcp"),"*.cs").Where(f=>new[]{"IntentEdits.FromStored(","IncrementalDiff.OrderOf(","IntentJson.Parse("}.Any(api=>DiCodeOf(File.ReadAllText(f)).Contains(api,StringComparison.Ordinal))).Select(f=>Path.GetFileName(f)).ToArray();
    Check(diOwners.Length==0,"no interfaces/mcp source builds an intent plan itself ["+string.Join(", ",diOwners)+"]");
    // No MCP source reaches the CLI write verbs (apply-edits' Run/Commit, apply-intent); the linked CLI files are only the
    // read-only diff-intent path. ProjectStore is banned in DiffIntentTool.cs above (McpServer's plan tool and the
    // mirror_drift tools use it by design).
    var diWriters=Directory.GetFiles(Path.Combine(root,"interfaces","mcp"),"*.cs").SelectMany(f=>new[]{"ApplyEditsCommand.Run(","ApplyEditsCommand.Commit(","ApplyIntentCommand"}
        .Where(api=>DiCodeOf(File.ReadAllText(f)).Contains(api,StringComparison.Ordinal)).Select(api=>Path.GetFileName(f)+": "+api)).ToArray();
    Check(diWriters.Length==0,"no interfaces/mcp source calls ApplyEditsCommand.Run/Commit or ApplyIntentCommand ["+string.Join(", ",diWriters)+"]");
    var diMcpProject=File.ReadAllText(Path.Combine(root,"interfaces","mcp","Bim.Mcp.csproj"));
    Check(!diMcpProject.Contains("ApplyIntentCommand.cs",StringComparison.Ordinal),"Bim.Mcp.csproj does not link the apply-intent verb");
}

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
    // A GitHub Actions annotation, so the failing check is visible without the log.
    Console.WriteLine("::error title=mcp-contract::"+exception.Message.Replace("\r"," ").Replace("\n"," "));
    Environment.ExitCode=1;
} finally {
    if(Directory.Exists(storeDir)) Directory.Delete(storeDir,true);
    if(Directory.Exists(cliDir)) Directory.Delete(cliDir,true);
}
