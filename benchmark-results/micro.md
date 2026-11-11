BenchmarkDotNet v0.15.8, .NET 10.0.12, X64 RyuJIT x86-64-v3, Intel Core i7-13620H, Windows 11 (10.0.26200)
Job: warmupCount=3, iterationCount=10

| Method             | Mean        | Error      | StdDev     | Gen0   | Gen1   | Allocated |
|------------------- |------------:|-----------:|-----------:|-------:|-------:|----------:|
| HlcNow             |    39.30 ns |   5.332 ns |   3.527 ns |      - |      - |         - |
| MergeTicketChange  |    38.48 ns |   0.682 ns |   0.451 ns | 0.0025 |      - |      32 B |
| MergeCounterChange |    55.54 ns |  10.347 ns |   6.844 ns | 0.0325 |      - |     408 B |
| SerializeState     |   928.67 ns |  81.017 ns |  53.588 ns | 0.4663 | 0.0086 |    5864 B |
| DeserializeState   | 2,450.35 ns | 486.843 ns | 322.017 ns | 0.1221 |      - |    1576 B |
| SerializeChange    |   413.95 ns |  19.825 ns |  11.798 ns | 0.0482 |      - |     608 B |
| ValidateChange     |    55.28 ns |   2.360 ns |   1.234 ns | 0.0166 |      - |     208 B |
