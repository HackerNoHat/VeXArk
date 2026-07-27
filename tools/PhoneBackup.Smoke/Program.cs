using System.Text.Json;
using PhoneBackup.Core;
using PhoneBackup.Desktop;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: PhoneBackup.Smoke <exact-adb-serial>");
    return 2;
}

var adb = new AdbService();
await using var agent = await AgentClient.ConnectAsync(adb, args[0]);
if (!await agent.PairWithApprovalAsync(
        TimeSpan.FromSeconds(30),
        new Progress<string>(Console.WriteLine)))
{
    Console.Error.WriteLine("Desktop pairing was not approved.");
    return 3;
}

using var hello = await agent.HelloAsync();
var capabilities = hello.RootElement.GetProperty("capabilities")
    .EnumerateArray()
    .Select(x => x.GetString())
    .Where(x => x is not null)
    .ToHashSet(StringComparer.Ordinal);
if (!capabilities.Contains("media-export") ||
    !capabilities.Contains("account-inventory"))
{
    Console.Error.WriteLine("Agent does not expose the new capabilities.");
    return 4;
}

var mediaCapability = await agent.GetMediaCapabilityAsync();
var accountBytes = await agent.ExportAccountInventoryAsync();
using var accountDocument = JsonDocument.Parse(accountBytes);
var accountCount = accountDocument.RootElement
    .GetProperty("accounts")
    .GetArrayLength();
if (accountDocument.RootElement.GetProperty("credentialsIncluded").GetBoolean())
{
    Console.Error.WriteLine("Account inventory unexpectedly contains credentials.");
    return 5;
}

var count = 0;
var totalBytes = 0L;
FileEntry? sample = null;
await foreach (var entry in agent.ScanMediaAsync())
{
    count++;
    totalBytes += entry.Size;
    if (sample is null && entry.Size is > 0 and <= 5 * 1024 * 1024)
        sample = entry;
}

var received = 0L;
if (sample is not null)
{
    await using var input = await agent.OpenMediaFileAsync(sample.LinkTarget!);
    var buffer = new byte[256 * 1024];
    while (true)
    {
        var read = await input.ReadAsync(buffer);
        if (read == 0) break;
        received += read;
    }
    if (received != sample.Size)
    {
        Console.Error.WriteLine(
            $"Media stream size mismatch: expected {sample.Size}, received {received}.");
        return 6;
    }
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    paired = true,
    mediaCapability.Images,
    mediaCapability.Videos,
    mediaCount = count,
    totalBytes,
    accountCount,
    sampleBytes = received
}));
return 0;
