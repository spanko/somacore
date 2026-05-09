var jobName = ParseJobArg(args) ?? "(none)";
Console.WriteLine($"would run job: {jobName}");
return 0;

static string? ParseJobArg(string[] args)
{
    const string prefix = "--job=";

    foreach (var arg in args)
    {
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            return arg[prefix.Length..];
        }
    }

    return null;
}
