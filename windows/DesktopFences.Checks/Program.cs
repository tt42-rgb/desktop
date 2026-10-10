using System;
using DesktopFences;

try
{
    var results = ModelChecks.Run();
    foreach (var check in results) Console.WriteLine("PASS " + check);
    Console.WriteLine($"{results.Count} checks passed.");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
