using Bim.Core;
using System;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
public static class CoreBenchmark
{
    public static void Run()
    {
        foreach(var count in new[]{100,1000,10000}) {
            var p=new Project(Guid.Parse("00000000-0000-0000-0000-000000000001"),0,Enumerable.Range(0,count).Select(i=>new Element(new Guid(i+1,0,0,new byte[8]),"wall",[i,0,i+1,0,3,.2],[],new("synthetic","specified"))).ToArray());
            var next=p with {Elements=p.Elements.Select((e,i)=>i==0?e with {GeometryMetres=[0,0,1.4,0,3,.2]}:e).ToArray()};
            var timings=new double[20];
            for(int i=0;i<timings.Length;i++) {var watch=Stopwatch.StartNew(); var diff=Compiler.Diff(p,next); timings[i]=watch.Elapsed.TotalMilliseconds; if(diff.Operations.Length!=1)throw new Exception("Non-minimal patch");}
            Array.Sort(timings);
            Console.WriteLine(JsonSerializer.Serialize(new {scope="core_diff_only",elements=count,samples=20,first_sample_includes_cold_jit=true,p50_ms=timings[9],p95_ms=timings[18],operations=1,native_delivery_latency_ms=(double?)null,runtime=Environment.Version.ToString(),processors=Environment.ProcessorCount}));
        }
    }
}
