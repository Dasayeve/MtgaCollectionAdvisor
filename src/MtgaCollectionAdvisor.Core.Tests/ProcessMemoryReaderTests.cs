using MtgaCollectionAdvisor.Core.Memory;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

public sealed class ProcessMemoryReaderTests
{
    [Fact]
    public void IsScannableMacRegion_Should_RequireReadAndWrite()
    {
        Assert.True(ProcessMemoryReader.IsScannableMacRegion(3));   // VM_PROT_READ | VM_PROT_WRITE
        Assert.True(ProcessMemoryReader.IsScannableMacRegion(7));   // and execute
        Assert.False(ProcessMemoryReader.IsScannableMacRegion(1));  // read-only
        Assert.False(ProcessMemoryReader.IsScannableMacRegion(2));  // write-only
        Assert.False(ProcessMemoryReader.IsScannableMacRegion(0));  // none
    }
}
