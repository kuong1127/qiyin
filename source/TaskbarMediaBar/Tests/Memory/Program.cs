using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TaskbarMediaBar.Services;

if(args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
Directory.CreateDirectory(args[0]);
Environment.SetEnvironmentVariable("TMB_DATA_DIR",args[0]);
var rows=new List<object>();
var clock=Stopwatch.StartNew();
for(int i=0;i<9;i++)
{
    var start=GC.GetTotalAllocatedBytes(true);
    var probe=QqLikeService.Probe(true); // 只读，重复扫描用于相同负载比较。
    using var p=Process.GetCurrentProcess();
    rows.Add(new{iteration=i,elapsedMs=clock.ElapsedMilliseconds,p.WorkingSet64,p.PrivateMemorySize64,
        allocated=GC.GetTotalAllocatedBytes(true)-start,heap=GC.GetTotalMemory(false),probe.Confirmed,known=probe.Liked.HasValue});
}
File.WriteAllText(Path.Combine(args[0],"scans.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
return 0;
