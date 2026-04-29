using System;
using System.IO;
using System.Net.Http;

namespace K8sSidecar.Tests;

public class IsEndOfStreamExceptionTests
{
    [Fact]
    public void DirectEndOfStreamException_ReturnsTrue()
    {
        var ex = new EndOfStreamException("Attempted to read past the end of the stream.");
        Assert.True(ResourceService.IsEndOfStreamException(ex));
    }

    [Fact]
    public void NestedEndOfStreamException_InHttpRequestException_ReturnsTrue()
    {
        var endOfStream = new EndOfStreamException("Attempted to read past the end of the stream.");
        var httpEx = new HttpRequestException("Error while copying content to a stream.", endOfStream);
        Assert.True(ResourceService.IsEndOfStreamException(httpEx));
    }

    [Fact]
    public void NestedEndOfStreamException_InAggregateException_ReturnsTrue()
    {
        // Reproduces the exact exception chain from the bug report:
        // AggregateException -> HttpRequestException -> EndOfStreamException
        var endOfStream = new EndOfStreamException("Attempted to read past the end of the stream.");
        var httpEx = new HttpRequestException("Error while copying content to a stream.", endOfStream);
        var aggEx = new AggregateException("One or more errors occurred.", httpEx);

        Assert.True(ResourceService.IsEndOfStreamException(aggEx));
    }

    [Fact]
    public void UnrelatedAggregateException_ReturnsFalse()
    {
        var aggEx = new AggregateException("One or more errors occurred.",
            new InvalidOperationException("Something else went wrong."));

        Assert.False(ResourceService.IsEndOfStreamException(aggEx));
    }

    [Fact]
    public void UnrelatedException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("Something went wrong.");
        Assert.False(ResourceService.IsEndOfStreamException(ex));
    }

    [Fact]
    public void DeeplyNestedEndOfStreamException_ReturnsTrue()
    {
        var endOfStream = new EndOfStreamException("Attempted to read past the end of the stream.");
        var httpEx = new HttpRequestException("Error while copying content to a stream.", endOfStream);
        var aggInner = new AggregateException("Inner aggregate.", httpEx);
        var aggOuter = new AggregateException("Outer aggregate.", aggInner);

        Assert.True(ResourceService.IsEndOfStreamException(aggOuter));
    }
}
