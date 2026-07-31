# .NET Object Sizing at Runtime

## Question
Can we determine actual heap size of a managed object at runtime?

## Answer: No direct API exists

Microsoft explicitly chose not to expose per-object heap size
(dotnet/runtime#24200). The GC manages object layout internally.

## Available approaches

| Method | Accuracy | Cost | Production-safe |
|--------|----------|------|-----------------|
| `Marshal.SizeOf` | Exact for unmanaged structs only | Cheap | Yes |
| `GC.GetTotalMemory(true)` delta | Approximate | Moderate (forces GC) | Risky |
| `Encoding.UTF8.GetByteCount(json)` | Data payload only | Cheap | Yes |
| Reflection graph walker | Approximate | Expensive | Possible |
| Profiler (dotTrace, ANTS) | Exact | N/A | No |

## Recommended for cache sizing

`Encoding.UTF8.GetByteCount(serializedJson)` with a calibrated overhead factor.

Calibration: during first CacheStatistics pass, sample 5-10 entries:
```csharp
long gcBefore = GC.GetTotalMemory(true);
T sample = deserialize(json);
long gcAfter = GC.GetTotalMemory(true);
double factor = (double)(gcAfter - gcBefore) / Encoding.UTF8.GetByteCount(json);
```

Apply factor to all subsequent measurements. Re-calibrate periodically.
Typical factor: 2.0-3.0x depending on object complexity.

## References
- https://github.com/dotnet/runtime/issues/24200
- https://stackoverflow.com/questions/605621/how-to-get-object-size-in-memory
