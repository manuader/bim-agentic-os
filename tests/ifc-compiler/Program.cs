using Bim.Core;
using Bim.Ifc;
using System.Text.Json;
var room = new RoomIntent(Guid.Parse("a71c442d-5181-4b11-9663-a4257ac1261f"),5,4,3,.2,-.2,-.2,"m",new("test","approved"));
var before=RoomCompiler.Compile(Guid.Parse("66ef39da-f724-4328-b055-c33e78dbb2ed"),7,room);
var after=RoomCompiler.Compile(before.Id,7,room with { WidthMetres=5.4 });
var packet=IfcCompiler.Export(before,after,Compiler.Diff(before,after),room.RoomId,"resize-400");
if(packet.BeforeJson!=JsonSerializer.Serialize(before)||packet.AfterJson!=JsonSerializer.Serialize(after))throw new Exception("exact core JSON lost");
IfcCompiler.Validate(packet);
void Reject(Action action) {try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("invalid packet accepted");}
Reject(()=>IfcCompiler.Validate(packet with { PlanJson="{}" }));
Reject(()=>IfcCompiler.Validate(packet with { CommittedAfterJson="{}" }));
Reject(()=>IfcCompiler.Validate(packet with { BeforeJson=packet.BeforeJson+" " }));
Reject(()=>IfcCompiler.Export(before,after with {Revision=8},Compiler.Diff(before,after),room.RoomId,"stale"));
Reject(()=>IfcCompiler.Export(before,after,Compiler.Diff(before,after) with { DirtyIds=[] },room.RoomId,"tampered"));
IfcCompiler.Validate(IfcCompiler.Export(before,before,Compiler.Diff(before,before),room.RoomId,"noop"));
var cyclic=after with { Elements=after.Elements.Select(e=>e.Kind=="level"? e with {Dependencies=[e.Id]}:e).ToArray() };
Reject(()=>IfcCompiler.Export(before,cyclic,Compiler.Diff(before,after),room.RoomId,"cyclic"));
var unsupported=after with {Elements=after.Elements.Select(e=>e.Kind=="wall"?e with {Kind="roof"}:e).ToArray()};
Reject(()=>IfcCompiler.Export(before,unsupported,Compiler.Diff(before,unsupported),room.RoomId,"unsupported"));
if(args.Length>0)File.WriteAllText(args[0],JsonSerializer.Serialize(packet));
if(args.Length>1){var custom=room with {WidthMetres=6};var a=RoomCompiler.Compile(before.Id,7,custom);var b=RoomCompiler.Compile(before.Id,7,custom with {WidthMetres=6.4});File.WriteAllText(args[1],JsonSerializer.Serialize(IfcCompiler.Export(a,b,Compiler.Diff(a,b),room.RoomId,"custom-resize")));}
if(args.Length>2){var a=after with {Revision=8};var b=RoomCompiler.Compile(before.Id,8,room with {WidthMetres=5.8});File.WriteAllText(args[2],JsonSerializer.Serialize(IfcCompiler.Export(a,b,Compiler.Diff(a,b),room.RoomId,"resize-next")));}
if(args.Length>3)File.WriteAllText(args[3],JsonSerializer.Serialize(IfcCompiler.Export(before,before,Compiler.Diff(before,before),room.RoomId,"noop")));
Console.WriteLine("10 IFC compiler contract assertions passed");
