```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 2 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4


```
| Method                      | Mean     | Error     | StdDev    | Allocated |
|---------------------------- |---------:|----------:|----------:|----------:|
| FixedDepthSearch20Positions | 9.308 ms | 0.1858 ms | 0.4195 ms |         - |
| Evaluate20000Positions      | 1.012 ms | 0.0201 ms | 0.0506 ms |         - |
