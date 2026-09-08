using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AcDream.App.Diagnostics;
using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Diagnostics;

public sealed class LocalCrashReportWriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("acdream-crash-report-tests-").FullName;

    [Fact]
    public void WrittenReport_PreservesExactVulkanFieldsAndLoadedAssemblyIdentity()
    {
        VulkanCallException failure = ThrownVulkan();
        var notices = new List<string>();
        string path = Assert.IsType<string>(LocalCrashReportWriter.TryWrite(failure, _directory, notify: notices.Add));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        JsonElement entry = Assert.Single(root.GetProperty("exceptions").EnumerateArray());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Environment.ProcessId, root.GetProperty("processId").GetInt32());
        Assert.InRange(DateTimeOffset.UtcNow - root.GetProperty("utc").GetDateTimeOffset(), TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assembly assembly = typeof(LocalCrashReportWriter).Assembly;
        Assert.Equal(assembly.GetName().Version!.ToString(), root.GetProperty("assemblyVersion").GetString());
        Assert.Equal(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            root.GetProperty("informationalVersion").GetString());
        Assert.Equal(assembly.ManifestModule.ModuleVersionId, root.GetProperty("moduleMvid").GetGuid());
        Assert.Equal(typeof(VulkanCallException).FullName, entry.GetProperty("type").GetString());
        Assert.Equal(failure.HResult, entry.GetProperty("hResult").GetInt32());
        Assert.Contains(nameof(ThrownVulkan), entry.GetProperty("stack").GetString());
        JsonElement vk = entry.GetProperty("vulkan");
        Assert.Equal("vkQueueSubmit2 (abandoned frame timeline signal)", vk.GetProperty("operation").GetString());
        Assert.Equal(-4, vk.GetProperty("result").GetInt32());
        Assert.Equal(nameof(Result.ErrorDeviceLost), vk.GetProperty("resultName").GetString());
        Assert.Equal($"Local crash report saved: {path}", Assert.Single(notices));
    }

    [Fact]
    public void NestedAggregate_PreservesParentAndChildOrderWithoutDuplicateFirstChild()
    {
        var failure = new AggregateException("omitted",
            new InvalidOperationException("omitted", new ArgumentException("omitted")),
            new AggregateException("omitted", new VulkanCallException("vkAcquireNextImageKHR", Result.ErrorDeviceLost)));
        using JsonDocument document = Write(failure);
        JsonElement[] nodes = document.RootElement.GetProperty("exceptions").EnumerateArray().ToArray();
        Assert.Equal(5, nodes.Length);
        Assert.Equal(new[] { "root", "aggregate", "inner", "aggregate", "aggregate" },
            nodes.Select(node => node.GetProperty("relation").GetString()));
        Assert.Equal(new int?[] { null, 0, 1, 0, 3 },
            nodes.Select(node => NullableInt(node.GetProperty("parentIndex"))));
        Assert.Equal(new int?[] { null, 0, null, 1, 0 },
            nodes.Select(node => NullableInt(node.GetProperty("aggregateIndex"))));
        Assert.False(document.RootElement.GetProperty("exceptionTreeTruncated").GetBoolean());
        Assert.Equal("vkAcquireNextImageKHR", nodes[4].GetProperty("vulkan").GetProperty("operation").GetString());
    }

    [Fact]
    public void MissingContext_LeavesGpuAndWorldNull()
    {
        using JsonDocument document = Write(new Exception("private"));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("gpu").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("world").ValueKind);
        Assert.False(document.RootElement.GetProperty("contextCaptureFailed").GetBoolean());
    }

    [Fact]
    public void Context_PreservesOnlyProjectedGpuAndCellLocalPosition()
    {
        var gpu = new LocalCrashGpu("AMD device", "cached driver", "1.3", "1.4", 123, 1920, 1080, 4);
        var world = new LocalCrashWorld(0xF4180111, 12.25f, -3.5f, 160, "Falling");
        using JsonDocument document = Write(new Exception(), () => new(gpu, world));
        JsonElement actualGpu = document.RootElement.GetProperty("gpu");
        Assert.Equal("AMD device", actualGpu.GetProperty("deviceName").GetString());
        Assert.Equal("cached driver", actualGpu.GetProperty("driverInfo").GetString());
        Assert.Equal("1.3", actualGpu.GetProperty("instanceApiVersion").GetString());
        Assert.Equal("1.4", actualGpu.GetProperty("deviceApiVersion").GetString());
        Assert.Equal(123u, actualGpu.GetProperty("deviceApiVersionPacked").GetUInt32());
        Assert.Equal(1920, actualGpu.GetProperty("width").GetInt32());
        Assert.Equal(1080, actualGpu.GetProperty("height").GetInt32());
        Assert.Equal(4, actualGpu.GetProperty("sampleCount").GetInt32());
        JsonElement actualWorld = document.RootElement.GetProperty("world");
        Assert.Equal(0xF4180111u, actualWorld.GetProperty("cellId").GetUInt32());
        Assert.Equal(12.25f, actualWorld.GetProperty("x").GetSingle());
        Assert.Equal(-3.5f, actualWorld.GetProperty("y").GetSingle());
        Assert.Equal(160f, actualWorld.GetProperty("z").GetSingle());
        Assert.Equal("Falling", actualWorld.GetProperty("state").GetString());
        Assert.False(actualWorld.GetProperty("nonFiniteCoordinates").GetBoolean());
    }

    [Fact]
    public void NonFiniteCoordinates_AreExplicitlyNullAndDoNotPreventAReport()
    {
        using JsonDocument document = Write(new Exception(), () => new(null,
            new LocalCrashWorld(null, float.NaN, float.PositiveInfinity, float.NegativeInfinity, null)));
        JsonElement world = document.RootElement.GetProperty("world");
        foreach (string name in new[] { "cellId", "x", "y", "z", "state" })
            Assert.Equal(JsonValueKind.Null, world.GetProperty(name).ValueKind);
        Assert.True(world.GetProperty("nonFiniteCoordinates").GetBoolean());
    }

    [Fact]
    public void LargeUnicodeAndWideExceptionTree_AreBoundedBeforeValidJsonIsWritten()
    {
        string huge = string.Concat(Enumerable.Repeat("秘密🌋", 20_000));
        var failure = new AggregateException(Enumerable.Range(0, 1_000)
            .Select(_ => new VulkanCallException(huge, Result.ErrorDeviceLost)));
        var gpu = new LocalCrashGpu(huge, huge, huge, huge, null, null, null, null);
        string path = Assert.IsType<string>(LocalCrashReportWriter.TryWrite(failure, _directory, () => new(gpu, new(null, null, null, null, huge))));
        byte[] bytes = File.ReadAllBytes(path);
        Assert.InRange(bytes.Length, 1, LocalCrashReportWriter.MaxReportBytes);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Assert.Equal(LocalCrashReportWriter.MaxExceptionNodes, root.GetProperty("exceptions").GetArrayLength());
        Assert.True(root.GetProperty("exceptionTreeTruncated").GetBoolean());
        Assert.True(root.GetProperty("exceptions")[0].GetProperty("childrenTruncated").GetBoolean());
        Assert.EndsWith("[truncated]", root.GetProperty("gpu").GetProperty("deviceName").GetString());
        Assert.InRange(root.GetProperty("gpu").GetProperty("deviceName").GetString()!.Length, 1, 512);
        Assert.EndsWith("[truncated]", root.GetProperty("exceptions")[1].GetProperty("vulkan").GetProperty("operation").GetString());
        Assert.DoesNotContain("\uFFFD", document.RootElement.ToString());
    }

    [Fact]
    public void DeepInnerTree_StopsAtEightAndMarksOmittedChildren()
    {
        Exception failure = new Exception();
        for (int i = 0; i < 100; i++)
            failure = new Exception("not serialized", failure);
        using JsonDocument document = Write(failure);
        JsonElement nodes = document.RootElement.GetProperty("exceptions");
        Assert.Equal(LocalCrashReportWriter.MaxExceptionNodes, nodes.GetArrayLength());
        Assert.True(nodes[7].GetProperty("childrenTruncated").GetBoolean());
        Assert.True(document.RootElement.GetProperty("exceptionTreeTruncated").GetBoolean());
    }

    [Fact]
    public void MessagesDataSourcePathsAndInjectedRemoteStack_AreNeverSerialized()
    {
        var failure = new Exception("PRIVATE_MESSAGE C:\\secret\\source.cs");
        failure.Data["PRIVATE_DATA"] = "secret session";
        ExceptionDispatchInfo.SetRemoteStackTrace(failure, "PRIVATE_REMOTE_STACK at Fake in C:\\secret\\injected.cs:line 42");
        try { throw failure; }
        catch (Exception caught)
        {
            string path = Assert.IsType<string>(LocalCrashReportWriter.TryWrite(caught, _directory));
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("PRIVATE_", text);
            Assert.DoesNotContain("secret", text);
            Assert.DoesNotContain(".cs", text);
            using JsonDocument document = JsonDocument.Parse(text);
            Assert.Contains(nameof(MessagesDataSourcePathsAndInjectedRemoteStack_AreNeverSerialized),
                document.RootElement.GetProperty("exceptions")[0].GetProperty("stack").GetString());
        }
    }

    [Fact]
    public void VirtualExceptionTextAccessors_AreNotRead()
    {
        using JsonDocument document = Write(new PoisonTextException());
        Assert.Equal(typeof(PoisonTextException).FullName,
            document.RootElement.GetProperty("exceptions")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void RepeatedWrites_AreUniqueNonOverwritingAndLeaveOnlyCompleteJson()
    {
        string sentinel = Path.Combine(_directory, "existing.json");
        File.WriteAllText(sentinel, "existing report must survive");
        string first = Assert.IsType<string>(LocalCrashReportWriter.TryWrite(new Exception(), _directory));
        byte[] firstBytes = File.ReadAllBytes(first);
        string second = Assert.IsType<string>(LocalCrashReportWriter.TryWrite(new Exception(), _directory));
        Assert.NotEqual(first, second);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));
        Assert.Equal("existing report must survive", File.ReadAllText(sentinel));
        Assert.Equal(3, Directory.GetFiles(_directory).Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        foreach (string path in new[] { first, second })
        {
            Assert.StartsWith("crash-", Path.GetFileName(path));
            Assert.Contains($"-{Environment.ProcessId}-", Path.GetFileName(path));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }
    }

    [Fact]
    public void FailedContextCapture_StillWritesMinimalReportWithoutSecondaryText()
    {
        using JsonDocument document = Write(new Exception(), () => throw new Exception("PRIVATE_CAPTURE"));
        Assert.True(document.RootElement.GetProperty("contextCaptureFailed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("gpu").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("world").ValueKind);
        Assert.DoesNotContain("PRIVATE_CAPTURE", document.RootElement.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrBlockedDestination_DoesNotThrowOrChangeOriginalFailure(bool blockedByFile)
    {
        string destination = "\0";
        if (blockedByFile)
        {
            destination = Path.Combine(_directory, "not-a-directory");
            File.WriteAllText(destination, "preserve");
        }
        var notices = new List<string>();
        var original = new InvalidOperationException("original");
        Exception? propagated = Record.Exception((Action)(() =>
        {
            try { throw original; }
            catch (Exception failure)
            {
                Assert.Null(LocalCrashReportWriter.TryWrite(failure, destination, notify: notices.Add));
                throw;
            }
        }));
        Assert.Same(original, propagated);
        Assert.Equal("Local crash report unavailable.", Assert.Single(notices));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_directory, "*.json"));
        if (blockedByFile)
            Assert.Equal("preserve", File.ReadAllText(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNotification_DoesNotThrowOrChangeOriginalFailure(bool invalidDestination)
    {
        var original = new Exception("original");
        string? path = null;
        int notices = 0;
        Exception? propagated = Record.Exception((Action)(() =>
        {
            try { throw original; }
            catch (Exception failure)
            {
                path = LocalCrashReportWriter.TryWrite(failure, invalidDestination ? "\0" : _directory,
                    notify: _ => { notices++; throw new Exception("PRIVATE_NOTIFICATION"); });
                throw;
            }
        }));
        Assert.Same(original, propagated);
        Assert.Equal(1, notices);
        if (invalidDestination)
            Assert.Null(path);
        else
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Assert.IsType<string>(path)));
            Assert.DoesNotContain("PRIVATE_NOTIFICATION", document.RootElement.ToString());
        }
    }

    private JsonDocument Write(Exception failure, Func<LocalCrashReportContext?>? capture = null) =>
        JsonDocument.Parse(File.ReadAllBytes(Assert.IsType<string>(LocalCrashReportWriter.TryWrite(failure, _directory, capture))));

    private static int? NullableInt(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();

    private static VulkanCallException ThrownVulkan()
    {
        try { throw new VulkanCallException("vkQueueSubmit2 (abandoned frame timeline signal)", Result.ErrorDeviceLost); }
        catch (VulkanCallException failure) { return failure; }
    }

    private sealed class PoisonTextException : Exception
    {
        public override string Message => throw new InvalidOperationException("PRIVATE_MESSAGE");
        public override string StackTrace => throw new InvalidOperationException("PRIVATE_STACK");
        public override IDictionary Data => throw new InvalidOperationException("PRIVATE_DATA");
        public override string ToString() => throw new InvalidOperationException("PRIVATE_TOSTRING");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
