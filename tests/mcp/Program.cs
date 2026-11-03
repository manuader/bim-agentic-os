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
Check(string.Join(",",tools.Select(t=>t!["name"]!.GetValue<string>()))=="diff,validate,room,plan,"+string.Join(",",RevitCommandCatalogue.All.Select(c=>c.ToolName)),"tools/list names exactly diff, validate, room, plan, then one revit_* tool per catalogue command (RevitCommandCatalogue.All, in order)");
Check(RevitCommandCatalogue.V1.Select(c=>c.ToolName).SequenceEqual(new[]{"revit_query_levels","revit_query_wall_types","revit_query_floor_types","revit_query_family_symbols","revit_wall_create","revit_opening_place","revit_floor_create","revit_room_create"}),"catalogue v1 tool names are the eight ADR0009 commands");
Check(tools.All(t=>t!["inputSchema"]!["type"]!.GetValue<string>()=="object" && t!["inputSchema"]!["properties"] is JsonObject && t!["inputSchema"]!["required"] is JsonArray && !string.IsNullOrWhiteSpace(t!["description"]!.GetValue<string>())),"every tool has a JSON Schema object inputSchema and description");
string Required(string tool)=>string.Join(",",tools.Single(t=>t!["name"]!.GetValue<string>()==tool)!["inputSchema"]!["required"]!.AsArray().Select(x=>x!.GetValue<string>()));
Check(Required("diff")=="before,after" && Required("validate")=="project" && Required("room")=="project_guid,revision,intent" && Required("plan")=="store_path,desired","tool schemas require the CLI inputs");

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
    Check(annotations?["readOnlyHint"]?.GetValue<bool>()==!spec.Writes,spec.ToolName+" annotations.readOnlyHint is "+(!spec.Writes));
    if(spec.Writes) {
        var destructive=new[]{"element.set_parameter","element.delete","wall.set_profile","material.set","element.change_type"}.Contains(spec.Name);
        Check(annotations!["destructiveHint"]?.GetValue<bool>()==destructive && annotations["idempotentHint"]?.GetValue<bool>()==true,spec.ToolName+" write annotations: destructiveHint "+destructive.ToString().ToLowerInvariant()+" (only element.set_parameter, element.delete, wall.set_profile, material.set and element.change_type change or remove existing elements), idempotentHint true (operation_id replay)");
        var description=tool["description"]!.GetValue<string>();
        Check(PermissionPolicy.RequiredLevel(spec.Name,McpReal,copiesRoots)==PermissionLevel.WriteRealProject
            && description.Contains(".bim/native-runs",StringComparison.Ordinal) && description.Contains(".bim/revit-enabled-roots.txt",StringComparison.Ordinal)
            && description.Contains("no per-document or per-actor check",StringComparison.Ordinal),spec.ToolName+" on a real project needs write-real-project; description states the directory-root write rule");
    }
    else Check(annotations!.Count==1,spec.ToolName+" read annotations carry only readOnlyHint");
}
Check(tools.Where(t=>!t!["name"]!.GetValue<string>().StartsWith(RevitCommandSpec.ToolPrefix,StringComparison.Ordinal)).All(t=>t!["_meta"]==null && t!["permission_level"]==null && t!["annotations"]==null),"core tools carry no Revit permission level or annotations");
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
Check(string.Join(",",Node(syntheticServer.Handle(Request(2,"tools/list",null))!)["result"]!["tools"]!.AsArray().Select(t=>t!["name"]!.GetValue<string>()))=="diff,validate,room,plan,revit_query_levels,revit_wall_create","the tool list follows the injected catalogue");
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
};
foreach(var (tool,makeArgs) in v2Writes) sampleArgs[RevitCommandCatalogue.All.Single(spec=>spec.ToolName==tool).Name]=makeArgs;
Check(RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).All(spec=>sampleArgs.ContainsKey(spec.Name)),"sample arguments cover every v1 and v2 command");
Check(RevitCommandCatalogue.Elements.Select(spec=>spec.Name).SequenceEqual(new[]{"query.elements","query.inspect"}) && RevitCommandCatalogue.Elements.All(spec=>sampleArgs.ContainsKey(spec.Name)),"sample arguments cover the P1-02 element queries");
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
