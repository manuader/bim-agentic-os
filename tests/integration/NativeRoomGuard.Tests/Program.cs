// Removing the request identity/empty-geometry gate must fail these behavioral assertions.
using System.Reflection;
var guard=Assembly.GetExecutingAssembly().GetType("Bim.Revit.NativeRoomGuard");
if(guard==null) throw new Exception("FAIL native Room preflight guard missing");
var validate=guard.GetMethod("ValidateRequest")!;
void Check(string? identity,double[] metres,bool nested,string? expected) {
 try { validate.Invoke(null,[identity,metres,nested]); if(expected!=null)throw new Exception("Unsafe request accepted: "+expected); }
 catch(TargetInvocationException e) { if(e.InnerException?.Message!=expected) throw; }
}
Check("owned-identity",[],false,null);
Check(null,[],false,"room_document_identity_required");
Check(" ",[],false,"room_document_identity_required");
Check("owned",[double.NaN],false,"room_empty_metres_required");
Check("owned",[1],false,"room_empty_metres_required");
Check("owned",[],true,"room_nested_request_rejected");
var normalize=guard.GetMethod("NormalizeMillimetres")!;
double N(double x)=>(double)normalize.Invoke(null,[x])!;
if(N(4999.9999999)!=5000 || N(5000.001)!=5000.001)throw new Exception("FAIL normalization hides a material error or rejects floating noise");
var receipt=guard.GetMethod("ValidateReceipt")!;
try { receipt.Invoke(null,["hash-a","hash-b",2,2]); throw new Exception("FAIL conflicting receipt accepted"); }
catch(TargetInvocationException e) when(e.InnerException?.Message=="room_receipt_conflict") { }
try { receipt.Invoke(null,["hash-a","hash-a",2,3]); throw new Exception("FAIL superseded receipt accepted"); }
catch(TargetInvocationException e) when(e.InnerException?.Message=="room_receipt_conflict") { }
receipt.Invoke(null,["hash-a","hash-a",2,2]);
var snapshot=guard.GetMethod("ValidateCommittedSnapshot");
if(snapshot==null)throw new Exception("FAIL canonical committed snapshot guard missing");
try { snapshot.Invoke(null,["canonical-rev2","corrupt-rev2"]);throw new Exception("FAIL corrupted committed snapshot accepted"); }
catch(TargetInvocationException e) when(e.InnerException?.Message=="room_committed_snapshot_conflict") { }
snapshot.Invoke(null,["canonical-rev2","canonical-rev2"]);
var select=guard.GetMethod("SelectParameterField");
if(select==null)throw new Exception("FAIL native schedulable parameter selection missing");
if((int)select.Invoke(null,[new long[]{100,200,300},200L])! != 1)throw new Exception("FAIL selected a field with the wrong native parameter");
foreach(var candidates in new[]{new long[]{100,300},new long[]{200,200}}) {
 try { select.Invoke(null,[candidates,200L]);throw new Exception("FAIL missing or ambiguous native field accepted"); }
 catch(TargetInvocationException e) when(e.InnerException?.Message=="room_schedulable_parameter_missing_or_ambiguous") { }
}
Console.WriteLine("PASS 15 native Room request/measurement/receipt/schedule guards");
