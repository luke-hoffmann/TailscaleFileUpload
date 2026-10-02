using OpenCvSharp;
using Xunit;

namespace Taildrop.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void OpenCvNativeLibraryLoads()
    {
        using var mat = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(255));
        Assert.Equal(10, mat.Rows);
    }
}
